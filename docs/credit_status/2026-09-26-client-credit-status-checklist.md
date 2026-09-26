# Credit status card — checklist

**Status:** In progress: B1, B2 and C1 landed. C2's guide paragraph is written on the
Pia repo's `feature/7654` branch (commit `7dbc5e6a`); `help-corpus` must wait until that
merges. C3 and the PR (C4) still wait on the server endpoint (G1).
**Owner:** Marco Altmann
**Written:** 2026-09-26
**Origin:** Owner request, 2026-09-26. Server side is Pia work item #7654
(`docs/plans/2026-09-26-client-credit-status.md` in the Pia repo).

| Group | Plan |
|---|---|
| A, B, C | [2026-09-26-client-credit-status-plan.md](2026-09-26-client-credit-status-plan.md) |

**Effort:** `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a
new surface · `L` a week or more, a new subsystem.
**Value:** `High` user-visible or a real risk closed · `Med` worthwhile, not headline ·
`Enabler` little standalone value, unblocks a High.

## Decision gates

Do not tick a dependant of an open gate without revisiting it.

| Gate | Question it answers | Answer |
|---|---|---|
| G1 | Is `GET /api/ai/credits` deployed where the release points, with the contract the plan copies? | Open. Blocks C3 and the PR in C4, not A or B. |

## A — Data

- [x] **A1 DTOs and `CreditStatusService`.** Read `GET /api/ai/credits` and answer null for every
  "nothing to show" case (Task 1).
  *Deps:* none · *Effort:* S · *Value:* Enabler
- [x] **A2 `CreditMeterBuilder` and labels.** Turn a response into ordered, captioned meters, with
  en/de/fr strings (Task 2).
  *Deps:* A1 · *Effort:* S · *Value:* Enabler

## B — Surface

- [x] **B1 Account view model.** Fetch on each Account visit and on sign-in, clear on sign-out
  (Task 3).
  *Deps:* A2 · *Effort:* S · *Value:* Enabler
- [x] **B2 The card.** Render the meters in `AccountView.xaml`, hidden unless there is something
  to show (Task 4).
  *Deps:* B1 · *Effort:* XS · *Value:* High

## C — Ship

- [x] **C1 Release note.** One bullet in `docs/release_notes/RELEASE.md` (Task 5, step 1).
  *Deps:* B2 · *Effort:* XS · *Value:* Med
- [ ] **C2 Desktop guide.** A *Credits* paragraph in Pia.Docs quoting the resx labels, then
  `help-corpus` before the next push to `main` (Task 5, step 2).
  *Deps:* B2 · *Effort:* XS · *Value:* Med
- [ ] **C3 Live walkthrough.** Free user, unlimited group, server without the endpoint (Task 6).
  *Deps:* B2, G1 · *Effort:* XS · *Value:* High
- [ ] **C4 PR** `feature/credit-status` → `main`.
  *Deps:* C1, C2, C3 · *Effort:* XS · *Value:* High

## Not yet planned

- A compact credit indicator in the chat view, so the budget is visible without opening Settings.
- Richer 429 text: the refusal already carries `poolLimit`/`poolUsed`/`topUpGranted`/`topUpUsed`,
  and the three copies in `AiClientService` and `PiaCloudChatClient` read only `resetsAt`. One shared
  parser would let "limit reached" say whether the pool or the top-up ran out.
- Refreshing the card after each chat turn while Settings is open.

## Suggested order

A1 → A2 → B1 → B2 (a reviewable slice with fakes, no server needed), then C1 and C2 in parallel,
then C3 once G1 is answered, then C4.
