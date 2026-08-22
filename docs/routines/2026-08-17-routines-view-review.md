# Routines view — code review findings

Review of the routines-view commits (`0ee30e7f`, `075ed65d`, `2b7a11cb`): code quality, repo
guidelines, memory/performance.

## Bugs

1. **Failed save corrupts the editor state.** In `RoutinesViewModel.SaveAsync` the code after the
   `finally` runs on the failure path too: `EditingJobId` is cleared while the editor stays open (a
   retry then creates a duplicate instead of updating), and the trailing refresh re-resolves the
   selection to a new row instance, which cancels the editor and discards the user's input. The
   `catch` clearly intends to keep the editor open. Fix: return early from the `catch`, or move the
   refresh/select tail into the success path only.

2. **12-hour clock in the detail pane.** `RoutinesView.xaml` formats `TimeOfDay` with `hh:mm` and no
   AM/PM designator, so 14:30 renders as "02:30". Should be `HH:mm` (the editor parses `HH:mm`).

## Performance / memory

3. **N+1 queries on refresh.** `RefreshAsync` calls `IsOwnedByThisDeviceAsync` per job, which
   re-queries the job row the VM already holds and re-resolves the local device id (another DB read)
   every time — roughly 3N+2 round-trips per refresh, all on the UI thread (Microsoft.Data.Sqlite
   "async" runs synchronously). Suggestion: resolve the device id once and compute ownership from
   `job.OwnerDeviceId` in memory.

4. **Sync-over-async run-history reads.** `GetFiringsForTriggerAsync` executes synchronously under
   the shared `AgentRunService` gate and returns `Task.FromResult`, so it runs inline on the UI
   thread and can contend with the background run writer. Indexed and `LIMIT 5`, so low risk;
   wrapping the load phase in `Task.Run` would fix this and finding 3 together. Awaiting services on
   the UI thread is repo-standard, so treat as "consider", not a violation.

No memory leaks found: no event subscriptions, timers, or dangling callbacks; the VM is scoped per
window and needs no `IDisposable`.

## Repo guidelines

5. **Comment discipline.** CLAUDE.md allows one short line (two wrapped lines as ceiling) and never a
   `<para>` block. The `RoutinesViewModel` class summary contains a `<para>` block, and several XML
   docs exceed the ceiling (`JobKinds`, `DayOfWeekChoices`, `BuildRecentRunsSummary`, `RunNowAsync`,
   `RoutineRow`, `CanRunNow`). The repo has precedent for longer docs, but new code is where the rule
   gets enforced.

6. **Wrong failure string for toggle/delete.** Both reuse the "save failed" message for toggle and
   delete failures. Suggestion: a distinct or generic "operation failed" key.

7. **Dead `RefreshCommand`.** No refresh button exists in the view; only navigation calls it.
   Suggestion: wire a button or drop the command.

## What is done well

- Privacy-first logging is fully compliant: user content only via `SensitiveDebug`, IDs in
  `LogInformation`.
- CanExecute gates on busy/selection, delete confirmation, unknown-status-inert handling, and
  failure-isolated run history — all covered by the 492-line test suite.
- The missed-run dialog fix is correct: dismissal routes to the existing null path, the Secondary
  arm is live, and the prompt-gate wait is bounded without losing the occurrence.
- Playbook, resx (all three locales), and navigation wiring are consistent.

## Suggested fix order

1. `SaveAsync` early return in `catch` (fixes duplicate-create and lost-edits in one move).
2. `hh:mm` → `HH:mm` in the detail pane.
3. De-N+1 the refresh (device id once, ownership in memory; optionally `Task.Run` the load phase).
4. Trim the flagged XML docs to the one-line rule.
5. Distinct toggle/delete failure string.
6. Regression test: failed save keeps the editor open and the editing id intact.

---

# Verdicts after pressure-testing (2026-08-17)

Each finding was checked against the code before acting. Five were acted on, two rejected with evidence.

### 1. Failed save corrupts the editor state — **CONFIRMED, fixed**

Real, and both halves land. On the edit path the tail clears `EditingJobId` and then `RefreshAsync` rebuilds
`Jobs`, so re-resolving the selection hands `SelectedJob` a *new* `RoutineRow` instance; `RoutineRow` is a class
with no equality override, so the setter fires, `OnSelectedJobChanged` sees `IsEditorOpen` and calls
`CancelEdit()`. The user's input is gone and the id is null. Fixed by returning from the `catch` (the `finally`
still clears `IsBusy`).

Regression test `AFailedSave_KeepsTheEditorOpen_WithItsEditingIdAndTheTypedInput` asserts editor-open, the
editing id, *and* the typed name. Verified to FAIL against the unfixed code (at the `IsEditorOpen` assertion),
not merely to pass against the fixed code.

One caveat the test cannot reach: in the running app the editor also dies by a second route — `Jobs.Clear()`
makes the ListBox push `null` back through the TwoWay `SelectedItem` binding. The fix covers it because it skips
the refresh entirely, but a green unit test is not evidence that "the editor survives a refresh".

### 2. 12-hour clock in the detail pane — **CONFIRMED, fixed**

`StringFormat=hh\\:mm` on a `TimeOnly` with no designator renders 14:30 as "02:30". Now `HH\\:mm`, matching the
`HH:mm` the editor parses and the resx validation hint quotes.

### 3. N+1 queries on refresh — **PARTLY CONFIRMED (premise overstated), fixed anyway**

The count is wrong. `ResolveLocalDeviceIdAsync` is not "another DB read": it goes through
`SettingsService.GetSettingsAsync` → `JsonPersistenceService.LoadAsync`, which returns `_cached` unconditionally
when it is non-null. After the first call it is a memory read, no file and no DB. So the cost was 1
`GetAllAsync` + N redundant single-row SELECTs (+ N history reads), not "roughly 3N+2 round-trips".

Fixed regardless, because the redundant SELECTs are real and the project had already written down the fix. This
N+1 was a *recorded, deliberate* trade-off (`docs/superpowers/specs/agent-roadmap/00-OVERVIEW.md`): rejected the
in-ViewModel `OwnerDeviceId` comparison the review suggests, because it duplicates the owner rule that the
interface keeps in one place beside `GetDueJobsAsync`' SQL predicate — a drifted copy of that rule is a
double-fire. The same bullet named the sanctioned alternative: "an overload taking the already-loaded job, not a
second copy of the rule". That overload now exists — `IsOwnedByThisDeviceAsync(ScheduledJob)` holds the single
copy of the rule, `IsOwnedByThisDeviceAsync(Guid)` delegates to it after its own read — and the bullet is
updated in the same change rather than left claiming the debt is open.

### 4. Sync-over-async run-history reads — **REJECTED**

Not just "repo-standard". `RefreshAsync` is awaited by four command tails that then read `Jobs`/`SelectedJob`
synchronously. Under `Task.Run` the load phase runs off the UI context, so `PostOrRun` takes its `_sync.Post`
branch — fire-and-forget — and the correctness of those tails starts depending on SynchronizationContext queue
ordering, which no test covers. That is a real hazard traded for an indexed `LIMIT 5` query. Left alone.

### 5. Comment discipline — **CONFIRMED, fixed**

CLAUDE.md is explicit ("never a `<para>` block", two wrapped lines as the ceiling). Trimmed the
`RoutinesViewModel` class summary (the `<para>` is gone; the non-obvious constraint it carried — why autonomy is
not per-job — is kept in one line), plus `JobKinds`, `DayOfWeekChoices`, `LoadRecentFiringsAsync`,
`BuildRecentRunsSummary`, `RunNowAsync`, `RoutineRow`, `CanRunNow`, two over-long inline comments in `BuildRow`
and `SaveAsync`, and one three-line XAML comment.

Scope note beyond what the review flagged: commit `075ed65d` added new over-ceiling text in
`ScheduledJobBackgroundService` (the `_missedPromptQueueLimit` summary and the give-up comment) and in
`ScheduledJobNotificationSurface`; those are new text on this branch under the same rule, so they were trimmed
too. The *pre-existing* multi-`<para>` structure in `_missedPromptGate`'s doc and in `IScheduledJobService` was
left alone — `075ed65d` rewrote prose inside it rather than introducing it. Test-file docs were left alone.

### 6. Wrong failure string for toggle/delete — **REJECTED**

The premise is factually wrong. The key is named `…_SaveFailed` but its text is already generic: "That change
could not be saved." / "Diese Änderung konnte nicht gespeichert werden." / "Impossible d'enregistrer cette
modification." A failed enable/disable *is* a change that could not be saved, so the string is correct there.
For delete it reads slightly off, but not wrongly enough to justify three new resx entries and the locale-parity
churn on a false premise. Left as is.

### 7. Dead `RefreshCommand` — **CONFIRMED, dropped**

Confirmed unreferenced repo-wide: not in `RoutinesView.xaml`, not in the tests (which call `RefreshAsync()`
directly), nowhere else. Removed the `[RelayCommand]` attribute and the matching
`[NotifyCanExecuteChangedFor(nameof(RefreshCommand))]`; `RefreshAsync` stays a plain awaited method with its own
`IsBusy` guard. Dropped rather than wired to a new button, since adding an affordance is a design decision, not
a review fix. `CanWork` stays live via `StartCreateCommand`/`SaveCommand`.

### Gate

`dotnet build -t:Rebuild -v:n` Debug and Release: `0 Warning(s)`, `0 Error(s)`. `dotnet test` unfiltered:
`failed: 0`, `succeeded: 4072`.

---

# UI walkthrough (WinWright, 2026-08-17)

Driven against the real profile (no data-directory override exists — `Environment.SpecialFolder.LocalApplicationData`
ignores `LOCALAPPDATA`). Routines created for the walkthrough were deleted afterwards; the list is back to empty.
`Run now` was never invoked — it dispatches a real agent turn against the user's provider. Times were chosen so no
row was ever armed for the same day.

## What the walkthrough confirmed

| Change | Verdict |
|---|---|
| #2 `hh` → `HH` in the detail pane | **Confirmed.** A routine saved at 14:30 renders `14:30`; `02:30` is absent. Discriminating — the two formats only differ for hours ≥ 13, so the editor's 09:00 default would have passed either way. |
| "New routine" label (`2b7a11cb`) | **Confirmed.** `Routines_NewJob` exposes `Name = "New routine"`. |
| #7 `RefreshCommand` dropped | **Confirmed no regression.** Navigation still loads the list; the empty state, the rows and the provider choices all populate. |
| #3 ownership de-N+1 | **Smoke only.** On a single-device profile every row is locally owned, so `Run now` is enabled and `Routines_NotOwnedHere` never renders — identical before and after the refactor. This is a no-regression check, not validation. |
| #5 comment discipline | Not UI-visible. |
| #1 failed-save keeps the editor | Not inducible without corrupting the real database. The unit test verified to fail against the unfixed code is the stronger evidence and already exists. |

Also exercised and clean: editor round-trip (`HH:mm` back into `Routines_Field_Time`), edit-then-save (selection
retained, detail pane updates), enable/disable toggle (`Disable` → `Enable`), delete with its confirmation dialog,
and the conditional recurrence rows (Weekly shows `Routines_Field_DayOfWeek`, hides `Routines_Field_DayOfMonth`).
The log for the whole session contains no `Could not load/save/toggle/delete` line, and no routine name or goal
appears above `SensitiveDebug`.

## Two defects the walkthrough found, both fixed

### A. Saving a NEW routine left it unselected — **fixed**

Reproducible every time: create a routine, save, and the detail pane keeps showing the placeholder. With another
routine already selected it is worse — the pane keeps showing *that* one, so the user is looking at a different
routine than the one they just created, with nothing saying so.

`SaveAsync`'s tail read `Jobs` immediately after `await RefreshAsync()`, but the rebuild inside `RefreshAsync` is
marshalled through `PostOrRun`, which defers whenever the context captured at construction is not the one the call
is on — and every await in the refresh completes synchronously, so the tail ran first, against the *previous*
rows. `Jobs.FirstOrDefault(saved)` then found nothing and `?? SelectedJob` fell back to the old selection.

This is the ordering hazard the rejection of finding #4 assumed was not live ("the correctness of those tails
starts depending on SynchronizationContext queue ordering"). It already was.

Fix: `RefreshAsync` takes the id to select and resolves it *inside* the marshalled block, alongside the rebuild, so
no caller depends on when the marshal runs. The `?? SelectedJob` fallback is gone — it is what displayed the wrong
routine. Edit-save is unaffected (it happened to work, because the old rows still contained the edited id).

The other four `RefreshAsync` callers were audited for the same class of bug: `OnNavigatedToAsync`,
`ToggleEnabledAsync`, `DeleteAsync` and `RunNowAsync` all end *at* the `await`, and none reads `Jobs` or writes
`StatusMessage` after it, so the refresh's own deferred `LoadFailed` write is always the newest fact rather than an
out-of-order one. `SaveAsync` was the only tail that read state the marshal had not applied yet.

Regression test `SavingANewRoutine_SelectsIt_WhenTheRebuildIsDeferred` installs a queueing
`SynchronizationContext` for the constructor only, which is what makes `PostOrRun` defer the way it does under
WPF. Verified to FAIL against the unfixed code — and to fail by selecting the *previously* selected row, matching
the live symptom exactly, not merely by being null.

Re-verified live: creating a routine now selects it immediately (14:30, next run tomorrow 2:30 PM), and creating a
second one while the first is selected switches the pane to the second (17:05).

### B. Editor ComboBox items announced the C# record — **fixed**

Every item in the Kind / Repeats / Day / Month / Provider ComboBoxes exposed its UIA `Name` as the generated record
`ToString()` — `"RoutineDayOfWeekChoice { Value = Sunday, Label = Sunday }"` — because the item peer reads the bound
object, not the `DisplayMemberPath` text. That is what a screen reader reads out, and the provider list would have
read a GUID out loud. The visible text was correct throughout, so no screenshot shows it.

Fix: a `ToString()` returning the label on each of the five choice records. Re-verified live — items now read
`Sunday` … `Saturday`, and `ww_select(optionText="Weekly")` works where it previously failed with
`pattern_not_supported`.

## Observed, not fixed

- **Navigating away with the editor open discards the typed input**, silently. This is the second route the
  verdict on #1 flagged as unreachable by unit test; it *is* reachable — the VM is scoped per window, so coming
  back re-runs `RefreshAsync`, and the selection change closes the editor. Nothing is corrupted and the selection
  survives, so this is a design call (warn? keep the draft?), not a review fix.
- The wider `DisplayMemberPath`-over-a-record pattern also appears in `PersonaEditContentDialog`, `OptimizeView`,
  `ProvidersView`, `AssistantHistoryView` and `AssignmentConsentContentDialog`. Only the routines records were
  changed here; the others are worth the same one-line check.

### Gate after the fixes

`dotnet build -t:Rebuild -v:n` Debug and Release: `0 Warning(s)`, `0 Error(s)`. `dotnet test` unfiltered:
`failed: 0`, `succeeded: 4073`.

### For the playbook

`ww_select(optionText=…)` on a ComboBox bound to DTOs matches the item's `ToString()`, not the rendered label — a
`pattern_not_supported` error there is a naming problem in the app, not a selector problem. `optionIndex` works
regardless and is the way to confirm which it is.
