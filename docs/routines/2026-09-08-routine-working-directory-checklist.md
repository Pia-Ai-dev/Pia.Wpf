# Checklist — routine working directory

**Status:** not started.
**Owner:** Marco Altmann. **Written:** 2026-09-08.
**Origin:** [`2026-09-08-routine-working-directory.md`](2026-09-08-routine-working-directory.md),
which is the plan this tracks. Tick a box in the commit that lands its step.

**Effort:** `XS` under a day, no new types · `S` 1–2 days · `M` 3–5 days, new types or a new surface ·
`L` a week or more, a new subsystem.
**Value:** `High` user-visible or a real risk closed · `Med` worthwhile, not headline · `Enabler` little
standalone value, unblocks a High.

## Decision gates

| Gate | Question it answers | Blocks |
|---|---|---|
| G1 — the extraction (step 4) | Do `ChatTitleChipInteractionTests`' **assertions** still pass after the picker moves into a shared control? Its one `FindName` lookup is expected to be re-routed through the child; nothing else may move. | Steps 6 and 8. A red chip assertion means the extraction changed behaviour; fix the control, never the test. If it cannot be made green, fall back to duplicating the markup in `RoutinesView` and reopen decision D1 with the owner. |
| G2 — the manual pass (step 8) | Does a routine actually read and write inside its folder, on both kinds? | Calling the feature done. The `dotnet test` gate cannot answer this: nothing in it launches the app or fires a real routine. |

## Steps

- [x] **1. Persist the folder on a routine.** `ScheduledJob.WorkingDirectory`, the `CREATE TABLE`
  column and its PRAGMA-guarded `ALTER`, `workingDirectory` on `CreateAsync`/`UpdateAsync` (empty
  clears on update), and the three `IScheduledJobService` test fakes. Stays off the sync SET list.
  *Deps:* — · *Effort:* S · *Value:* Enabler
- [x] **2. The AgentTask leg runs in the folder.** One `WorkingSubpath: job.WorkingDirectory`
  argument on the `HeadlessRunRequest`; `RunWorkspaceService` already narrows the seed and promotes
  back to the narrowed root.
  *Deps:* 1 · *Effort:* XS · *Value:* High
- [x] **3. The Research leg runs in the folder.** `WorkingSubpath` on `BackgroundTurnRequest`, into
  the turn's `TaskContext`, and stamped on all three chats the runner writes. This is the kind the
  whole blueprint catalog produces, so without it the field looks broken.
  *Deps:* 1 · *Effort:* S · *Value:* High
- [ ] **4. Extract `PiaWorkingDirectoryPicker`.** Move the chip's inline picker markup and
  code-behind into `Controls/Shared`, give it an `AutomationIdPrefix` dependency property, a
  `CloseRequested` event and a `FocusEntries()` method, and rehost the chip on it with
  `AutomationIdPrefix="ChatChip"` so every existing id survives byte-identical. Pure refactor.
  *Deps:* — · *Effort:* M · *Value:* Enabler
- [ ] **5. Editor state in `RoutinesViewModel`.** `EditWorkingDirectory` plus its display string, the
  hosted picker view-model, the default cached in `RefreshAsync` and seeded into both create paths,
  the row's own copy, and the save wiring. Do **not** let the field trip `PickersTouched()`.
  *Deps:* 1 · *Effort:* S · *Value:* Enabler
- [ ] **6. The editor shows the picker.** Button + popup beside Effort — toggled by reading the popup,
  not the flag, the way the chip does — the detail-pane line, the four resx keys in all three
  locales, the `ViewAutomationIdTests` rows and the playbook's id list.
  *Deps:* 4, 5 · *Effort:* S · *Value:* High
- [ ] **7. Release notes and the gate.** The `RELEASE.md` bullet, a clean `-t:Rebuild` in Debug *and*
  Release at `0 Warning(s)`, and an unfiltered `dotnet test` at `failed: 0`.
  *Deps:* 2, 3, 6 · *Effort:* XS · *Value:* Enabler
- [ ] **8. Manual verification.** Walk §6 of the plan: seed, drill, create-inline, save, reopen, run
  now on both kinds, and confirm an old routine still reads `\`.
  *Deps:* 7 · *Effort:* XS · *Value:* High

## Suggested order

**1 → 2 → 3** first: the whole runtime half is cheap once the column exists, it is fully covered by
unit tests, and finishing it means the feature is real even if the UI slips. **4** next and on its
own — it is the only step that can go wrong quietly, and G1 is its gate. Then **5 → 6** as one
vertical slice, **7**, and **8** last.

Steps 2 and 3 are independent of each other once 1 has landed, and 4 is independent of 1–3 entirely,
so 4 can run in parallel with the runtime work if two sessions are available.

## Not yet planned

Candidates that came up while scoping and got no plan doc. Recorded so they are not lost.

- **Seed AI-created routines from the default folder too.** `ScheduledJobToolHandler` passes no
  working directory, so a routine Pia creates lands at the sandbox root while one created in the
  editor lands in `Playground`. Defensible either way; nobody has asked.
- **Tell the model its root on the Research leg.** `BackgroundAssistantTurnRunner` calls
  `PrepareTurn` with no `environmentRoot` at all today. Passing
  `IFilesToolHandler.DescribeEffectiveRoot(request.WorkingSubpath)` would name the narrowed folder in
  the system prompt, at the cost of a new constructor dependency.
- **Put the folder on the wire.** Needs server-side columns on `SyncScheduledJob` first, and an
  answer to what a path means on a device whose assistant-files folder is somewhere else entirely.
- **Show the folder on the routines list row**, not only in the detail pane.
