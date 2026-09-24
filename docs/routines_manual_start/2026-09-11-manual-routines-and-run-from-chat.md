# Manual routines, and starting a routine from chat — implementation plan

**Status:** implemented 2026-09-11 on `feature/manual-routines`; human smoke test open · **Owner:** Marco Altmann · **Written:** 2026-09-11
**Origin:** owner request 2026-09-11 — (1) let a routine be set to run *never*, so it can act as a
template for a complex task or agent run, and (2) give the `@Routine` at-command a "start now" door,
because saying "start it" today makes the model re-enact the routine's text in the current chat.

This document is **self-contained**: an implementer who never saw the conversation can execute it cold.
Every code claim below was verified against `main` at commit `349f0137`. Step tracking lives in
[2026-09-11-routines-manual-start-checklist.md](2026-09-11-routines-manual-start-checklist.md) — tick its
boxes in the commit that lands each slice; this plan adds no second tracking surface.

Two independent workstreams. **A** adds `RecurrenceType.Manual`. **B** adds a `run_routine` tool. Either
ships alone; together they are the feature, because a routine that never fires needs a door that starts it.

---

## 0. Why "start it" misbehaves today

`@Routine:Name` loads only the routine **CRUD** tools — `AssistantPromptComposer.cs:225` maps the domain to
`query_scheduled_research`, `create_scheduled_research`, `update_scheduled_research`,
`delete_scheduled_research`, `list_routine_blueprints`, `create_routine_from_blueprint`. Nothing in that
list *runs* a routine.

So "start it" leaves the model one move: read `Query` back out of `query_scheduled_research` and perform it
as an ordinary chat turn. That silently drops everything the routine carries, all of which is passed at
`ScheduledJobBackgroundService.cs:643`:

| Carried by the job | Lost in the re-enactment |
|---|---|
| `WorkingDirectory` → `HeadlessRunRequest.WorkingSubpath` | the turn runs in the chat's own working directory |
| `PersonaId`, `ReasoningEffort` | the chat's active persona and effort are used |
| `GrantedTools` → `GrantedWrites` | the chat's own tool gate applies instead |
| `RunShape.Planned` (an agent run) | a plain chat turn — no plan, no steps, no run panel |
| `AgentRunTrigger.Schedule` + `TriggerRef = job.Id` | the firing is not recorded against the routine |

The correct dispatch already exists and is already wired: `IScheduledJobRunner.RunNowAsync`
(`ScheduledJobBackgroundService.cs:290`), which the Routines view's **Run now** button calls
(`RoutinesViewModel.cs:1424`). It is a DI singleton and deliberately the *same instance* as the tick
(`Bootstrapper.cs:906`), so a manual fire sees the tick's duplicate-dispatch state. The fix is to give the
model a door to it — not to make the model try harder.

---

## 1. Decisions, settled

Binding. Do not re-open at implementation time.

| # | Decision | Why |
|---|---|---|
| D1 | "Never" is a **recurrence**, `RecurrenceType.Manual` — not a new `ScheduledJobStatus`, not a new bool. | A template must stay a healthy, listed, runnable row. `Status = Disabled` reads as "switched off", and any new `Status` value drops the row out of `GetActiveAsync` — which is the query the `@Routine` picker uses (`AutocompleteService.cs:153`), so the template would vanish from the place it is meant to be started from. |
| D2 | `Manual` is appended **after** `Yearly` (ordinal 5). | `RecurrenceType` crosses the sync wire as an int (`SyncMapper.cs:1005`) and is cast back with no `Enum.IsDefined` check (`:1050`, `:1071`). Append-only, exactly like `ScheduledJobKind` and `ScheduledJobStatus`. |
| D3 | A `Manual` job's `NextFireAt` is the sentinel `RecurrenceCalculator.Never = new DateTime(9999, 1, 1)`. | A named symbol, not a magic date: the due-query guard, the "no next run" converter and four tests all compare against it, and `DateTime.MaxValue` reads as "unset" at a glance. It is stored as an ISO string in SQLite and never leaves the device, so no column ceiling is in play. |
| D4 | `run_routine` **dispatches a detached run** — its own chat, its own run-panel row, its own toast. The calling chat gets "started, watch it there". | That is what carries the working directory, the agent shape and the grants. Running it inside the current chat would mean re-hosting that chat's working dir, persona and mode mid-conversation; it fights the design and is a much larger change. **Confirmed by the owner 2026-09-11** (gate Q1). |
| D5 | `run_routine` takes the routine **by name first**, id as fallback. | The `@Routine:` chip inserts the *name*, never the id (`AtCommandAutocompleteBehavior.cs:240`). An id-only tool forces a `query_scheduled_research` round-trip and re-opens the door to improvising. |
| D6 | `run_routine` v1 takes **no extra instruction** — it runs the routine's stored `Query` verbatim. | "Start it, but for Q3" is a different feature (parameterised templates) and needs its own design; see *Out of scope*. |
| D7 | A `Manual` job is **exempt from failure retirement** in `MarkRunFailedAsync` — its counter still climbs, but five strikes do not flip it to `Failed`. | Retirement drops the row out of `GetActiveAsync`, which is both the `@Routine` picker's source and `run_routine`'s name resolution — so the template would silently vanish from the two places it is started from, on exactly the kind of routine that gets run by hand many times and fails for user-caused reasons. A schedule that retires has a next occurrence it would otherwise waste; a template has none. **Confirmed by the owner 2026-09-11** (gate Q3). |

---

## 2. Workstream A — `RecurrenceType.Manual`

### 2.1 The choke point

Every write of `ScheduledJobs.NextFireAt` funnels through one method:

```
ScheduledJobService.ComputeNextFireAt(job, from)      (ScheduledJobService.cs:756)
  -> _calculator.ComputeNextFireAt(...)               (RecurrenceCalculator.cs:7)
```

Its callers are `CreateAsync:102`, `UpdateAsync:190`, `EnableAsync:286`, `MarkRunCompleteAsync:344`,
`MarkRunFailedAsync`, `MoveOffCurrentOccurrenceAsync:620` (the dispatch/skip/park settle) and
`UpsertFromSyncAsync:702` (the IMPORT leg only). Return the sentinel from the calculator and the row is
never due on any of those paths.

One path it does not cover, and the reason A3 is not optional: a sync pull onto an **existing** row
deliberately leaves `NextFireAt` alone (`:708` — execution state is each device's own), so a routine that
already carries a stale instant keeps it. The due query's recurrence test is what makes that inert.

`GetDueJobsAsync:128` then gets `AND Recurrence <> 'Manual'` as belt-and-braces — not because the sentinel
is expected to fail, but because a hand-edited row or a future writer that bypasses the calculator must not
be able to fire a template.

### 2.2 What does *not* need to change — verified, not assumed

- **No schema migration.** `Recurrence` is persisted as TEXT (read at `ScheduledJobService.cs:773`,
  `Enum.Parse` in `MapJob`). The string `Manual` stores and round-trips with no column change.
- **`MarkRunCompleteAsync` and `MoveOffCurrentOccurrenceAsync` settle on `Recurrence == Once` only**
  (`:315`, `:587`). A `Manual` job therefore never retires itself after a run — it stays `Active` and
  re-runnable, which is the whole point of a template. Both fall into the recurring branch and rewrite the
  sentinel over itself.
- **`BackfillRecurrenceDaysAsync:631`** selects only `Weekly`/`Monthly`/`Yearly` rows in SQL, so `Manual`
  is already excluded. Lock it with a test rather than editing it.
- **`ScheduledFiringReconciler`** reconstructs firings from run rows (`GetAllAsync`, `:33`) and never does
  arithmetic on `NextFireAt`. Unaffected.
- **Failure retirement** is the one exception — see D7. A `Manual` job is not a `Once` job, so today's code
  would give it the recurring five-strike rule, and retirement would take the template out of the picker.
- **`NextFireAt` is not on the sync wire at all** (`SyncScheduledJob.cs:7` — execution state is excluded),
  so the sentinel never leaves the device.

### 2.3 Edits

| # | File | Change |
|---|---|---|
| A1 | `src/Pia.Wpf/Models/Reminder.cs:3` | Append `Manual` to `RecurrenceType`. Add the one-line append-only note, same shape as the one on `ScheduledJobKind`. |
| A2 | `src/Pia.Wpf/Services/Scheduling/RecurrenceCalculator.cs` | Add `public static readonly DateTime Never = new(9999, 1, 1);` and an explicit `RecurrenceType.Manual => Never` arm **before** the `_` arm at `:27`. The default arm falls through to *daily* — that fall-through is how the old `Once`-with-no-date bug hid (recorded at `ScheduledJobService.cs:313`), so `Manual` must never rely on it. |
| A3 | `src/Pia.Wpf/Services/ScheduledJobService.cs:128` | `GetDueJobsAsync`: add `AND Recurrence <> 'Manual'` to the WHERE. |
| A4 | `src/Pia.Wpf/Services/ReminderService.cs:24,115` | Reject `RecurrenceType.Manual` in `CreateAsync`/`UpdateAsync`. A reminder has no manual door, and the enum is shared. |
| A5 | `src/Pia.Wpf/Services/ReminderToolHandler.cs:135,186` | Both sites `Enum.TryParse` model-supplied text. Treat a parsed `Manual` as unrecognised — create falls back to `Once` as it already does for junk, update falls back to `null`. Without this a model asking for a "manual" reminder silently gets a daily one via the calculator's default arm. |
| A6 | `src/Pia.Wpf/ViewModels/RoutinesViewModel.cs` | `EditorWantsTimeOfDay => EditRecurrence != RecurrenceType.Manual`; raise it in `OnEditRecurrenceChanged:442` next to the other three; gate the time clause of `CanSave:345` on it. `SaveAsync` also **parses** `EditTimeOfDay` and refuses a bad format (the `"25:99"` test at `RoutinesViewModelTests.cs:197`) — skip that parse for `Manual` and pass `default(TimeOnly)`, or an empty hidden box fails the save. The day/date fields at `:1260-1263` already null out for a recurrence that does not want them. |
| A6b | `src/Pia.Wpf/ViewModels/RoutinesViewModel.cs:934-936` | The editor seeds `EditDayOfWeek`/`EditDayOfMonth`/`EditMonth` from `NextFireAt` when the row stores no day. On a `Manual` row that seeds them from 9999-01-01, so switching the recurrence to Weekly offers a nonsense default. Fall back to `DateTime.Now` when `NextFireAt >= Never`. |
| A7 | `src/Pia.Wpf/Converters/NextFireAtToShortStringConverter.cs` | Render a value `>= RecurrenceCalculator.Never` as an em dash. Point `RoutinesView.xaml:453` at the converter instead of `StringFormat=g`, so the detail pane's "Next run" does not read `01.01.9999`. |
| A8 | `ViewStrings.resx` / `.de.resx` / `.fr.resx` | `Settings_ScheduledJobs_Recurrence_Manual` — en `Manual only`, de `Nur manuell`, fr `Manuel uniquement`. The label is looked up by enum name at `RoutinesViewModel.cs:525`; `Architecture/LocalizationTests.cs` enforces en/de/fr parity. Edit the three resx files only — never `Designer.cs`. |
| A9 | `src/Pia.Wpf/Services/ScheduledJobToolHandler.cs` | Extend the `create_scheduled_research` description: a routine may be created with `recurrence=manual`, which never fires on its own and is started by hand or with `run_routine`. |
| A10 | `src/Pia.Wpf/Services/Plugins/BuiltInPluginDefaults.cs:82` | The same fact in the scheduled-research plugin's `systemPromptAddition`. |
| A11 | `src/Pia.Wpf/Services/TextOptimizationService.cs:212` | The AI-draft prompt enumerates the recurrence vocabulary (`once`, `daily`, `weekly`, `monthly`, `yearly`) and `:278` parses the answer back. Add `manual` to the list, or a drafted routine can never be a template. |
| A12 | `src/Pia.Wpf/Services/ScheduledJobService.cs` | Per D7: exempt `Recurrence == Manual` from the five-strike retirement arm in `MarkRunFailedAsync`. The counter still increments; only the `Status = 'Failed'` flip is skipped. |

### 2.4 An older peer

A `Manual` job created here has `OwnerDeviceId` = this device, and `GetDueJobsAsync` only returns rows the
local device owns, so a peer on an older build cannot fire it whatever it thinks the recurrence is. The
unknown ordinal `5` arrives, is cast without validation and stored as the string `5`, and
`Enum.Parse<RecurrenceType>("5")` reads it back unchanged — the row round-trips **unless it is edited
there**: the old build's recurrence combo has no row for ordinal 5, so its `SelectedValue` binds to
nothing and a save rewrites the recurrence to whatever the user then picks. The other visible degradation
is a missing localization key on its Routines row, the same tolerance the `ScheduledJobStatus` doc comment
already demands of unknown values.

---

## 3. Workstream B — the `run_routine` tool

### 3.1 Shape

`ScheduledJobToolHandler` gains `IScheduledJobRunner` in its constructor (singleton, `Bootstrapper.cs:906`;
no cycle — `ScheduledJobBackgroundService` takes `IServiceScopeFactory`, never the tool handler) and one
new tool:

```
run_routine(routine: string)
```

`HandleToolCallAsync:70` gets a `"run_routine" => ((object?)null, await PrepareRunJob(args))` arm.
`PrepareRunJob` returns a `ScheduledJobToolCall` (`IScheduledJobToolHandler.cs:5`) whose `TargetJobId` is
the resolved job and whose `Execute` calls `RunNowAsync` and formats the result. That is all the wiring
there is: the pending action reaches the action card through the generic
`BuiltInPluginHandler.FromScheduledJobHandler` adapter (`:142`), which already forwards any pending call.

Error cases return a `ScheduledJobToolCall` with **no** `TargetJobId`, which the existing guard at
`ScheduledJobToolHandler.cs:87` turns into an immediate tool result with no action card shown.

The pending call's `Description`/`Details` **is** the sentence the user confirms, so it must say what is
about to run and where: the routine's name, its kind, and its working directory (root when null).

Constructor ripple: three test files build the handler directly and each needs the new substitute —
`tests/Pia.Wpf.Tests/Services/ScheduledJobToolHandlerTests.cs`,
`tests/Pia.Wpf.Tests/Services/ScheduledJobBlueprintToolTests.cs`,
`tests/Pia.Wpf.Tests/Integration/ScheduledJobToolIntegrationTests.cs`.

### 3.2 Resolving the routine

1. Trim, strip surrounding quotes (the chip inserts `@Routine:"My routine"`).
2. If it parses as a `Guid`, use it.
3. Otherwise match `GetActiveAsync()` by name, case-insensitive: exactly one hit runs; zero hits returns
   "no routine named X" plus the available names; two or more returns "several routines are called X, give
   the id" plus the ids. Never guess.

Consequence to state rather than discover: name resolution reads `GetActiveAsync`, so a **Disabled**
routine is unreachable by name but reachable by id, and `RunNowAsync` itself does not check `Status`. The
policy is to **allow** it, mirroring the Routines view — `CanRunNow` there is ownership-gated only
(`RoutinesViewModel.cs:1709`) — and to say in the result that the routine is disabled and this was a
one-off manual run.

### 3.3 Refusals, each with its own sentence

| Condition | Result |
|---|---|
| `ScheduledJobRunNowResult.Dispatched` | started; it runs as its own agent run and its answer arrives as a new chat |
| `NotOwner` | another device owns this routine's schedule, and only the owner may run it |
| `AlreadyRunning` | a run of this routine is executing right now |
| `NotFound` | deleted underneath the call |
| `job.Kind == MeetingAttendance` | refuse **before** dispatch: run-now would join the Teams meeting immediately. Say so. |

Each maps to a distinct string so the model explains the refusal instead of confabulating a success.

### 3.4 Gate and permissions

- `AssistantPromptComposer.cs:225` — add `run_routine` to the `Routine` domain's tool list, so `@Routine`
  turns load it.
- `ToolPermissionService.cs:107` — add `run_routine` to `AuthorityAuthoringTools`. It commits a run that
  exercises the job's `GrantedTools`, which is the caution `start_assignment` is listed for. Know what that
  list actually does: its only reader is `ToolCatalogRow.CautionFor:115`, so it paints a **caution badge in
  the tool catalog** — it does not force a confirmation. What forces one is the pending action above, and
  whether the card is shown or auto-run is decided by `ToolAutonomy.Resolve`: a user who grants
  `run_routine` "Always", or whose autonomy policy covers `ToolClass.Scheduling` (the class every
  scheduled-research tool lands in), gets it auto-run. That is the user's own auditable decision, the same
  as for every other gated tool, and it cannot reach an unattended run because the whole routine tool set is
  withheld there.
- `AssistantPromptComposer.RoutineToolNames:79` derives from the same mapping, so `run_routine` is
  **automatically withheld from an unattended turn**. That is the behaviour we want — a fired routine must
  not fan out into other routines — and `AssistantPromptComposerUnattendedTests` is where it is pinned.
- `BuiltInPluginDefaults.cs:82` — one sentence in the plugin's `systemPromptAddition`: `run_routine` starts
  a routine as its own run with the routine's own working directory, persona and grants, and the answer
  arrives as a new chat rather than in this one.
- Optional: an `ActionCardBuilder.cs:174` status label plus three resx keys. The scheduled-job tools have
  no rows there today and fall through to `Msg_Assistant_StatusProcessing`; adding one for `run_routine` is
  a nicety, not a requirement.

### 3.5 A note on `@Routines`

The canonical keyword is singular `Routine` (`AtCommandParser.cs:21`, with `Research` as a hidden alias).
`@Routines:X` does not parse as an at-command at all — the domain lookup misses at `:139` and the text
stays plain, so no tool gating happens and the model answers from the raw sentence. The picker always
inserts the canonical form, so this only bites someone typing by hand. No change proposed; recorded so the
symptom is recognisable.

---

## 4. Test plan

The gate is the built exe with no filter, `Failed: 0`:
`tests/Pia.Wpf.Tests/bin/Debug/net10.0-windows10.0.17763.0/Pia.Wpf.Tests.exe > test.log 2>&1`.

New coverage, by claim rather than by file:

1. `RecurrenceCalculator` returns `Never` for `Manual` and does **not** fall into the daily arm.
2. `CreateAsync(..., Manual, ...)` stores `NextFireAt == Never`, and `GetDueJobsAsync` never returns the
   row — including with the clock past every other job's fire time.
3. A `Manual` job survives a completed run: `MarkRunCompleteAsync` leaves `Status == Active` and
   `NextFireAt == Never`. This is the template property, and the one a future refactor of the
   settle-on-`Once` predicate would break.
4. `MarkOccurrenceDispatchedAsync` on a `Manual` job does not flip it to `Completed`.
5. A `Manual` job IMPORTED by `UpsertFromSyncAsync` arrives at `Never`, not tomorrow. (A pull onto an
   existing row leaves `NextFireAt` alone by design, which is what the due-query guard covers.)
6. `BackfillRecurrenceDaysAsync` leaves a `Manual` row untouched.
7. `ReminderService.CreateAsync`/`UpdateAsync` reject `Manual`; `ReminderToolHandler` given
   `recurrence: "manual"` does not produce a daily reminder.
8. `RoutinesViewModel`: selecting `Manual` hides the time field, `CanSave` is true with the time box empty,
   the save succeeds without a parseable time, and the saved job has null day/date fields. Opening a
   `Manual` row and switching to Weekly seeds today's weekday, not 9999-01-01's.
9. `run_routine` resolves by name, by id, refuses on ambiguity, refuses a `MeetingAttendance` job, runs a
   Disabled routine reached by id, and maps each `ScheduledJobRunNowResult` to its own sentence
   (substituted `IScheduledJobRunner`).
10. `run_routine` is in the `Routine` at-command tool set and absent from an unattended turn's tools.
11. Five consecutive failures of a `Manual` job leave it `Active` — the counter climbs, the status does not
    flip (D7).

Zero-warning policy applies: `dotnet build -t:Rebuild -v:n` in Debug **and** Release, `0 Warning(s)`.

Manual smoke, after the gate is green:

- Create a routine, set recurrence to Manual, save, reopen — the row shows "Manual only" and an em dash for
  next run, and survives a restart without firing.
- In a chat: `@Routine:"<name>"` then "start it" — an action card appears, confirming it starts a run whose
  working directory is the routine's, in agent shape, with the answer landing in a new chat.
- The Routines view's own **Run now** button still works on the same routine.

---

## 5. Risks

| Risk | Mitigation |
|---|---|
| A future writer of `NextFireAt` bypasses `ComputeNextFireAt` and arms a template. | Test 2 plus the SQL guard in A3; both fail loudly rather than silently firing. |
| The settle-on-`Once` predicate is later widened to "has no next occurrence", which would catch `Manual` and retire every template after one run. | Test 3 pins it. |
| A model starts a routine the user only asked about. | It returns a pending action, so the default is an action card the user confirms; it is auto-run only where the user has granted it "Always" or opted `ToolClass.Scheduling` into an autonomy policy, and it is withheld entirely from unattended turns. |
| A manual routine fails five times, retires, and disappears from the picker and from name resolution. | D7 exempts `Manual` from retirement; test 11 pins it. |
| A user expects the run "here" and does not notice the new chat. | The dispatched sentence says where the answer lands; the run panel and the completion toast already exist. |

## 6. Out of scope

- **Parameterised templates** ("start it, but for Q3"). Needs a design for how an extra instruction merges
  with the stored `Query` without diluting it — D6.
- **Running a routine inside the current chat** — D4.
- **A "Start" affordance on the `@Routine` picker row.** The Routines view already has Run now for the
  mouse path; revisit only if the tool proves unreliable in practice.
- Promoting a chat or an agent run into a routine template. Separate feature.

---

## 7. Live validation, 2026-09-11

Driven with WinWright against the real profile on this machine, Debug build, Pia Cloud pointed at the
local docker server (`https://localhost:8081/api/ai/chat`). UI language German, which also exercised the
new resx entries.

Confirmed:

- `Nur manuell` is offered and selectable; picking it hides `Routines_Field_Time` and leaves
  `Routines_Save` enabled. The saved row reads `Aktiv · Agenten-Ausführung · Nur manuell`, and
  `Routines_Detail_NextRun` renders `—`.
- `@Routine:"<name>" start it` made the model call `run_routine` with `{"routine":"ZZ Manual template
  test"}` — the chip's name, resolved by name, no `query_scheduled_research` round-trip needed on the
  first attempt. The turn's toolset was the 7 routine tools, `run_routine` among them.
- Approving dispatched the real thing: `Run-now dispatching scheduled job … (AgentTask)` →
  `Created run … shape=Planned state=Planning trigger=Schedule`. An agent run, not a chat turn.
- The tool result came back localized and the model reported the start rather than the answer.
- After a completed run the routine stayed `Aktiv` at `—`, with `Letzte 1 Ausführungen: 1 ok`, and the
  dispatch log reads `schedule moved on to 01/01/9999 00:00`. The template survives its own run.
- The run's own step was sent **no** routine tools at all, so a fired routine cannot start others.
- Declining leaves the routine unstarted; only one dispatch was recorded across two attempts.

One defect found, fixed and re-verified live: the approval card was headed *Geplanter Auftrag
erstellen* ("create") over a body that said start, because `ActionCardBuilder.FormatToolTitle` has no
arm for `run_routine` and falls through to `ActionCard_Action_Create`. Added `ActionCard_Action_Start`
(en/de/fr) and the mapping; the card now reads *Geplanter Auftrag starten*. Pinned by
`ActionCardBuilderScheduledCategoryTests.RunRoutineCard_SaysStart_NotCreate`.

`start_assignment` falls through the same default arm and is titled "create" too — pre-existing, not
touched here.
