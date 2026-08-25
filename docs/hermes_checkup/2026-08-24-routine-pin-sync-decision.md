# Decision — a routine's persona and reasoning-effort pins stay device-local

**Status:** Decided and shipped. The feature (group **E**) is complete and verified on Windows; this file is
the durable record of the *decision*, which the code cannot state.
**Owner:** Marco Altmann.
**Written:** 2026-08-24.
**Origin:** §2 and §12 of [2026-08-22-routine-persona-effort-plan.md](2026-08-22-routine-persona-effort-plan.md),
which is **superseded** — its Status inverts the truth (it says E7 is outstanding and nothing was built or
tested on Windows; E7 was done 2026-08-23 and E9–E11 shipped 2026-08-23/24) and it predates the resume work,
so it cannot be trusted as a description of behaviour. That plan is **not deleted yet**: it carries an inbound
link from a frozen C-track doc and dies in a later phase. Read this file instead of its §2.

This exists as its own doc because the tracking checklist — the alternative home — is being reduced to one
sentence per row, and half of "done" for this row is that the decision does not get re-litigated.

---

## 1. The decision

> **`ScheduledJob.PersonaId`: does NOT sync — device-local. `ScheduledJob.ReasoningEffort`: does NOT sync —
> device-local. No split: both stay off `SyncScheduledJob` and out of `UpsertFromSyncAsync`'s SET list.**

Both pins live only in `src/Pia.Wpf`, exactly like `QuietOnSuccess`. Local → synced is strictly additive
later; a wire slot is provably unreclaimable in this codebase (`SyncScheduledJob.cs:27` still carries dead
`AnswerLength` "for wire-contract stability").

---

## 2. Why — the data-loss mechanism, with the coordinates that are expensive to re-derive

**The wire is a two-repo contract this repo does not control.** The server lives in the sibling repo `Pia`,
which consumes `Pia.Shared` as a **git submodule pinned to `Pia.Wpf @ cda4590f` (2026-08-12)**. Its push
handler at **`Pia/src/Pia.Server/Sync/SyncService.cs:1750-1822`** copies scheduled-job fields **one by one by
hand** into `ServerScheduledJob`. There is no extension-data passthrough, so a new plaintext field is silently
dropped until a submodule bump + entity + EF migration + deploy all land.

**The destructive consequence, verified.** `ScheduledJobService.UpsertFromSyncAsync`
(`ScheduledJobService.cs:668-697`) writes its whole SET list **unconditionally**, and the pull-apply at
`SyncClientService.cs:1532` uses `>=`, so a tie favours remote. A synced-but-server-dropped field therefore
returns null and **NULLs the user's own pin on their own machine**. That is not hypothetical: it is exactly
the documented Persona `OutputFormat` bug — [`../sync-e2ee-overview.md`](../sync-e2ee-overview.md) line 48,
"the originating device wiped its own local value after one push→pull cycle" — which was fixed only by a
server DTO/entity/mapper change plus `AddPersonaOutputFormat`.

**Keep those three coordinates literal.** `SyncService.cs:1750-1822`, the `cda4590f` submodule pin, and
`sync-e2ee-overview.md:48` live in a submodule-pinned sibling repo and cannot be re-derived from this
checkout. Re-deriving them is the day of work this doc exists to prevent.

**The asymmetry that breaks the "ProviderId syncs, so PersonaId must" analogy.** `UpsertFromSyncAsync`
validates nothing and would store an unresolvable Guid verbatim, and
`HeadlessRunLauncher.ResolveRunPersonaAsync` (`HeadlessRunLauncher.cs:1093-1126`) answers an off-roster or
unresolvable persona by logging at Information and **silently** substituting the mode persona. An unresolvable
*provider* is the opposite: a **visible** `Failed` status plus one re-arm (`ScheduledJobService.cs:467`,
`IsPreModelFailure`). A pin that fails quietly is a worse thing to sync than one that fails loudly.

---

## 3. The three pro-sync claims that FAILED verification

Recorded so the losing case is not re-argued from scratch. Its best argument does survive — `SyncScheduledJob`
draws its own boundary at "execution state" and a persona pin is run *input*, `ModePersonaDefaults` already
syncs a bare persona Guid with a full tombstoning merge (`SyncMapper.cs:953`), and editing a routine is not
ownership-gated (`RoutinesViewModel.cs:415`, `CanActOnSelection` at `:364`), so a device-local pin means an
edit made on a non-owner device is confirmed by the UI and never runs. It loses on these three:

1. **Its own implementation plan says to add both columns to `UpsertFromSyncAsync`'s SET list. That IS the
   data-loss mechanism** of §2, so its reassurance that "both nullable… the apply side never clobbers a local
   value" is false against the actual code.
2. **Its server-contract citation is out of scope.** `docs/server/assistant-chat-history.md` §1 — "unknown
   fields must be stored and returned verbatim" — governs the chat-history *document* endpoint, not
   `/api/sync/push`, whose mapper drops unknowns.
3. **"Peers need it" is undercut by a fact it concedes.** Only the owner device ever fires
   (`ScheduledJobService.cs:117`, `:131-139`), so nothing is *acted on* elsewhere. The only real loss is
   authoring convenience, which is repairable in-client without a coordinated release.

The E2EE-demotion worry raised separately is discounted: `ProviderId` / `GrantedTools` already ride inside the
ciphertext (`SyncMapper.cs:992`), so plaintext leakage would be carelessness, not inevitability.

---

## 4. What would reverse it — three triggers

Any one of these flips `PersonaId`, and only then, separately, `ReasoningEffort`.

1. **The server half actually landing:** `Pia/src/Pia.Server/Models/ServerScheduledJob.cs` gaining the two
   columns with an EF migration, both push branches at `SyncService.cs:1750-1822` copying them, and the
   `lib/Pia.Wpf` submodule bumped past this work — **plus `UpsertFromSyncAsync` converted to a null-guarded
   apply**, the pattern `SyncMapper.cs:910` already uses for `AssistantDefaultWorkingDirectory` ("the apply
   side must not clobber the local value on null"), so the mixed-version window is survivable. **Do not drop
   that last clause**: without it the trigger *is* the data-loss mechanism §2 rejects.
2. **Ownership transfer shipping** (`SyncScheduledJob.cs:15` still calls it "a future flow"), which makes a
   device-local pin evaporate on transfer and forces the pin to travel with it.
3. **A real report of a multi-device user losing a pin** by editing on the non-owner device *after* the
   `Routines_NotOwnedHere` mitigation shipped — that would prove the in-client honesty fix is insufficient and
   the coordinated release is worth buying.

**The code comment at `ScheduledJobService.cs:688-689` records the omission but none of the three triggers.**
That gap is the whole reason this file exists.

---

## 5. Two owner questions still unanswered

- **Q2 — should a job's effort pin also override a DELEGATED step persona's effort?** The code implements
  **no**: `StepPersonaResolver.cs:193` passes `jobPin: null`, following that resolver's existing refusal to
  let a run-level provider override win at step level. Nothing else records that the other side has a case: a
  user who pins Minimal to control spend may be surprised that a fan-out's specialists run at their own,
  possibly higher, effort. Reversing it means threading the pin into `StepPersonaResolver.ResolveAsync` and
  touching both executor call sites.
- **Q4 — should `create_scheduled_research` / `update_scheduled_research` expose the two pins?** Deliberately
  no for now. Strictly additive later.

---

## 6. Two shipped properties nobody should "fix"

- **The null "inherit" row must never be labelled "None" in any of en/de/fr.** The effort picker's null row
  and `ReasoningEffort.None` are two different instructions in adjacent rows ("inherit" vs "no reasoning"); if
  the null row reads "None" in any locale, users will pin no-reasoning by accident on unattended runs.
- **The persona picker deliberately has no cap**, unlike the roster's `MaxAgentPersonaRoster` of 6. This is a
  picker, not a prompt payload, so none of the roster clamp's reasoning applies. A long ComboBox for a user
  with many personas is accepted, not overlooked.

---

## 7. Verification status — what was proved in the app, and what still rests on a unit test

From the 2026-08-23 Windows session (its own doc has since been folded away, so this is the surviving record):

- **The pickers.** `Routines_Field_Persona` offers "Use the active persona" plus all 12 personas — not
  roster-gated, as designed. `Routines_Field_Effort` offers "Use the persona's setting" plus all six
  `ReasoningEffort` members with readable labels. A routine saved with Experienced Coder / Extra high
  persisted both pins, and both survived a Disable toggle.
- **The "no longer available" row**, machine-checked: with the job's `PersonaId` pointed at a GUID no persona
  has, the picker reads exactly "No longer available" while the effort pin beside it still reads "Extra high"
  — the two degrade independently, which is the point of the row. The unresolvable pin had to be written into
  the database directly, because no UI can produce one.
- **The `ALTER TABLE` migration half.** A throwaway profile was seeded from the real `history.db` with both
  `ScheduledJobs` pin columns **dropped**, so `CREATE TABLE IF NOT EXISTS` was a no-op and only the ALTER pass
  could restore them. It did, on first open, and both pins then round-tripped through the editor into columns
  that exist only because the migration added them.
- **The persisted-pin write half is confirmed end-to-end.** 13 of 13 user-triggered runs carry a persisted
  `PersonaId` — every run in the corpus, not a sample. The parked run holds `0000000a-…-0002` across a full
  application restart while `modePersonaDefaults.Assistant` was moved to `…0003` in between, so the value is
  durable *and* no longer agrees with the current mode default, which is precisely the state a resume has to
  read correctly.
- **The read half rests only on a unit test.** `ReasoningEffort` reads NULL on all 13 and that is correct — a
  user-dispatched run carries no job pin and the mode persona sets no effort — but it also means **the corpus
  never exercised the effort half at all**. The resumed run actually running *as* the persisted persona was
  not verified in the app; the resume affordance was unreachable through automation that session. The
  read-back is pinned by
  `HeadlessRunLauncherTests.Resume_RunsThePersonaAndEffortTheLaunchResolved_NotTheCurrentModeDefault`, which
  moves the per-mode default mid-park and was checked non-vacuously by reverting each half separately. The
  automation gap itself is closed in code and recorded in
  [`../test_hygiene/2026-08-24-gate-profile-hygiene.md`](../test_hygiene/2026-08-24-gate-profile-hygiene.md) §3.
