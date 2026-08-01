# Batch 09 — Scheduler UI · implementation spec

Companion to [`09-scheduler-ui.md`](09-scheduler-ui.md). Written 2026-08-01, after
[Batch 13](13-view-test-host.md), against the as-built code at `db51b09`.

## 0. What the roadmap got right, and the one thing it did not

`09-scheduler-ui.md` says the acceptance is "jobs carry budget + policy". **Neither exists on the model, and
that was verified rather than assumed.** `Models/ScheduledJob.cs` carries Name, Query, Kind, GrantedTools,
ProviderId, the recurrence fields, NextFireAt, Status, CreatedAt/UpdatedAt, LastFiredAt, LastResultEntryId,
ConsecutiveFailures and OwnerDeviceId — and nothing else. The budget an agent job runs under is built at fire
time from **global** settings (`ScheduledJobBackgroundService.ExecuteAgentTaskAsync:183` →
`RunProfile.FromBudget(settings.ScheduledMaxSteps, settings.ScheduledMaxReplans,
settings.ScheduledWallClockMinutes)`), and no autonomy policy is passed at all — the run resolves one from
settings at launch via `RunAutonomyPolicy.FromSettings`.

## 1. Decisions (owner, 2026-08-01)

- **D1 — Global-only budget and policy for v1.** The editor authors goal + schedule + provider + granted
  tools; budget and autonomy are shown as read-only context pointing at the settings that own them. Rejected
  for v1: per-job fields. The reason is not size — it is that a per-job autonomy class list becomes
  **peer-writable, unvalidated input** the moment it crosses the sync wire (`SyncScheduledJob.GrantedTools`
  already is, 04 §13.2), so it needs `ParseGrantedTools`' treatment and a decision about what an unknown class
  from a newer peer means. That is a batch, not a field.
- **D2 — Full CRUD plus enable/disable, including the re-arm surface.** Anything less leaves both obligations
  Batch 10's W3 handed to 09 still open.
- **D3 — Both `ScheduledJobKind` values.** Every job the chat tool has ever created is a `Research` one, so a
  UI listing only `AgentTask` would show an incomplete list and invite a user to create a duplicate.
- **D4 — "Run now", owner device only.** Non-owner devices never advance a job; the button is unavailable
  there and says why rather than being silently absent.
- **D5 — Last-run outcome is rendered**, with a link to `LastResultEntryId`, so a `Failed` job explains itself
  instead of merely being off. Read-only; no new persisted state.
- **D6 — Granted write tools are editable**, reusing the existing synced field.
- **D7 — The UI is a SECTION of `Views/SettingsViews/AssistantView.xaml`**, not a new settings page. It
  re-roots to its own sub-ViewModel the way `PersonasVm` (:163), `ToolPermissionsVm` (:168) and `MeetingVm`
  (:249) already do, and it sits next to the "Background & scheduled runs" budget block that already exists —
  which is also what makes D1's read-only budget context free rather than a new surface.

## 2. What this batch must NOT get wrong (inherited obligations)

- **Unknown `ScheduledJobStatus` must render safely.** `ScheduledJobStatus`' own XML doc mandates it: the enum
  crosses the sync wire as an int and is cast back **unvalidated** (`SyncMapper.cs:1000`,
  `(ScheduledJobStatus)(decrypted.Status ?? 0)`), so a newer peer's ordinal arrives as an undefined value. The
  UI must show it as unknown-and-inert, must never coerce it to `Active`, and an edit must not silently
  normalise it.
- **The re-arm gap is a missing PARAMETER, not missing logic.** `UpdateAsync` already re-arms a `Completed`
  one-off whose recomputed `NextFireAt` lands in the future — but it takes no `specificDate`, so the one thing
  a settled one-off needs (a new date) cannot be supplied and the row can never be moved. Adding the parameter
  is what makes the existing re-arm reachable.
- **Owner semantics.** `OwnerDeviceId` null means a legacy device-local row; only the owner fires.

## 3. Work groups

- **G1 — service layer.** `IScheduledJobService.UpdateAsync` gains `specificDate` (and `kind`, for the same
  reason: a job authored as Research cannot otherwise become an AgentTask without delete-and-recreate, which
  loses its history). Re-arm becomes reachable for a settled one-off. Tests: a settled one-off moved to a
  future date goes `Completed` → `Active`; moved to a past date does not; a `Disabled` row is still untouched.
- **G2 — run-now seam.** A narrow interface over `ScheduledJobBackgroundService` (already a DI **singleton**,
  `Bootstrapper.cs:592`, so it is injectable as-is) exposing a single `RunNowAsync(Guid, CancellationToken)`
  that resolves the job, refuses a non-owner, and dispatches through the same `_runLock` + `ExecuteJobAsync`
  path a tick uses. **It must not bypass the lock** — see R15: that lock is what bounds a delegating job.
- **G3 — the ViewModel.** `ScheduledJobsSettingsViewModel`: list ordered by `NextFireAt`, create/edit/delete,
  enable/disable, run-now, last-run outcome, unknown-status tolerance. Privacy: the job goal is user content →
  `SensitiveDebug` only.
- **G4 — the XAML section + strings.** A section in the settings `AssistantView`, re-rooted to the new
  sub-ViewModel, plus every new string in `ViewStrings.resx` **and** `.de.resx` **and** `.fr.resx`.

## 4. What Batch 13 changes about G4, and it is the point of having done 13 first

The new section's binding paths are covered **automatically** by
`SettingsAssistantViewParseTests.EveryBindingPath_ResolvesOnTheViewModelThatMarkupRootsItAt`, which walks that
exact file and re-roots at each section's `DataContext`. So a typo in the new section fails a test instead of
shipping a dead control — the first batch on this branch whose settings XAML is covered as it is written
rather than booked as smoke debt. **It is not total**: the sweep sees `TextBlock.Text` only, `Content=` on a
button or CheckBox is invisible without template application, and a `DataTemplate`'s item-scoped bindings are
out of reach of a logical walk — so a jobs LIST rendered through an `ItemTemplate` has its row bindings
uncovered unless a fact instantiates the template the way the avatar and trace-row facts do.

## 5. Acceptance

A user can create, edit, enable/disable, delete and manually fire scheduled jobs of both kinds from the
Assistant settings page; a settled one-off can be re-armed; an unknown status renders inert; existing
emission and owner semantics are unchanged; every new string exists in all three locales; the build is
`0 Warning(s) / 0 Error(s)` in Debug **and** Release under `-t:Rebuild`, and the suite is at `failed: 0`.
