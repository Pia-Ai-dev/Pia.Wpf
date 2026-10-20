# Clear Local Synced Data on Account Switch — Checklist

**Status:** Open
**Owner:** Marco Altmann
**Written:** 2026-10-20
**Origin:** [2026-10-20-local-data-cleanup-plan.md](2026-10-20-local-data-cleanup-plan.md), from
[2026-10-20-local-data-cleanup-design.md](2026-10-20-local-data-cleanup-design.md)

Tick a box in the commit that lands its step. Step numbers match the plan's task numbers.

**Effort:** `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a new
surface · `L` a week or more, a new subsystem.
**Value:** `High` user-visible or a real risk closed · `Med` worthwhile, not headline · `Enabler`
little standalone value, unblocks a High.

## Steps

- [x] **1. Marker file** — `PiaPaths.LocalDataResetMarkerPath` and `LocalDataResetMarker` with an atomic write.
  *Deps:* none · *Effort:* XS · *Value:* Enabler
- [x] **2. Settings resets** — `ClearAccountState`, `ResetSyncedData`, the 13 synced preference names and the post-reset push latch on `AppSettings`.
  *Deps:* none · *Effort:* XS · *Value:* Enabler
- [x] **3. Wiper** — `LocalDataWiper` empties every synced store with plain SQL and file operations and reseeds the kanban columns.
  *Deps:* 2 · *Effort:* S · *Value:* High
- [x] **4. Startup hook** — `LocalDataResetStartup` runs a pending clear in `Bootstrapper` before any store is read, and keeps the marker on failure.
  *Deps:* 1, 3 · *Effort:* XS · *Value:* High
- [x] **5. Chat flush** — `IChatSyncFlusher` on `AssistantChatSyncService` reports whether every chat change reached the server.
  *Deps:* none · *Effort:* S · *Value:* Med
- [x] **6. Reset service** — `ILocalDataResetService`: the "has data" check, the notices, the final upload, schedule and restart.
  *Deps:* 1, 5 · *Effort:* S · *Value:* Enabler
- [x] **7. Prompts and strings** — `AccountSwitchPrompts`, the dialog close label and the 19 strings in en/de/fr.
  *Deps:* 6 · *Effort:* S · *Value:* Enabler
- [ ] **8. Account settings wiring** — sign-in guard on four commands, the sign-out prompt on the button, the revoked dialog and account deletion.
  *Deps:* 7 · *Effort:* S · *Value:* High
- [ ] **9. Wizard wiring** — the sign-in guard in the first-run wizard, and the wizard is not marked done when a clear restarts it.
  *Deps:* 7 · *Effort:* XS · *Value:* High
- [x] **10. First sync after a clear** — pushes omit the settings row until a pull has applied the account's.
  *Deps:* 2 · *Effort:* XS · *Value:* High
- [ ] **11. Gate and manual check** — zero warnings in Debug and Release, `Failed: 0`, and a manual round trip on a throwaway profile.
  *Deps:* 1–10 · *Effort:* XS · *Value:* High

## Not yet planned

- A brand-new device's first sync overwrites the account's preferences in the same way; step 10
  covers only the sign-in after a clear.
- The Pia.Docs desktop guide likely needs a line about the sign-out and sign-in prompts; the
  `help-corpus` skill checks this on the release branch.

## Suggested order

1 → 2 → 3 → 4 first: they deliver the clear and its crash guarantee, and the wiper's test is the
most decisive check of the whole design. Then 10, which closes the one risk that would damage
account data. Then 5 → 6 → 7, then the two user-facing slices 8 and 9, then 11.
