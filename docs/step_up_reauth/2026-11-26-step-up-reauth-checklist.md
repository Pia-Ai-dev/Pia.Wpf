# Step-up re-authentication and E2EE reset: client checklist

**Status:** In progress
**Owner:** pia-client session (client); pia-srv session (server)
**Written:** 2026-11-26
**Origin:** Pentest items #7902, #7963, #7965, #7966 (ADO, Project Pia); server PRs 5002 (#7964) and 5003 (#7871)

Effort: `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a new surface ·
`L` a week or more, a new subsystem.
Value: `High` user-visible or a real risk closed · `Med` worthwhile, not headline · `Enabler` little
standalone value, unblocks a High.

| Group | Source |
|-------|--------|
| A | #7902 — client after a server-side E2EE reset (independent of the server) |
| B | #7965 / #7966 — confirming protected calls, re-scoped once G1 is answered |

## Decision gates

| Gate | Question | Blocks |
|------|----------|--------|
| G1 | Server keeps the per-call password model (PR 5002/5003) or builds the #7963 grant model? **Answered 2026-11-26: keep the password model (a+)**; add the check to session revoke, passkey remove, team writes; TOTP with the password when MFA is on; password on device revoke too (contract pending from server). | B1–B4 |
| G2 | How do SSO accounts confirm account delete / device revoke under the chosen model? **Answered: SSO account delete stays token-only as an accepted residual; an identity-provider spike decides a browser step-up later.** | B3 |
| G3 | When the server reports E2EE off while this device holds the key: stop sync until the user decides (proposed), never drop the key on the server's word. | A2 |

## A — #7902

- [ ] **A1 Reproduce.** Run the SySS scenario (reset from another session, server says off) on a current build and record the result on #7902. *Deps:* — · *Effort:* XS · *Value:* Enabler
- [ ] **A2 Status while the key is held.** Ask `api/e2ee/status` on a cadence also while the key is held; `false` stops pushes and raises the notice until the user decides. *Deps:* G3 · *Effort:* S · *Value:* High
- [x] **A3 Mark before forgetting.** `RaiseCurrentDeviceRevokedAsync` marks the account E2EE-required before it forgets the key. *Deps:* — · *Effort:* XS · *Value:* High
- [ ] **A4 Unknown status holds plaintext.** First-sync migration and chat sync hold plaintext for an unmarked account while the server status is unknown or `false` after this device held a key. *Deps:* A3 · *Effort:* S · *Value:* High
- [x] **A5 Refuse inbound plaintext.** With the key held, a pulled plaintext settings row or assistant chat is refused, not applied. *Deps:* — · *Effort:* S · *Value:* High
- [ ] **A6 Main-window notice.** A notice naming the cause ("account reset or device removed"), independent of the settings view model, that survives a restart until acknowledged. *Deps:* A2 · *Effort:* M · *Value:* High
- [ ] **A7 Onboarding after reset.** Verify the dead end (new registration auto-activated, no wrap) and fix or rule it out. *Deps:* A1 · *Effort:* S · *Value:* Med

## B — confirming protected calls (re-scope after G1)

- [x] **B1 Sync reset.** Confirmed: the client never calls `DELETE /api/sync/reset`; nothing to wire. *Deps:* — · *Effort:* XS · *Value:* Med
- [ ] **B2 Password where the server asks.** Send the password on every password-guarded call and handle 401 (wrong) / 429 (locked) / 403 (SSO) without signing out. *Deps:* G1 · *Effort:* S–M · *Value:* High
- [ ] **B3 SSO confirmation.** Per G2. *Deps:* G1, G2 · *Effort:* M–L · *Value:* High
- [ ] **B4 Refused revoke message.** A refused device revoke no longer reports "approve failed". *Deps:* — · *Effort:* XS · *Value:* Med

## Suggested order

B1 (done by inspection) → B4 → A3 → A5 → A2 → A4 → A6 → A1/A7 on a build → B2/B3 once G1 is answered.

## Owner decisions

- 2026-11-26: device-signature enforcement on device revoke and re-wraps of an existing wrapped key stays at 2026-12-01; released clients (up to v1.4.355) do not sign these calls.
