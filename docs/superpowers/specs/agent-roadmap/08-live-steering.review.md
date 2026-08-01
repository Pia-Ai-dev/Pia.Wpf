# Batch 08 (live steering) — adjudicated review

**Range reviewed:** `7772602..c4d141b` (eleven commits: G1 `8166f4a`, G2 `968ea9a`, G3 `e24a3b5`, G4 `343db8d`,
G5 `8e36d19`, G6 `68efc02`, G7 `1a67bce`, G8a `566a026`, G8b `462b543`, G8c `9c3f302`, SIMPLIFY `c4d141b`).
**HEAD at review:** `c4d141b`. **Branch:** `feature/agent-run-spine`.

**Six independent refutation lenses fed this file:**

| Lens | Scope | Mode |
|---|---|---|
| 1 | D1 resumability, the two consume sites, the resumability tests | read |
| 2 | the four CASes, D6/D7, persisted-enum discipline, the startup sweep | read |
| 3 | plan mutation, nudge, privacy/logging, service-vs-UI enforcement | read |
| 4 | UI, strings, trilingual parity, executor parity, Batch-14 pins | read |
| 5 | pause round trip, boundaries, stop-vs-pause, restart | **executed** |
| 6 | D6 fan-out cascade, child slot pool, the scheduled (D5) path | **executed** |

**Measured gate at `c4d141b`** (orchestrator, on this tree): Debug `-t:Rebuild -v:n` → 0 Warning(s) / 0 Error(s),
6 genuine `csc.exe`; Release → 0 Warning(s) / 0 Error(s), 6 genuine `csc.exe`. Suite run 1 → 2853 / 1 failed
(the known `AssistantChatConcurrencyTests` intermittent, green 13/13 isolated three times); suite run 2 →
2853 / 0 failed / 2852 passed / 1 skipped. Baseline before the batch 2734; net +119. Lens 5 measured
2866/0 and lens 6 measured 2860/0 on their own trees with their probes added — i.e. three independent
observers agree the suite is green and the batch adds no regression.

**Adjudicator's own gate contribution:** `dotnet build tests/Pia.Wpf.Tests` → 0 Warning(s) / 0 Error(s) with
probes added and again after removing them. I did **not** re-run the full suite; I ran four targeted probes
(below) and deleted them. `git status` is clean apart from this file.

---

## Headline verdict

**The batch's central claim is NOT delivered. A user pause can settle a run terminally `Cancelled`, with
`CompletedAt` stamped and no claim path back, on at least three reachable paths — and I demonstrated one of
them myself, by execution, at `c4d141b`.** The single root cause is that the pause gate
(`AgentRunSteeringService.PauseAsync`, the pausable-state pre-check at `:61`, and `TryPauseUserAsync`'s CAS
source set) is evaluated against the **persisted row**, which does not say where the dispatch actually is. Three
instantiations, three different fixes: (F1) a pause cascaded to a fan-out child that is still `Planning`, or
still queued behind the child slot pool, which cannot honour it and drags the parent to a terminal `Failed`
(executed by lens 6, read-verified by me); (F2) a pause landing inside the fan-out **dispatch prologue**, where
the row already reads `Running` but D6's cascade branch is not yet armed — `FIRE → cts.IsCancellationRequested
→ FanOutResult(Cancelled: true) → SafeFail(cancelled: true)` (**executed by me**: `ROW-IN-PROLOGUE=Running
ACCEPTED=True FINAL=Cancelled COMPLETEDAT=SET RESUMABLE=False`); (F3) a pause landing in the resume ramp-up
between `RegisterDispatch` and `RunAsync`'s revoke-on-entry, which converts a live pause request into a plain
cancel (executed by lens 5 through the real `HeadlessRunLauncher`). Everything the batch *did* pin — the
mid-step pause on both executors, the cascade of a settled `WaitingForChildren` parent, the restart survival,
the four CASes, the plan-mutation service gate — holds up under attack and is genuinely well covered; the
failures are all in the windows the tests structurally wait past. Two further executed findings show the
opposite failure mode as well: a pause that is accepted (`PauseAsync` returns `true`) and then **silently never
happens** — the run runs on and settles `Completed` (F6), or discards a step that had already succeeded (F5).
Separately and unrelated to the pause path, one of the five shipped plan-mutation verbs is dead on arrival:
`EditStepCommand` never receives a `CanExecuteChanged` (**executed by me**: `Edit=0` while the other five fire),
so "Edit step" stays greyed out on every step row that existed before the pause.

---

## Findings table

28 findings were filed across six lenses; they resolve to **21 distinct defects + 2 coverage gaps**. All 23 are
CONFIRMED, one of them narrowed on adjudication (F13); three lens *arguments* were partially refuted or
corrected and are recorded in [Refuted / corrected](#refuted--corrected-claims).

| ID | Sev | Verdict | file:line | Defect | Lens |
|---|---|---|---|---|---|
| F1 | must-fix | CONFIRMED (exec) | `src/Pia.Wpf/Services/AgentRunSteeringService.cs:130` | the cascade fires a cancel at children outside the pausable set (`Planning`) or not yet started (queued) — they cannot become `Paused`, and the parent then settles terminally `Failed` | 6 |
| F2 | must-fix | CONFIRMED (exec, mine) | `src/Pia.Wpf/Services/AgentRunSteeringService.cs:87` + `AgentRunOrchestrator.cs:823`, `:289` | a pause landing in the fan-out **dispatch prologue** (row still `Running`) fires the parent's own token → run settles `Cancelled`, `CompletedAt` stamped, unresumable | 1, 2 |
| F3 | must-fix | CONFIRMED (exec) | `src/Pia.Wpf/Services/AgentRunOrchestrator.cs:142` + `HeadlessRunLauncher.cs:553` | a pause landing between the resume's `RegisterDispatch` and `RunAsync`'s revoke-on-entry is revoked while its cancel stands → run settles `Cancelled` | 5 |
| F4 | must-fix | CONFIRMED (exec, mine) | `src/Pia.Wpf/ViewModels/RunProgressViewModel.cs:70-81` | `EditStepCommand` is the one state-gated row command missing from `_state`'s `[NotifyCanExecuteChangedFor]` list → "Edit step" stays disabled on every pre-existing row after a pause | 4 |
| F5 | should-fix | CONFIRMED (exec, mine) | `src/Pia.Wpf/Services/AgentRunOrchestrator.cs:367` | a pause request that survives a fan-out is consumed against the **next** step, which ran to full success — its work is unrecorded and re-runs on resume (duplicate side effects) | 2 |
| F6 | should-fix | CONFIRMED (exec, mine) | `src/Pia.Wpf/Services/AgentRunSteeringService.cs:77` + `RunProgressViewModel.cs:723` | a cascade that reaches no live child leaves the request unconsumed and fires nothing: the run completes normally and the button sits at "Pausing…" forever | 4 |
| F7 | should-fix | CONFIRMED | `src/Pia.Wpf/Services/AgentRunService.cs:797` + `RunProgressViewModel.cs:794` | only `Title` is validated, but **`Intent` is the only field either executor sends** — an inserted step ships the literal turn `Execute step 3: .` | 3 |
| F8 | should-fix | CONFIRMED (exec) | `src/Pia.Wpf/Services/AgentRunOrchestrator.cs:657` | `siblings.Count < 2` returns before `SafeCancelStaleChildrenAsync`, so a paused child of a mixed/skipped generation is orphaned permanently (survives restart) | 3, 6 |
| F9 | should-fix | CONFIRMED (exec) | `src/Pia.Wpf/Services/AgentRunOrchestrator.cs:214-237`, `:483` | a pause landing inside a **replan** parks the run with the `Failed` step unrepaired and no memory a replan was owed → the resumed run settles `Completed` | 5 |
| F10 | should-fix | CONFIRMED (exec) | `src/Pia.Wpf/ViewModels/AssistantViewModel.cs:761` | **Stop → Pause** (that order) swallows the Stop: the run comes back `Paused` and resumable | 5 |
| F11 | should-fix | CONFIRMED | `src/Pia.Wpf/Services/AgentVerifier.cs:144`, `AgentPlanner.cs:525` | a user-typed step Title/Intent reaches the provider inside the **System** prompt, where `TokenizeMessages` never tokenizes it | 3 |
| F12 | should-fix | CONFIRMED | `src/Pia.Wpf/Controls/Assistant/RunProgressPanel.xaml:82-86` | "Pause the run to change its plan." renders in **every** non-`Paused` state, including `Completed` and `WaitingForInput` where no Pause button exists | 4 |
| F13 | nit | CONFIRMED (narrowed) | `src/Pia.Wpf/ViewModels/RunProgressViewModel.cs:580` | `Project` clears `PlanMutationNote` when the run is not `Paused`, so the `NotPaused` rejection — **and only that one of six outcomes** — is set and instantly wiped by its own refresh | 4 |
| F14 | should-fix | CONFIRMED | `tests/Pia.Wpf.Tests/Architecture/LocalizationTests.cs:89` | the code-key scan cannot see the five keys returned by the `MutationErrorKey` **helper** — the exact shape `T-CONV-3` exists to guard | 4 |
| F15 | should-fix | CONFIRMED (exec) | `src/Pia.Wpf/Services/AgentRunService.cs:784` | a `Skipped` step joins the immutable prefix, so the next mutation hoists it above still-pending steps | 2, 3, 4, 5 |
| F16 | should-fix | CONFIRMED | `src/Pia.Wpf/Services/AgentPlanner.cs:520-529` | the replan prompt lists only `CompletedSteps`, so nothing tells the model a step was **skipped** — it can re-add the work the user removed | 3 |
| F17 | nit | CONFIRMED | `src/Pia.Wpf/ViewModels/RunProgressViewModel.cs:804`, `:817` | "Move up" on the first pending row and "Move down" on the last are enabled and do nothing (documented as deliberate) | 4 |
| F18 | nit | CONFIRMED | `src/Pia.Wpf/Resources/Strings/ViewStrings.resx` `Run_Nudge_Scope_Note` | the string says "the next step … only"; the nudge actually rides every step of the dispatch plus the critic and every replan | 3 |
| F19 | nit | CONFIRMED | `src/Pia.Wpf/Services/HeadlessRunLauncher.cs:606`, `:623`, `:652` | an interrupted resume re-parks a **user** pause as `WaitingForInput`/`resume-interrupted`, so the panel says "stopped at budget" | 5 |
| F20 | nit | CONFIRMED | `docs/superpowers/specs/agent-roadmap/08-live-steering.md:188` | smoke item 3 says every child "resumes"; D6 supersedes and re-dispatches the paused generation instead | 5 |
| F21 | nit | CONFIRMED | `tests/Pia.Wpf.Tests/Views/ViewHostDataContextTests.cs:119` | the failure message still says "all 28 of the panel's paths"; the floor is now 26 and the walk measures more | 4 |
| C1 | coverage | CONFIRMED | `tests/Pia.Wpf.Tests/Services/AgentRunOrchestratorUserPauseLiveTests.cs` | the *throwing* abort shape is pinned Headless-only, though both mechanisms that produce it are Live-specific | 1 |
| C2 | coverage | CONFIRMED | `tests/Pia.Wpf.Tests/Services/D5PausePremiseTests.cs` | no fact combines a **user** pause with a second due job in the same scheduler tick | 6 |

---

## Confirmed findings

### F1 · must-fix · the cascade pauses children that cannot be paused

`src/Pia.Wpf/Services/AgentRunSteeringService.cs:130`

**Defect.** `CascadeToChildrenAsync` filters on terminal-or-not:

```csharp
if (child.State is AgentRunState.Completed or AgentRunState.Failed or AgentRunState.Cancelled)
    continue;
```

Every other gate in the batch uses the explicit pausable set `{Running, Verifying, WaitingForChildren}` — the
parent's own pre-check at `:61` and `TryPauseUserAsync`'s CAS source list. A child outside that set gets
`RecordPauseRequest` + `FireCancel` and **cannot** reach `Paused`. Introduced here, not inherited: at G4
(`343db8d`) a `WaitingForChildren` parent was refused a pause outright; G5 (`8e36d19`) made it pausable and
added this cascade.

Two shapes:

* **Child still `Planning`.** `PlanAsync` throws OCE → the child's own `catch (OperationCanceledException)` at
  `AgentRunOrchestrator.cs:483` consumes the request (returns `true`), `SafePauseUser`'s CAS then **loses**
  (`Planning` is not a source state), and the method returns. The child row is stranded at `Planning(0)`,
  non-terminal, with no dispatch behind it. The parent's per-child switch reads `Planning` → `default:` →
  `anyFailed` with `"child run did not settle"` → both sibling steps recorded **`Failed`** → `AnyParked` is
  false so D6's pause arm at `:313` is never reached → replan → degrade → `SafeFail` → parent terminally
  `Failed` with `CompletedAt`.
* **Child still queued** behind `_childSlots(2,2)` (`HeadlessRunLauncher.cs:56`, acquired *inside* the dispatch
  task at `:382`). `slots.WaitAsync(runCts.Token)` throws with `started == false` → `:411-415`
  `FailAsync(..., cancelled: true)` → **`Cancelled`**, terminal.

**Failure scenario (the most likely real-world timing in the whole batch).** A user runs a task with a persona
roster; the plan has a 2-member `parallelGroup`. The parent dispatches both children and parks at
`WaitingForChildren` **immediately** — the children then spend the next several seconds inside their own
planning LLM call, which is the longest single phase of a child's life. The panel shows the Pause button
(`CanPause` includes `WaitingForChildren`). The user clicks it. Both children are stranded at `Planning`, both
sibling steps are recorded `Failed`, the parent burns a replan and settles **`Failed`** with `CompletedAt`
stamped. Nothing is resumable. The stranded children are cleaned up only by the next app start's sweep.

**Evidence.** Lens 6 **executed** both legs:
`ProbeD_ACascadeReachingAChildStillInPlanning_LeavesItStuckNonTerminalAtPlanning` on the shipped default
profile (`RunProfile.Interactive`, `MaxReplans = 2`) asserts both children `Planning` with null `CompletedAt`,
all steps `Failed`, `ReplanCalls == 1`, `ReplanFailures` containing `"child run did not settle"`, parent
`Failed`, and `TryResumeFromPauseAsync` false; `ARealQueuedChild_CascadePaused_SettlesCancelledInsteadOfPaused`
runs three real `LaunchChildAsync` calls through the **real `HeadlessRunLauncher`** and shows the queued child
`Cancelled` with `CompletedAt` set while the other two are `Paused` with `CompletedAt` null. I independently
re-derived both chains by reading `AgentRunSteeringService.cs:111-142`, `AgentRunOrchestrator.cs:475-492`,
`:765-816`, and `HeadlessRunLauncher.cs:376-416`, and confirmed the G4→G5 provenance from
`git show 343db8d:src/Pia.Wpf/Services/AgentRunSteeringService.cs`.

**Downstream consequence, also executed** (`ProbeA2`): when at least one child *did* pause, `AnyParked` wins and
the parent parks correctly — but the non-paused children's steps stay `Failed`. `NextPendingStepAsync` matches
only `Pending` and `SafeSeedResumeContext` filters `== Done`, so on resume that step is neither re-dispatched
nor shown to the critic, and the run reaches **`Completed`** carrying a `Failed` step nobody retried.

**Recommended fix.** In `CascadeToChildrenAsync`, replace the terminal-exclusion filter with the same explicit
pausable set the parent gate uses:

```csharp
if (child.State is not (AgentRunState.Running or AgentRunState.Verifying or AgentRunState.WaitingForChildren))
    continue;   // outside the pausable set: leave it running rather than cancelling it
```

Leaving a `Planning`/queued child running is the safe direction — the parent re-dispatches a fresh generation
on resume anyway (`SafeCancelStaleChildrenAsync`), so nothing is lost. Then decide the *parent's* side
explicitly: with `cascaded == 0` the parent still has an unconsumed request and no cancel (that is F6), so the
same fix should make the parent's own arm honour the request at its next boundary rather than at none. Add a
fact that pauses a `WaitingForChildren` parent whose children are still `Planning` and asserts the parent
reaches `Paused` (not `Failed`) and every child is either `Paused` or still `Running`.

---

### F2 · must-fix · a pause in the fan-out dispatch prologue settles the run `Cancelled`

`src/Pia.Wpf/Services/AgentRunSteeringService.cs:87`, with `AgentRunOrchestrator.cs:269`, `:289-294`, `:342`,
`:728`, `:823`

**Defect.** D6's protection — "never fire the parent's own token" — is keyed on the **persisted row** reading
`WaitingForChildren` (`AgentRunSteeringService.cs:77`). That state is not written until `SafeBeginChildWait` at
`AgentRunOrchestrator.cs:728`, which runs *after* `SafeCancelStaleChildrenAsync` (`:664`) and after the entire
`LaunchChildAsync` loop (`:675-716`). Throughout that window the row reads `Running` — set by `SafeSetState`
at `:342` for the previous in-process step, by `SafeTryEndChildWait` at `:831` after a previous clean group, or
by the resume CAS. A pause landing there takes the `:87` branch and cancels the parent's own CTS. Then:

* the `cts.Token.Register` callback at `:738-749` revokes and cancels every already-dispatched child;
* `TryFanOutAsync`'s `if (cts.IsCancellationRequested)` at `:823` returns `FanOutResult(Cancelled: true, …)`
  **before** the parent's pause request is ever looked at;
* the caller's `children.Cancelled` arm at `:289-294` calls `SafeFail(run.Id, error, cancelled: true)` →
  `AgentRunService.FailAsync` stamps `State=Cancelled, CompletedAt=@Now` unconditionally.

Neither in-process consume site (`:367`, `:483`) nor the `AnyParked` consume (`:313`) is reached, and
`HeadlessRunLauncher.ResumeAsync`'s state switch (`:464-469`) refuses a `Cancelled` row (`_ => false`).

**Failure scenario.** A run whose plan is shaped `[ordinary step, parallel group]` (or any resumed delegating
parent — the resume CAS puts the row at `Running` and the prologue then supersedes the old generation and
creates N stub chats + run rows, hundreds of ms to seconds). The user clicks **Pause** while the panel shows
`Running` and the Pause button is live. The run settles **`Cancelled`** with `CompletedAt` stamped; the freshly
created children are cancelled; every previously `Done` step is now attached to a terminal, unclaimable run.
*(Not reachable on the very first step of a fresh launch: the row still reads `Planning` there and the
pre-check at `:61` refuses.)*

**Evidence — EXECUTED by me** at `c4d141b`, real `AgentRunOrchestrator` + real SQLite `AgentRunService` + real
`RunSteeringStore` + real `AgentRunSteeringService`, with the child launcher gated inside `LaunchChildAsync` to
hold the loop in the prologue:

```
ROW-IN-PROLOGUE=Running ACCEPTED=True FINAL=Cancelled COMPLETEDAT=SET
PausedCalled=False EndCalled=True EndCancelled=True RESUMABLE=False
STEPS=[s1:Done,g1:Done,g2:Done] REPLANS=0
```

Note the last line: both children actually **completed**, both sibling steps were recorded `Done`, and the run
was then thrown away terminally anyway. `TryResumeFromPauseAsync` returns `false`.

**Why the suite misses it.** Every cascade fact awaits `WaitingForChildren` before pausing —
`AgentRunOrchestratorCascadePauseTests.cs:413`, `:479`, `:527`, `:641` all call
`await h.WaitForStateAsync(run.Id, AgentRunState.WaitingForChildren, ct)` first. The window is structurally
outside the tests, and `PausingAParent_DoesNotFireItsOwnToken_SoItNeverSettlesCancelled` proves its claim only
for a row that has already reached `WaitingForChildren`.

**Recommended fix.** Prefer the structural one:

1. **Do not fire the parent's token anywhere in the fan-out, not just in the persisted `WaitingForChildren`
   window.** Have `TryFanOutAsync` publish "this run is fanning out" to `IRunSteeringStore` at `:664` and clear
   it at `:831`, and branch `AgentRunSteeringService.PauseAsync` on that flag rather than on the row's state.
   The cascade then runs for the whole fan-out and the parent parks at `:313` exactly as D6 intends.
2. If a narrower patch is wanted first, `TryFanOutAsync` can consume the request before the `:823` check and
   return `AnyParked: true` instead of `Cancelled: true`. **State the post-condition accurately if you do**: the
   settle loop restores a sibling step to `Pending` only on the `WaitingForInput or Paused` arm, so on this
   branch the siblings are whatever the loop recorded — `Done` when they finished first (my probe:
   `STEPS=[s1:Done,g1:Done,g2:Done]`), but `Failed` when the `:738` registration callback cancelled them, and a
   `Failed` sibling step never re-runs (see F1's downstream consequence). So this patch must *also* restore any
   sibling step this generation recorded `Failed` back to `Pending` before returning. That extra restore is why
   fix 1 is the better shape.

Add a fact that gates inside `LaunchChildAsync` and asserts `Paused` + `CompletedAt is null` +
`TryResumeFromPauseAsync == true`. (My probe is the red demo; it is deleted, but the harness shape is
`AgentRunOrchestratorCascadePauseTests`'s with the launcher blocking on a `TaskCompletionSource`.)

---

### F3 · must-fix · the revoke boundary is at `RunAsync` entry, not at dispatch registration

`src/Pia.Wpf/Services/AgentRunOrchestrator.cs:142` with `HeadlessRunLauncher.cs:553`, `:591`, `:596`

**Defect.** `ResumeAsync` CASes `Paused → Running` (row now reads `Running`), then calls
`_steering.RegisterDispatch(run.Id, steerCancel)` at `:553` — **synchronously, before `Task.Run` at `:571`**.
`RunAsync` revokes any request for its own run id at `:142`. A pause recorded in the window between
`started = true` (`:591`) and `:142` therefore passes `RecordPauseRequest`'s registration check against the
*new* dispatch's own sink, fires that dispatch's `runCts`, and is then blind-`TryRemove`d at `:142`. The loop
now sees a cancelled token with no request: the step comes back cancelled, `TryConsumePauseRequest` at `:367`
returns **false**, `SafeRecordStep` writes the step `Failed`, and `SafeFail(cancelled: true)` stamps
`CompletedAt` → **`Cancelled`**. `AgentRunSteeringService.PauseAsync` had already returned `true`, so the panel
told the user the pause succeeded.

The panel can reach this: `CanPause` (`RunProgressViewModel.cs:123-126`) does **not** read `IsResuming`, and the
row reads `Running` from the resume CAS onward; `ResumeAsync` returns as soon as `Task.Run` is scheduled.

**Failure scenario.** A user-paused run. The user clicks **Continue**, then a beat later clicks **Pause**
("wait — I wanted to edit the plan first"). The run settles `Cancelled` with `CompletedAt`, its remaining step
`Pending`, and neither claim arm can reclaim it (`ResumeAsync`'s switch is `_ => false`).

**Evidence — EXECUTED by lens 5** through the **real `HeadlessRunLauncher`**, gating at
`_executingRuns.Register` (the last statement before `orchestrator.RunAsync`):

```
LPROBE1 row while ramping up = Running (the panel's Pause is live)
LPROBE1 PauseAsync accepted = True
LPROBE1 FINAL state=Cancelled completedAt=01/08/2026 19:21:42 reason=<none> steps=[S1:Pending]
LPROBE1 resumableFromPaused=False resumableFromWaiting=False
LPROBE0 (baseline, no pause) state=Completed steps=[S1:Done]
```

reproduced independently at orchestrator level (`Probe1_…`).

**Window width — corrected.** Lens 5 described the window as including `RunAsync`'s `if (resume)` block, "which
does `GetAsync` + `SafeRange`, two `_gate`-serialized SQLite operations". That is wrong: lines 121-125 only read
`run.FirstMessageId`/`run.LastMessageId` off the already-loaded object, and `PinRange` at `:130` is a local
function *declaration*, not a call. The real window is `started = true` → `_executingRuns.Register` → task
resumption → `RunAsync` prologue → `:142`: milliseconds, not hundreds. A pause landing **earlier** (during
`_slots.WaitAsync`, which can be long under load) hits `started == false` and re-parks as `WaitingForInput` —
resumable, and only cosmetically wrong (F19). So this is a narrow race, not a wide one — but it is silent,
terminal and unrecoverable when it lands, which is the direction the batch itself calls non-recoverable.
The launch path is not exposed: `LaunchCoreAsync` registers at `:352` while the row still reads `Planning`,
which `PauseAsync:61` refuses.

**Recommended fix — needs an owner decision, not a one-liner.** Today `RecordPauseRequest` attributes a request
to the **new** dispatch (registration-scoped) while `RunAsync:142` attributes it to the **old** one. Pick one.
The structural fix is to move the revoke into `RegisterDispatch` (drop any request at the moment a new sink is
registered), which closes the window by construction — but that turns
`RunSteeringStoreTests.StalePauseRequest_FromAPreviousDispatch_IsNotHonoured` red, because that fact registers a
sink and *then* records, and asserts the request is dropped. The alternative, cheaper and test-compatible, is a
generation counter: `RegisterDispatch` returns a token, `RecordPauseRequest` stamps it, and `RunAsync:142`
revokes only requests stamped with an *older* token. Either way, add a fact that pauses between `started = true`
and `RunAsync` entry and asserts `Paused`.

---

### F4 · must-fix · "Edit step" is dead on every pre-existing row

`src/Pia.Wpf/ViewModels/RunProgressViewModel.cs:70-81` (the attribute block on `_state`) with `:741`

**Defect.** Six row commands are declared `[RelayCommand(CanExecute = nameof(CanMutatePlan))]` — `EditStep`
(`:741`), `SaveStepEdit` (`:768`), `InsertStepBelow` (`:786`), `MoveStepUp` (`:803`), `MoveStepDown` (`:816`),
`SkipStep` (`:830`). Only **five** are listed in `_state`'s `[NotifyCanExecuteChangedFor]` block;
`EditStepCommand` is missing. CommunityToolkit.Mvvm's `RelayCommand` has no `CommandManager` integration, so
`CanExecuteChanged` fires only from an explicit `NotifyCanExecuteChanged()`, and `ButtonBase` caches
`_canExecute` until that event arrives.

**Failure scenario.** Open a chat with a live Planned run: the panel realizes the step rows while
`CanMutatePlan` is false, so each row's buttons hook their command with `CanExecute == false`. Click **Pause**.
The run lands `Paused`, the five-verb group becomes visible, Insert/MoveUp/MoveDown/Skip enable — and **"Edit
step" is greyed out on every row that already existed**. Nothing un-sticks it: a later Skip or Insert leaves
`State` at `Paused`, so `SetProperty` short-circuits and raises nothing. Only re-minting the VM (switch chats
and back) recovers. `InsertStepBelow` mints a *new* row → a new container → that one button hooks fresh and
**is** enabled, so the panel ends up showing one working "Edit step" among several dead ones.

**Evidence — EXECUTED by me.** Subscribing to all six commands' `CanExecuteChanged` and driving
`State = Running → Paused` on the real VM:

```
CANEXECUTECHANGED Edit=0 SaveStepEdit=1 Insert=1 MoveUp=1 MoveDown=1 Skip=1 | CanMutatePlan=True
```

The WPF consequence (a button that stays disabled because its cached `_canExecute` is never refreshed) is
reasoned from `ButtonBase.IsEnabledCore`, not executed. The missing notification itself is demonstrated.

**Why the suite misses it.** `RowCommands_AreDisabledWhileTheRunIsLive`
(`RunProgressViewModelSteeringTests.cs:410`) calls `vm.EditStepCommand.CanExecute(row)` directly, which
recomputes every call; `ADoneRow_HasAllFiveButtonsDisabled_ThroughIsMutable` sets `State = Paused` *before*
loading the row, so the buttons hook already-enabled.

**Recommended fix.** Add `[NotifyCanExecuteChangedFor(nameof(EditStepCommand))]` to the `_state` attribute block
(between `PauseCommand` and `SaveStepEditCommand`). Pin it with a fact that subscribes to every
`CanMutatePlan`-gated command's `CanExecuteChanged` and asserts each fires exactly once on
`Running → Paused` — a count-based fact, so the seventh verb someone adds later goes red rather than silent.

---

### F5 · should-fix · a pause discards a step that already succeeded

`src/Pia.Wpf/Services/AgentRunOrchestrator.cs:367-390`

**Defect.** The parent's pause request is consumed on the `AnyParked` arm (`:313`) or at the in-process step
boundary (`:367`). If a fan-out returns with `AnyParked == false` and the parent's own token was never fired
(the `WaitingForChildren` cascade branch deliberately does not fire it), the request stays pending, the loop
`continue`s at `:339`, the **next** step executes to full success on an un-cancelled token, and `:367` then
consumes the stale request against it. The branch sets the step back to `Pending` (`:373`), skips
`SafeRecordStep` (no ledger entry, no `First/LastMessageId`), skips `ctx.RecordStep`, and never advances
`runFirst`/`runLast`, so `PinRange()` excludes that step's transcript.

This is **not** D2. D2 sanctions discarding an *aborted* step so it re-runs clean; here nothing aborted it.

**Failure scenario.** A fan-out parent with two children. The user clicks Pause at the moment the last child
settles: `AgentRunSteeringService.cs:130` skips every terminal child, `cascaded == 0`, nothing is cancelled.
`TryFanOutAsync` returns `AnyParked: false, AnyFailed: false` → `continue`. The next step (say a `write_file`
into the workspace) executes to completion, writing the file. `:367` consumes the stale request, the step goes
back to `Pending`, its ledger entry and message range are dropped. The user clicks Continue; the step runs
again and writes the file a second time. Same shape via the `AnyFailed` → replan → `continue` route.

**Evidence — EXECUTED by me** (real orchestrator + real SQLite, children settled before the cascade reads
them, plan `[g1(group), g2(group), s3]`):

```
TRAILING=True ACCEPTED=True PARENT-TOKEN-FIRED=False FINAL=Paused COMPLETEDAT=<null>
EXECUTED=[s3] STEPS=[g1:Done,g2:Done,s3:Pending] PausedCalled=True EndCalled=False
```

`s3` ran (`EXECUTED=[s3]`) and is back at `Pending` with nothing recorded.

**Recommended fix.** Make the request's consumption boundary explicit rather than incidental: at the top of the
drain loop, *before* `TryFanOutAsync`/`ExecuteStepAsync`, check for a pending request and park there
(`PinRange` → `SafePauseUser` → `SafeOnPaused` → return) instead of starting another step. That also fixes F6.
Keep the existing `:367` consume for the abort case; the pre-step check simply means a request can no longer
outlive a step boundary unhonoured.

---

### F6 · should-fix · an accepted pause that silently never happens

`src/Pia.Wpf/Services/AgentRunSteeringService.cs:77` with `RunProgressViewModel.cs:723-739`

**Defect.** Same precondition as F5 — a `WaitingForChildren` cascade that reaches no live child (`cascaded == 0`)
records the parent's request and fires nothing — but the opposite ending: if there is no further step, the run
drains, verifies and settles **`Completed`**. The request is never consumed. Meanwhile `Pause()` discards
`PauseAsync`'s `bool` entirely and sets `IsPausing = true` unconditionally, with no `finally`; `Project` clears
it only once the run leaves `{Running, WaitingForChildren}`. So a refused or unhonourable pause is
indistinguishable from a slow one: the button reads "Pausing…" and stays disabled, with no note and no retry.

**Failure scenario.** "I pressed Pause and the run finished anyway, and the button was stuck." Reachable
whenever the user pauses a delegating parent whose children have all just settled — and also on the plain
refusal paths (`run not found`, not pausable, not dispatched in this process), where `PauseAsync` returns
`false` and the VM ignores it.

**Evidence — EXECUTED by me** (same probe as F5, with the group as the last step):

```
TRAILING=False ACCEPTED=True PARENT-TOKEN-FIRED=False FINAL=Completed COMPLETEDAT=SET
EXECUTED=[] STEPS=[g1:Done,g2:Done] PausedCalled=False EndCalled=True
```

**Recommended fix.** Two independent halves:
1. Service: the pre-step park of F5 makes an accepted request always land somewhere; additionally, have
   `CascadeToChildrenAsync` return `cascaded` and let `PauseAsync` fall through to the `FireCancel` branch when
   it is `0` **only if** the parent is genuinely not fanning out (see F2's fix 2) — otherwise the parent is
   still safe to park at its next boundary.
2. VM: `Pause()` should honour the return value — on `false`, set `PlanMutationNote`/a new
   `Run_Pause_Error_Refused` line and reset `IsPausing = false`; wrap the call so an exception resets it too.

---

### F7 · should-fix · the validated field is not the field the model receives

`src/Pia.Wpf/Services/AgentRunService.cs:797-799`, `:814`; `src/Pia.Wpf/ViewModels/RunProgressViewModel.cs:794`

**Defect.** `ApplyPlanMutationAsync` requires only `Title` (`TitleRequired`); `Intent` is
`NullIfBlank(NormalizeStepText(...))`, i.e. optional. But **both executors build the step turn from `Intent`
alone**:

* `src/Pia.Wpf/ViewModels/Models/ChatSession.cs:790` — `instruction = $"Execute step {spec.Ordinal + 1}: {spec.Intent}.";`
* `src/Pia.Wpf/Services/HeadlessTurnExecutor.cs:550` (called from `:284`) — `BuildInstruction(step.Ordinal, step.Intent ?? string.Empty, …)`

Neither ever reads `step.Title`. `AgentPlanner.cs:354` drops a planner step whose `Intent` is blank, so
`ApplyPlanMutationAsync` is the **first writer in the codebase** that can persist a Pending step with a null
Intent. `InsertStepBelow` (`RunProgressViewModel.cs:794`) mints exactly that:
`new PlanStepEdit(null, _localization["Run_Plan_NewStep_Title"], null, null)`. Compounding: the inline editor's
two boxes (`RunProgressPanel.xaml:150`, `:152`) carry no label and no `PlaceholderText`, so nothing tells the
user which one reaches the model.

**Failure scenario.** Pause a run → "Insert step below" → Continue. The run sends the user message
`Execute step 3: .`, burns a step against the budget, bills the tokens, records the step `Done`, and its title
("New step") then enters the verify prompt as *completed work*. Manual-round item 4 ("watch the run honour each
on its next step") is not met for `insert`, nor for an `edit` that only touches the first box.

**Recommended fix.** In `ApplyPlanMutationAsync`, fall back rather than validate a second field:
`Intent = NullIfBlank(NormalizeStepText(edit.Intent, MaxStepIntentChars)) ?? title;` — the title is already
required, normalized and capped, and an intent-less step then reads as "do what the title says". Also give the
two editor boxes `PlaceholderText="{loc:Str Run_Plan_Edit_Title}"` / `…_Intent` (new keys, EN/DE/FR).

---

### F8 · should-fix · a one-member group orphans the previous generation's paused children

`src/Pia.Wpf/Services/AgentRunOrchestrator.cs:657`

**Defect.** `SafeCancelStaleChildrenAsync` (`:664`) is the only cleanup for a previous child generation, and
`TryFanOutAsync` early-returns at `:657` when `siblings.Count < 2` — counting **Pending** members only
(`SafeSiblingGroupAsync:932`). D6's cascade leaves each sibling step back at `Pending`, i.e. fully mutable in
the panel, and a `Paused` child is never swept (`State < 3`).

**Failure scenarios (two triggers, one defect).**
* *Mixed generation* (lens 6): one child `Done`, one `Paused` → one Pending member remains → the resumed
  parent returns at `:657` before the supersede → the paused child is orphaned. After the parent settles
  `Completed`, the child is still `Paused` with null `CompletedAt` and a live stub chat, and
  `FailInterruptedRunsAsync` leaves it there — a **permanent** orphan, not one a restart cleans up.
* *User skip* (lens 3): pause a 2-way fan-out → both children park, both sibling steps go `Pending` → the user
  clicks "Skip step" on one → Continue → the group has one Pending member → same early return, same leak. So
  does skipping both.

**Evidence.** Lens 6 **executed** the mixed-generation leg
(`ProbeC_AMixedGenerationCascadePause_OrphansThePausedChildForeverAcrossARestart`) including the restart. I
read-verified the early return's position relative to `:664` and confirmed no other cleanup caller of
`GetChildRunsAsync` exists in `src/`. The G5 commit message discloses the mechanism verbatim and defers it as
an owner call; the restart leg is new information.

**Recommended fix.** Hoist the stale-generation cleanup above the group-size test: move
`await SafeCancelStaleChildrenAsync(_childLauncher, run.Id, cts.Token)` to immediately after the
`run.ParentRunId is not null` depth guard at `:654`, before `SafeSiblingGroupAsync`. It is idempotent
(terminal children are skipped) and failure-isolated, so running it on every fan-out evaluation costs one
`GetChildRunsAsync` and removes the leak for every group size.

---

### F9 · should-fix · a pause during a replan loses the repair

`src/Pia.Wpf/Services/AgentRunOrchestrator.cs:214-237` with `:483`

**Defect.** `TryReplanAfterFailureAsync` awaits `_planner.ReplanAsync(..., cts.Token)` with no local catch. A
pause landing there throws OCE into the outer arm at `:475`, which consumes the request and parks the run
`Paused` — correctly, with `inflightStepId` already null so nothing is wrongly restored. But the plan is left
holding the `Failed` step the replan was going to repair, and: `NextPendingStepAsync` filters `Pending` so the
step never re-runs; `SafeSeedResumeContext` filters `== Done` so the critic is never told it failed; `replans`
is a `RunAsync` local so the resumed dispatch has no memory a replan was owed. The resumed run drains the rest
and settles **`Completed`**.

**Failure scenario.** A 4-step run; step 2 fails; the panel still shows Running while the replan turn is in
flight; the user clicks Pause to inspect. The run parks `Paused` (reason `user`) with step 2 `Failed`. The user
clicks Continue. The run executes steps 3-4 and reports **Completed** — step 2's work was never repaired and
the failure never reached the critic. Pre-Batch-08 this interleaving settled `Cancelled`, so the shape is new.

**Evidence — EXECUTED by lens 5** (`Probe4_PauseDuringReplan`), pausing from inside a hooked `ReplanAsync`:

```
AFTER PAUSE:  state=Paused completedAt= reason=user   step 0 s1 = Failed  step 1 s2 = Pending
AFTER RESUME: state=Completed executed=[s2] replans=0 step 0 s1 = Failed  step 1 s2 = Done
```

**Honest caveat, and why this is should-fix rather than must-fix:** a *real* critic could still reject the run,
triggering a replan whose `KeepDoneAsync` drops the `Failed` row. The safety net is a model verdict, not an
invariant, and lens 5's probe used the default-accepting `FakeVerifier`.

**Recommended fix.** On the pause branch of the outer `catch (OperationCanceledException)`, restore the failed
step for re-planning: if the run has a step at `AgentStepStatus.Failed` with no successor plan, set it back to
`Pending` alongside the `inflightStepId` restore — or, more precisely, persist the owed replan by recording the
failure reason in the pause envelope (`RunPauseEnvelope`) and seeding `replans`/`ctx` from it on resume. The
cheap version (restore the `Failed` step to `Pending`) at least makes the resumed run re-attempt the work
instead of silently reporting success.

---

### F10 · should-fix · Stop → Pause swallows the Stop

`src/Pia.Wpf/ViewModels/AssistantViewModel.cs:761` (`ExecuteCancelStreaming` → `RevokeAnyPendingPause`)

**Defect.** `RevokeAnyPendingPause()` correctly runs *before* `session.Cancel()`. But the step then takes time
to unwind, during which the row still reads `Running`, so the panel's Pause button is still enabled. A Pause
pressed in that window re-arms the request; the unwinding loop consumes it at `:367` and parks the run instead
of settling it. `AgentRunSteeringService.PauseAsync` reads only `run.State` and cannot see that the dispatch is
already unwinding terminally. `IRunSteeringStore`'s own FAILURE DIRECTION paragraph calls this direction
non-recoverable: *"a run the user wanted terminated comes back resumable."*

**Failure scenario.** A live Planned run is doing something unwanted. The user hits **Stop**; the stream takes a
second to unwind and nothing visibly changes; they hit **Pause** too. The run sits `Paused` with a Continue
button instead of `Cancelled`.

**Evidence — EXECUTED by lens 5** (`Probe6_StopThenPause`):
`state=Paused reason=user PausedCalled=True EndCancelled=False`. Coverage gap confirmed by read:
`StopButton_RevokesAPendingPause_AndTheRunSettlesCancelled` covers only Pause-then-Stop.

**Recommended fix.** Make terminal intent sticky rather than a one-shot revoke: add
`IRunSteeringStore.MarkTerminating(runId)` (set at the four revoke sites) which `RecordPauseRequest` then
refuses, cleared by `ReleaseDispatch`. Alternatively, and cheaper, have `ExecuteCancelStreaming` clear the
panel's affordance immediately (`IsPausing = true` / a `IsStopping` flag feeding `CanPause`). Either way it
should become a written decision with a fact, since "last click wins" is also a defensible reading.

---

### F11 · should-fix · user-typed plan text reaches the provider inside the System prompt

`src/Pia.Wpf/Services/AgentVerifier.cs:144` (and the fact line at `:351`), `src/Pia.Wpf/Services/AgentPlanner.cs:525`

**Defect.** `ctx.RecordStep(step, r)` stores the persisted (user-edited) `Title`/`Intent` into
`ctx.CompletedSteps`. `AgentVerifier.BuildVerifyMessages` appends
`$"- [{…}] {Flatten(c.Title)}: {Flatten(c.Intent)}"` into the `StringBuilder` that becomes
`new(ChatRole.System, sb.ToString())` at `:163`; `AgentPlanner.BuildReplanMessages` does the same at `:525`
into `new(ChatRole.System, …)` at `:539`. `TokenizingAiClientService.TokenizeMessages` (`:263`) short-circuits
on `msg.Role != ChatRole.User`. So the same edited text is **tokenized** when it rides the step instruction (a
User message) and **untokenized** when it rides the verify/replan System prompt of the same run. Planner-emitted
titles already sit there but carry PII *placeholders* (they came back from a model that was given tokenized
input); a title typed in the run panel is raw user keystrokes written straight to the DB.

**Failure scenario.** PII tokenization ON. Pause a run → edit a pending step's intent to
`"Mail the signed contract to john.doe@acme.com"` → Continue. The step turn ships `[EMAIL_1]`; the verify turn
and any replan turn ship `john.doe@acme.com` verbatim in the System message.

**Not a re-argument of D3/D4:** the batch did implement D3's logging rule (titles appear only in
`SensitiveDebug`) and D4's nudge rule (the nudge rides User only, pinned by `AgentRunNudgeParityTests`). This is
D4 item 7's System-prompt rule applied to the *other* class of user text in the same batch, and the nudge's
correctness is the strongest evidence it is an oversight rather than a decision.

**Recommended fix.** Tokenize the two completed-step lines at the point of composition: have `AgentVerifier` and
`AgentPlanner` route `c.Title`/`c.Intent` through the same token-map service the User path uses before
appending, or (simpler and consistent with the nudge precedent) move the "Steps executed" block out of the
System message into the User message that already carries `ctx.AppendNudge(ctx.Goal)`.

---

### F12 · should-fix · "Pause the run to change its plan." shows on runs that cannot be paused

`src/Pia.Wpf/Controls/Assistant/RunProgressPanel.xaml:82-86`

**Defect.** The note binds `Visibility` to `CanMutatePlan` through `InverseBooleanToVisibilityConverter`, and
`CanMutatePlan => State == Paused` (`RunProgressViewModel.cs:144`). So it renders in **every** state except
`Paused` — `Completed`, `Failed`, `Cancelled`, `Planning`, `WaitingForInput`. The impl spec §13 8b states the
condition as "whenever the run is **live**".

**Failure scenario.** A run parks at its budget → `WaitingForInput`. The panel shows Continue and, underneath,
"Pause the run to change its plan." — but `CanPause` is false for `WaitingForInput`, so there is no Pause button
to press; the instruction is impossible to follow. Second instance: a run that completed an hour ago still
carries the nagging line forever.

**Recommended fix.** Add `public bool ShowPauseFirstNote => CanPause;` (a live run is exactly a pausable one) or
`=> State is RunProgressState.Running or RunProgressState.WaitingForChildren or RunProgressState.Planning;`, bind
the note to it with `BooleanToVisibilityConverter`, and add it to `_state`'s `[NotifyPropertyChangedFor]` list.

---

### F13 · nit · the `NotPaused` rejection — and only that one — is wiped by its own refresh

`src/Pia.Wpf/ViewModels/RunProgressViewModel.cs:580` with `:849-868`

**Defect, and its exact extent.** `ApplyStepEditsAsync` sets `PlanMutationNote` and *then* awaits
`RefreshAsync()`; `Project` clears `PlanMutationNote` whenever `State != Paused` (`:580-581`). Five of the six
outcomes (`TitleRequired`, `UnknownStep`, `EmptyPlan`, `TooLong`, `WriteFailed`) leave the run still `Paused`,
so their notes survive the refresh and display correctly — I verified this against the service, which returns
those outcomes only after passing the `state != AgentRunState.Paused` gate at `AgentRunService.cs:778`. The one
exception is `NotPaused`, which by definition means the row is no longer `Paused`, so the note is set and
immediately wiped by the very refresh meant to surface it. (Two narrow holes in that: if `GetAsync` returns
null `RefreshAsync` returns early and the note survives; and if the run re-parked to `Paused` between the
mutation and the refresh, it survives too.) So this is one outcome of six, on a path where the run has already
left the user's control — hence nit, not should-fix.

**Failure scenario.** The run is Paused; the user clicks **Skip**; concurrently the Flow card's "Continue run"
(or a second window) resumes it. The service returns `NotPaused`, nothing is written, the note is set and
cleared, `CanMutatePlan` flips false and the whole row-button group vanishes. The user's skip disappeared with
zero feedback.

**Recommended fix — one line, no new machinery.** In `ApplyStepEditsAsync`, set the note *after* the refresh
rather than before it: `await RefreshAsync(); PlanMutationNote = result.Outcome == Applied ? null : _localization[...];`
`Project`'s clear then runs first and cannot wipe the note it was never about. Do **not** add a freshness flag —
that is more machinery than the defect is worth.

---

### F14 · should-fix · five mutation-error keys are outside the localization scan

`tests/Pia.Wpf.Tests/Architecture/LocalizationTests.cs:89-96` with `RunProgressViewModel.cs:870-879`

**Defect.** G2 widened the code-key regex array with `_localization\["(\w+)"\]`, but the six `Run_Plan_Error_*`
keys are consumed as `_localization[MutationErrorKey(result.Outcome)]` — a helper call, not a literal — so five
of them (`NotPaused`, `UnknownStep`, `TitleRequired`, `EmptyPlan`, `TooLong`) match no regex.
`Run_Plan_Error_WriteFailed` is covered only because it also appears as a literal in the `catch`. The impl spec
§14's claim that "the remaining seven are covered from the moment they land" is false as shipped. The same file
already has a dedicated guard for the identical shape two screens down (`T-CONV-3`, added because
`RunStateToLabelConverter.LabelKey` "moved the literals out of reach of the regex").

**Failure scenario.** Someone renames or drops `Run_Plan_Error_TooLong` in `ViewStrings.resx`. The suite stays
green (parity is resx-to-resx and no code scan sees the key). A user who submits a plan over
`RunProfile.MaxStepsCap` sees the literal `[Run_Plan_Error_TooLong]` in the panel.

**Recommended fix.** Add a `T-CONV-3`-shaped fact: enumerate `PlanMutationOutcome` and assert every value's
`MutationErrorKey(...)` result exists in the resource manager. Make `MutationErrorKey` `internal static` so the
test can call it (there is precedent in the same file).

---

### F15 · should-fix · a skipped step is hoisted above still-pending steps

`src/Pia.Wpf/Services/AgentRunService.cs:784`

**Defect.** `prefix = persisted.Where(s => s.Status != AgentStepStatus.Pending).OrderBy(s => s.Ordinal)` and
`rows = prefix + tail`, with ordinals reassigned prefix-first. A `Skipped` step is not Pending, so on the *next*
mutation it is hoisted out of position and re-ordinaled ahead of the whole remaining Pending tail.

**Failure scenario.** Plan `[0 Done, 1 Done, 2 Pending, 3 Pending, 4 Pending]`. The user skips step 4 (order
preserved — the skipped row rides in the submitted tail). The user then edits step 2 → prefix `[0,1,4]`, tail
`[2,3]` → persisted order `[0,1,4,2,3]`. `SyncSteps`'s `Steps.Move` pass repaints it faithfully: the skipped row
teleports above two pending ones, and `KeepDoneAsync` carries that order into the replan prompt's completed-work
listing. No execution-order impact — a skipped step never drains — but the panel shows a plan order the user
never arranged.

**Evidence.** Lens 5 **executed** it through the real VM commands against the real service:
`rows=[a:Done,c:Skipped,d:Pending,b:Pending]` after a skip of `c`. Read-verified by me at `:784`.

**Recommended fix — and a warning about the obvious one.** The tempting patch (narrow the prefix filter to
`Done or Failed` so `Skipped` rides the tail) is **two-sided and touches an invariant**, so do not apply half of
it: (a) the service must also add `Skipped` rows to the `editable` dictionary, or every resubmission of one
returns `UnknownStep`; (b) the VM must include `Skipped` rows in all five verbs' submissions, which today are
built from `Steps.Where(r => r.Status == AgentStepStatus.Pending)` only, so a `Skipped` row would otherwise be
silently *dropped* from the plan; (c) it must keep refusing un-skipping (`edit.Skip == false` on an
already-`Skipped` id → `UnknownStep`); and (d) it weakens the stated property at `AgentRunService.cs:849-851`
from "no settled row can move" to "no `Done`/`Failed` row can move". Given the defect is cosmetic — a `Skipped`
step never drains — the proportionate action is to **document it** (a line in the panel's XAML comment and in
`ApplyPlanMutationAsync`'s prefix comment saying a skipped step sorts with the settled work), and only take the
four-part change if the owner decides plan order must read as the user arranged it.

---

### F16 · should-fix · a replan can re-add the work the user skipped

`src/Pia.Wpf/Services/AgentPlanner.cs:520-529` with `AgentRunOrchestrator.cs:524-537`

**Defect.** W13's fix is real and correct — `KeepDoneAsync`'s filter is now `Done or Skipped` and
`SafeSeedResumeContext` correctly stays `== Done`, so a skipped step's row survives a replan and its
`ExpectedArtifact` is never probed. But `BuildReplanMessages` lists only `ctx.CompletedSteps` (Done-only), so
the replanner is never told a step was *skipped*, and nothing in the prompt forbids regenerating it.
`IAgentRunService`'s own doc for `PlanStepEdit.Skip` claims "a replan must not quietly re-add work they
removed"; only the row half is delivered.

**Failure scenario.** Pause → skip "Delete the old backups" → Continue → a later step fails → replan → the
model, seeing only the goal and the completed steps, emits a fresh "Delete the old backups" step and the run
does the work the user removed.

**Recommended fix.** In `BuildReplanMessages`, after the "Completed so far" block, append the skipped titles
from the persisted plan under an explicit instruction — e.g.
`"The user REMOVED these steps from the plan. Do not re-add them or their work:"` followed by
`- {Flatten(title)}` per `Skipped` row. `ReplanAsync` already has `ctx`; the skipped list needs to be seeded
into `RunContext` alongside `CompletedSteps` (a `SkippedTitles` list populated by `SafeSeedResumeContext` and by
`ApplyPlanMutationAsync`'s outcome projection).

---

### F17-F21 · nits

* **F17** `RunProgressViewModel.cs:804`, `:817` — "Move up" on the first pending row and "Move down" on the last
  are enabled (`IsMutable` only) and return without a service call, no note, no visual change. The VM comments
  document it as deliberate; a `CanMoveUp`/`CanMoveDown` on `StepRowViewModel` would close it.
* **F18** `Run_Nudge_Scope_Note` (`ViewStrings.resx` + `.de`/`.fr`) says the note is "sent with the next step of
  this continuation only". `ctx.Nudge` is set once per dispatch and `AppendNudge` is called on **every** step of
  that dispatch plus the critic (`AgentVerifier.cs:166`) and every replan (`AgentPlanner.cs:542`); the fence text
  itself says "follow it for the remaining steps". Fix the string (all three files), not the code. The
  "not saved / does not survive a restart" half is accurate.
* **F19** `HeadlessRunLauncher.cs:606`, `:623`, `:652` — all three re-park arms call
  `PauseAsync(runId, "resume-interrupted")`, a blind write to `WaitingForInput`. A run the *user* paused comes
  back as a budget park, and `DescribePause` (`RunProgressViewModel.cs:640-646`) falls through to
  `Run_Activity_WaitingAtBudget` ("stopped at budget"). Still resumable, so cosmetic — but it also means the
  reason token the panel shows is wrong about who paused the run.
* **F20** `docs/superpowers/specs/agent-roadmap/08-live-steering.md:188` — smoke item 3 says "every one
  resumes"; the shipped D6 behaviour (pinned by
  `ResumingAPausedParent_SupersedesThePausedGeneration_AndDispatchesAFreshOne`) is that the paused generation is
  **superseded and re-dispatched fresh**. Not re-arguing D6 — the smoke script will just fail against reality.
* **F21** `tests/Pia.Wpf.Tests/Views/ViewHostDataContextTests.cs:119` — the failure message still says "all 28
  of the panel's paths". I verified `RunProgressPanelParseTests.MinimumBoundPaths` is now 26 (raised from 18 by
  this batch); the actual measured walk count is lens 4's figure of 36 and is **unverified by me**. Either way
  the "28" predates this batch's additions. Message-only; no assertion depends on it.

---

## Coverage gaps (not defects)

* **C1** — `AgentRunOrchestratorUserPauseLiveTests` has no fact for the *throwing* abort shape, although both
  mechanisms that produce it are Live-specific: `LiveTurnExecutor.cs:127` awaits `_stepPersonas.ResolveAsync`
  **outside** `PostAsync` (an OCE there leaves the step row `Running`), and `:232`'s
  `catch (OperationCanceledException) { tcs.SetCanceled(); }` makes `ExecuteStepAsync` throw rather than return.
  The shared orchestrator arm at `:483` *is* pinned headless
  (`UserPause_WhoseStepThrowsInsteadOfReturning_AlsoLeavesTheRunResumable`, whose own doc comment says G4 would
  otherwise be the first place it is exercised "for Live only"), so this is debt, not a live defect. A
  regression that mis-scoped `inflightStepId` would leave a Live-paused run at `Paused` with its step still
  `Running(1)` — invisible to `NextPendingStepAsync`, silently skipped on resume — and nothing would red.
* **C2** — no fact combines a **user** pause with a second due job in the same scheduler tick.
  `ParkedScheduledRun_DoesNotBlockTheNextDueJobOfTheSameTick` covers the `_runLock` release only for a budget
  park. The release is in a `finally` and does not discriminate pause kind, so lens 6 explicitly did **not**
  claim it fails — an uncovered leg, not a defect.

---

## Refuted / corrected claims

Recorded because a refutation reasoned from the code is a result. Nothing below is left without a verdict.

**Corrections to confirmed findings (the finding survives, the argument did not):**

1. **Lens 5's F3 window description — REFUTED in part.** Lens 5 wrote that `RunAsync`'s revoke at `:142` sits
   behind "`GetAsync` + `SafeRange`, two `_gate`-serialized SQLite ops". It does not: lines 121-125 only read two
   fields off the already-loaded `AgentRun`, and `PinRange` at `:130` is a local function declaration. The window
   is `started = true` → `RunAsync:142`, i.e. milliseconds. The finding stands on lens 5's executed evidence; the
   claimed width does not. Also corrected: a pause landing during `_slots.WaitAsync` (`started == false`) re-parks
   as `WaitingForInput` and stays resumable, so the slot-wait period is **not** part of the dangerous window.
2. **Lens 1's and lens 2's F2 window boundary — narrowed.** Both put the whole prologue in the window. The first
   two awaits (`SafeSiblingGroupAsync`, `SafeCancelStaleChildrenAsync`) swallow OCE inside their own
   `catch (Exception)`, so a cancel landing there yields an empty sibling list → `siblings.Count < 2` → the step
   runs in-process → the normal `:367` consume → `Paused`. The reachable bad window starts at the
   `foreach (var sibling in siblings)` launch loop, whose `catch (Exception)` at `:707` swallows OCE into
   `anyFailed`. My probe gates inside `LaunchChildAsync`, i.e. squarely inside the reachable window.
3. **Lens 2's "`TryPauseUserAsync`'s `WaitingForChildren` source is unreachable" — CONFIRMED as an observation,
   and it sharpens F2.** `SafePauseUser` is only ever called after `TryFanOutAsync` has returned, by which point
   the un-park CAS has moved the row to `Running`. So the W6 defence guards a state no caller presents, while the
   state that *is* presented mid-prologue (`Running`) has no branch that consumes the request. Not a defect on its
   own; recorded because it explains why the batch believed the window was covered.

**Claims examined and found clean (refutation attempts that failed):**

4. **"A pause leaves a resumable run" on the paths the batch tests — HOLDS.** `TryPauseUserAsync`
   (`AgentRunService.cs:405-425`) is a single-statement CAS over the explicit set
   `{Running, Verifying, WaitingForChildren}` with **no `CompletedAt`**; `TryResumeFromPauseAsync` (`:455-475`) is
   the disjoint single-source CAS from `Paused`. `UserPause_MidStep_LeavesTheRunResumable_OnHeadless` and its Live
   sibling both drive the *full* round trip to `Completed` with the aborted step re-run — they are not
   "assert `Paused` and stop" — and `HeadlessRunLauncherTests.Resume_ClaimsAUserPausedRun_AndDrainsItToCompletion`
   does it through the real launcher. Lens 5's independent `LPROBE0` baseline agrees.
5. **Double-run on resume — NOT REACHABLE.** The four CASes are disjoint by source state
   (`TryBeginResumeAsync` {`WaitingForInput`}, `TryPauseUserAsync` {`Running`,`Verifying`,`WaitingForChildren`},
   `TryResumeFromPauseAsync` {`Paused`}, `TryEndChildWaitAsync` {`WaitingForChildren`}), all single
   `UPDATE … WHERE Id=@Id AND State…` statements under one connection and `lock (_gate)`, each moving its ledger
   clock only on `affected > 0`. Lens 5 executed it: two concurrent resume claims → `[True,False]`; two concurrent
   pauses → one request, consumed once.
6. **`TryBeginResumeAsync` erasing a user pause's envelope — REFUTED.** `ExtraJson=NULL` is unconditional inside
   the SET clause, but the statement is `AND State=@Expected` with `@Expected = WaitingForInput`; a `Paused(4)` row
   matches zero rows.
7. **Kill/restart while paused — HOLDS.** Sweep statement 1 is `WHERE State < WaitingForInput(3)`, so `Paused(4)`
   survives with its envelope; statement 2 keys on `WaitingForChildren` only. Lens 5 executed it: solo-paused,
   cascade-paused parent and both paused children all survive with null `CompletedAt` and reason `user`, and are
   reclaimable; only the genuinely mid-flight control is cancelled. The harder shape (crash with the parent at
   `WaitingForChildren` and a child already `Paused`) re-parks the parent `WaitingForInput`/`children-interrupted`
   with its fan-out step back to `Pending` and leaves the child resumable.
8. **Boundaries — HOLDS.** Lens 5 executed all six non-pausable states (`Planning`, `Completed`, `Failed`,
   `Cancelled`, `WaitingForInput`, `Paused`): every one refuses quietly, changes no row and leaves no request
   behind. A pause landing inside the terminal critic parks correctly and resumes to `Completed` re-running
   nothing.
9. **D7 / no state ranges — CLEAN.** A full `src/` scan for `State`/`Status` against `< > <= >=` returns exactly
   one live range, the sanctioned startup sweep. The SIMPLIFY commit `c4d141b` did not smuggle one in:
   `AgentRunStates.IsParked` is `state is WaitingForInput or Paused`, identical to the two explicit sets it
   replaced, and the fan-out `case` at `:798` correctly stayed a literal pattern with `null` falling to
   `default:`. `AgentEnums.cs` gained no member and renumbered none.
10. **The G6 panel↔service handover — CLEAN, and this was the seam most expected to break.** All five verbs build
    their submission from `Steps.Where(r => r.Status == AgentStepStatus.Pending)`, so a `Skipped` row is never
    resubmitted and the predicted blanket `UnknownStep` never occurs. Lens 5 executed the adversarial set:
    whitespace-only title → `TitleRequired`; a step id from another run → `UnknownStep`; a duplicate id →
    `UnknownStep`; mutating a `Running` run → `NotPaused`. Ordinals are assigned service-side `0..n-1`, so
    duplicate/negative/gapped/cross-boundary ordinals are unrepresentable rather than merely rejected.
11. **The D5 scheduled premise — STRONGER than G1 left it, not weaker.** G5 re-pinned it with a **real** user
    pause (`PausedScheduledRun_AdvancesTheScheduleAndFailsNothing`, driving the real launcher, orchestrator,
    steering service and `ScheduledJobBackgroundService`), with a non-vacuous negative twin. Lens 6 ran both:
    green.
12. **Child slot-pool deadlock / exhaustion — NO DEFECT.** `if (acquired) slots.Release()` is unconditional in
    the dispatch `finally`, so a cascade-paused child always returns its slot; a child never delegates, so the
    nested-acquire deadlock `_childSlots` exists to prevent cannot arise via pause. Lens 6 paused *and resumed* a
    fan-out across a saturated 2-wide pool with hard timeouts and nothing hung.
13. **Executor parity — NO CELL DIFFERS.** All seven verbs are available or refused identically on Live and
    Headless (lens 4's matrix, re-checked against `ChatSessionManager.cs:835`/`:598-612`: both `SetActiveRun`
    call sites are Planned-only, so there is no "live Simple run shows a Pause button that can never work"). Two
    true observations that are not defects: a resumed run is always headless (so `ChatSession`'s `AppendNudge`
    site at `:793` is dead code today), and shutdown deliberately does *not* revoke a pending pause
    (`HeadlessRunLauncher.cs:658-667`), which is asserted rather than merely commented.
14. **Trilingual parity and the Batch-14 pins — CLEAN.** All 20 new keys exist in EN/DE/FR with genuine
    translations matching the spec §14 table; no `Designer.cs` was hand-edited; `MinimumBoundPaths` was **raised**
    18 → 26 with zero assertions deleted and three anchors added; both new converters are registered in
    `App.xaml`. The only localization hole is F14, which is coverage, not a missing key.
15. **Logging privacy — CLEAN.** Titles appear only in `SensitiveDebug` (`AgentRunService.cs:809`); the paired
    `LogInformation` carries run id + counts. The nudge appears in no log statement anywhere. VM catch-arms log
    the run id only.
16. **Sibling steps stuck `Running` under a `Paused` parent — NOT REACHABLE.** Every call in the fan-out settle
    loop is failure-isolated with a bare `catch (Exception)` (which swallows OCE too), and the loop writes a
    status for every dispatched sibling. The invariant "a `Paused` run never carries a `Running` step" holds on
    every non-fault path — with one thin residual caveat both lens 2 and I reached independently: both restores go
    through the swallowing `SafeSetStepStatus`, and sweep statement 1b only resets `Running` steps for rows at
    `WaitingForChildren`, so a DB fault there would strand the step invisible to `NextPendingStepAsync`.
17. **A forged verify "fact" line from an edited title — NO FINDING.** `NormalizeStepText`
    (`AgentRunService.cs:1078`) is char-for-char `AgentVerifier.Flatten` (`:429`): both strip `\r`/`\n`/`\t` and
    both miss U+2028/U+2029/U+0085, so the edited title *inherits* the precedent rather than regressing it, and
    the only "attacker" is the user against their own prompt. The double-ellipsis and fact-eviction worries are
    both unfounded (the caps are per-item counts, not a shared budget).

**Direct conflict between lenses — execution wins.** Lens 4's summary states: *"I found no path that settles
`Cancelled` on a pause, no double-run on resume, and no lost in-flight step."* Its reasoning about
`TryPauseUserAsync` and the two in-process pause sites is correct as far as it goes, but the conclusion is
contradicted by three executed results — lens 6's `ProbeD`/real-launcher queued-child probe (F1), lens 5's
`LPROBE1` (F3), and my own prologue probe (F2). Each of those settles a paused run terminally. Lens 4 checked
the sites that *consume* a request and found them correct; it did not check the paths on which the request is
never consumed at all. That is exactly why the read-only "could not break it" result does not survive.

---

## What this review did NOT cover

* **No real provider, ever.** Every fact and probe used fakes or `FakeVerifier`. F9's severity in particular
  depends on how a *real* critic reacts to a `Failed` step it was never told about; F11's impact depends on the
  real tokenizer being enabled; F16's depends on what a real replanner does when it is not told a step was
  skipped. None of those can be settled without a live model.
* **No real multi-step run, and no real fan-out.** Child dispatch was simulated in every probe (mine included).
  The *timing* claims — especially F1's "children spend seconds in their planning call" and F2's "the prologue
  is hundreds of ms" — are reasoned from what the code does, not measured against a real provider and a real
  workspace provisioner.
* **No rendering.** DE/FR strings were compared as resource values only. Clipping, wrapping, the width of the
  five-verb button group at the panel's real size, the inline editor's two unlabelled boxes (F7), and whether
  "Pausing…" is legible in the button are all human-eye items.
* **No human NOT clicking.** Several findings are races whose real-world frequency only a human can judge:
  F3 (Continue-then-Pause within milliseconds), F10 (Stop-then-Pause during an unwind), F5/F6 (Pause exactly as
  the last child settles). A human should also confirm the *opposite* of the smoke list — that pausing and doing
  nothing for a minute leaves the run genuinely idle and the button honest.
* **No scheduled-job wall-clock observation.** D5's premise is re-pinned in code (and lens 6 verified it), but
  "a second job actually dispatches while the first sits paused" over real elapsed time is manual item 2 and
  stays manual.
* **Not re-run: the full suite.** I relied on three independent green measurements (orchestrator 2853/0, lens 5
  2866/0, lens 6 2860/0) and built only the test project. My four probes were deleted; `git status` is clean.
* **Not audited: everything outside the batch diff.** In particular the pre-existing budget-pause path is only
  examined where Batch 08 changed its behaviour (F3's note that the old Continue button is now exposed to the
  same window, F19's re-park reason).

---

## Tally

* **Filed:** 28 findings across six lenses.
* **Distinct:** 23 — **21 defects + 2 coverage gaps**. Largest merges: F15 (4 filings: lens 2, 3, 4, 5), F2
  (2 filings: lens 1, 2), F8 (2 filings: lens 3, 6).
* **CONFIRMED:** 23 of 23, but **one narrowed on adjudication** (F13: only 1 of the 6 mutation outcomes is
  affected, so it drops from should-fix to nit). **REFUTED:** 0 findings; **3 arguments corrected** (lens 5's F3
  window width, lenses 1+2's F2 window boundary, lens 2's unreachable-`WaitingForChildren`-source observation),
  and **1 lens-level negative overturned by execution** (lens 4's "I could not break the central risk"). A 23/23
  confirmation rate is itself worth a sceptical read: it reflects that the six lenses filed conservatively, not
  that nothing they wrote was wrong — see the corrections above and F13's and F15's re-derived fixes, both of
  which would have had a builder implement more than the defect warrants.
* **By severity:** 4 must-fix, 11 should-fix, 6 nits, 2 coverage gaps. (IDs are stable and roughly
  severity-ordered; F13 is the one row whose severity moved after the table was laid out.)
* **Executed by the adjudicator:** F2, F4, F5, F6 (four probes, real orchestrator / real SQLite / real VM,
  deleted after the run).

### Ordered must-fix list

1. **F1** — `AgentRunSteeringService.cs:130`: the cascade must use the pausable set, not the terminal set.
   Pausing a fan-out parent in the seconds after dispatch strands its children at `Planning` and settles the
   parent terminally `Failed`. Widest window, most likely real timing, executed against the real launcher.
2. **F2** — `AgentRunSteeringService.cs:87` / `AgentRunOrchestrator.cs:823`: a pause inside the fan-out dispatch
   prologue settles the run `Cancelled` with `CompletedAt` and no way back. Executed by the adjudicator; the
   batch's named central risk, realized verbatim.
3. **F3** — `AgentRunOrchestrator.cs:142` / `HeadlessRunLauncher.cs:553`: Continue-then-Pause inside the resume
   ramp-up settles the run `Cancelled`. Narrow window, unrecoverable when it lands; needs an owner decision on
   which dispatch owns a request.
4. **F4** — `RunProgressViewModel.cs:70-81`: add `[NotifyCanExecuteChangedFor(nameof(EditStepCommand))]`. One
   of the five shipped plan-mutation verbs is unusable on every step row that existed before the pause.
