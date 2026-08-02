# Batch 08 — Live steering · IMPLEMENTATION SPEC

Companion to [`08-live-steering.md`](08-live-steering.md). Written 2026-08-01 against the as-built code at
`1941e3c` on `feature/agent-run-spine`, from six grounding passes — five read-only over the real files and one
that **executed a build and a test run** (D5, Ground F). Where this file and the batch file disagree, **this
file wins, and §0 says why for every case.** Where a grounding report and the code disagree, the code wins;
that happened once and it changes a load-bearing line of the design (**W3**).

The seven decisions D1–D7 are **owner-resolved**. §1 restates each as a build instruction and never re-argues
it. Every sub-choice the decisions delegate is made here, by name, with its evidence.

`git diff aa5beb9..HEAD -- src/` is EMPTY (Batch 14 shipped tests + docs only), so every `src/` line reference
in the batch spec was re-checkable at HEAD. Four are wrong. They are spec errors, not drift.

Read §2 (hazards) before every group and §3 (the gate) at every commit.

---

## 0. Where the spec was wrong

Sixteen disagreements. Each is *claim → code at `1941e3c` → what this plan does*. **Do not quietly adapt any
of these; the corrected form is the instruction.**

| # | Claim | Code at `1941e3c` | What this plan does |
|---|---|---|---|
| **W1** | D1 bullet 1 (`:60`): "the orchestrator's own `cts.IsCancellationRequested` checks, **which settle the run `Cancelled`** (e.g. `:668`–`:672`)". | `AgentRunOrchestrator.cs:668`–`:671` is a comment and `:672` is a **check**. No `AgentRuns` row is written in that range — `:673` returns a `FanOutResult`. The three sites that actually write `Cancelled` are `:265` (fan-out), `:307` (in-process step) and `:385` (`catch (OperationCanceledException)`), all via `SafeFail` → `FailAsync`, which stamps `CompletedAt` **unconditionally** (`AgentRunService.cs:286`). | The pause branch is installed at **three** named sites, not at `:672`. `:672` is left alone and is *protected* instead (see W6/§1 D6): a cascade pause must never fire the parent's own token. A pause that lands in `FailAsync` does not merely write the wrong state — it stamps a completion time. |
| **W2** | D1 bullet 2 (`:62`): "A step left `Running` is invisible to `NextPendingStepAsync` and the resumed run silently drops it." | On the in-process path the aborted step is **not** left `Running` — it is persisted **`Failed(3)`**. `:296` `SafeRecordStep` is unconditional and runs **before** the `r.Cancelled` test at `:303`; `SafeRecordStep` maps `!Succeeded → Failed` (`:950`). `Failed` is invisible to `NextPendingStepAsync` (`AgentRunService.cs:665`) **and** dropped by `KeepDoneAsync` (`:407`). Neither `SetStepStatusAsync` nor `RecordStepResultAsync` tests `ct`, so the write lands on an already-cancelled token. There is a **third** post-abort status: OCE out of `StepPersonaResolver.ResolveAsync` (awaited *before* the exchange try/catch on **both** executors — `HeadlessTurnExecutor.cs:277-280`, `LiveTurnExecutor.cs:125-127`; it rethrows OCE at `StepPersonaResolver.cs:172`/`:244`) leaves the step **`Running(1)`**. | The restore is **not** "instead of leaving it Running" — it is **intercept before `:296` ever runs**. §1 D1 installs the pause branch *above* `SafeRecordStep`, so no `Failed` row and no `First/LastMessageId` are written for an aborted step (D2 discards its text). The `Running(1)` path is covered by the second site, in `catch (OCE)`, which needs a hoisted `inflightStepId` because `step` is scoped to the `while` at `:218`. |
| **W3** | D1 bullet 3 + Ground A §5: candidate **(b)** "routes through `ChatSession.Cancel()` and therefore **releases action cards**", implying a live step blocked at `WaitingForTool` comes back cancelled. | **The release is a `Decline` that CONTINUES the exchange, not a cancellation.** `ActionCardInfo.cs:265` `Cancel()` → `_tcs.TrySetCanceled()`; `ChatSession.cs:1020`–`:1025` catches `TaskCanceledException` and maps it to **`ToolDecision.Decline`**; `:1050`–`:1055` then returns the *string* `"User declined the {tool} operation…"` as a **tool result** and the tool loop keeps going. Whether `r.Cancelled` ends up true depends on the *next* provider round observing the cancelled token inside `RunModelExchangeAsync` — an inference, not a guarantee. If it does not, the step returns `Succeeded:false, Cancelled:false` → `:311` → `TryReplanAfterFailureAsync` → **the user clicks Pause and the run replans**, which is exactly D6's named failure mode relocated to the in-process live path. | **The pause branch consumes the request BEFORE testing `r.Cancelled`, and does not depend on it at all.** Shape is `if (_steering?.TryConsumePauseRequest(run.Id) == true) { …pause…; return; }` placed between `:295` and `:296`. Same ordering in the `catch (OCE)` arm. This is a correction to a grounding report as well as to the spec, and it is the single line whose ordering the batch most depends on. |
| **W4** | D6 (`:142`–`:143`): a `Paused` child "falls through to `default:` **at `:661`**". | `default:` is at **`:660`**. `:661` is `anyFailed = true;`, `:662` is `error ??= "child run did not settle"`, `:663` is the `SettleSiblingAsync(…, FanOutStepResult(false, error))`. | Cosmetic, but §1 D6's edit list quotes real line numbers. Also note `switch (child?.State)` at `:628` is over a **nullable** — `null` (a `SafeGetRunAsync` fault) must stay in `default:`, so the widening is `case AgentRunState.WaitingForInput or AgentRunState.Paused:`, never a null-tolerant pattern. |
| **W5** | D6 (`:152`): "**this arm** is a required edit site" — naming only `:647`. | There is a **second** exact-match `WaitingForInput` site with the same failure shape: `SafeCancelStaleChildrenAsync` `:816`, `if (old.State is AgentRunState.WaitingForInput) await _runService.FailAsync(old.Id, "superseded by a re-dispatched fan-out", cancelled: true, …)`. A cascade-paused child at `Paused(4)` passes the terminal guard at `:808`, gets a **no-op** `CancelAsync` (it is not in `_inflight` after a restart), and is **not** force-settled — it leaks forever with its own stub chat, i.e. precisely the leak `:813`–`:815` exists to prevent, surviving exactly the restart path that comment names. | **Two** explicit-set widenings, not one: `:647` and `:816`. G5 does both in one commit and pins the second with a restart-shaped fact. |
| **W6** | D6 (`:135`): "pause every child, then move the parent `WaitingForChildren → Paused`" — implying the `:647` widening delivers the parent transition. | It does not. The parent's park is written by a **different arm** — `:277` `SafePause(run.Id, …, reason: ChildrenParkedReason)` → `PauseAsync` → **`WaitingForInput(3)`**. And by the time that arm runs the parent's row is already **`Running`**, because the un-park CAS `SafeTryEndChildWait` at `:680` executes *inside* `TryFanOutAsync`, before the caller sees `AnyParked`. | The parent's pause CAS accepts an **explicit source set** `{Running, Verifying, WaitingForChildren}` — `Running` is what the fan-out route actually presents, `WaitingForChildren` covers a pause that lands before `:680`, `Verifying` covers a pause during the critic. G5 adds a user-pause arm at `:269` that takes `TryPauseUserAsync` instead of `SafePause`. |
| **W7** | D4 (`:110`): "`SafeSeedResumeContext` (`:442`) is where a persisted one would be restored." | **Unreachable.** Order on the only resume path: `HeadlessRunLauncher.cs:432` reads the run (ExtraJson intact) → `:437` `TryBeginResumeAsync` → `AgentRunService.cs:339` `UPDATE … ExtraJson=NULL …` → `:558` hands the **stale** object to `RunAsync` → orchestrator `:92` reads the stale copy → `:446` `SafeSeedResumeContext` does a **fresh `GetAsync`** whose `ExtraJson` is now NULL. A nudge on `AgentRuns.ExtraJson` is destroyed by the resume claim before the stated restore point runs. `AgentSteps.ExtraJson` is no better: any replan's `KeepDoneAsync` (`:401`) drops Pending steps and the planner re-serializes `{parallelGroup}` fresh. | §1 D4 picks **scope-to-dispatch**, and states that the persist branch was not merely un-chosen — the spec's own restore seam does not work, and persisting would need either a new column or a widened SET clause on the resume CAS. |
| **W8** | D3 (`:90`): "confirm it inherits `AgentVerifier`'s existing flatten-and-cap of the step title (`:349`)". | Inherited **only** in the artifact-facts block. `AgentVerifier.cs:144` flattens but does **not** cap. `AgentPlanner.BuildReplanMessages:525` — `sb.AppendLine($"- [{…}] {c.Title}: {c.Intent}")` — applies **neither** `Flatten` nor `Truncate`, and D4 explicitly routes edits through the replan. `Intent` is capped **nowhere** in the codebase. `HeadlessTurnExecutor.BuildInstruction:546`, `ChatSession.cs:790`–`:792` and `AgentRunOrchestrator.BuildChildGoal:717`–`:721` sanitize neither field. | **Normalize at WRITE time, in the mutation validator** (§1 D3): flatten CR/LF/TAB → space, trim, cap Title 200 / Intent 400 / ExpectedArtifact 200 before the row is persisted. That bounds every downstream prompt at once instead of patching five interpolation sites, and it is a superset of what D3 asked for. The residual System-prompt role question is escalated, not fixed — §19 Q3. |
| **W9** | D5 (`:117`–`:118`): `HeadlessRunHandle.Completion` settles on a park, "orchestrator `:647`–`:651` states it". | Those lines are **exact** and they do assert it, but they are a *comment on the fan-out child arm*. The mechanism is `HeadlessRunLauncher.cs:359` + `:424` + `:426` (`Completion` **is** the dispatch `Task.Run`, which self-catches OCE at `:390` and `Exception` at `:400` and always runs its `finally` at `:411`) combined with the park `return` at orchestrator `:234`. **Measured** by Ground F: a genuinely parked run settles `Completion`, and so does a dispatch that throws. | G1 lifts Ground F's measured file first, before anything is built on the premise, as the spec demands. §1 D5's build instruction points at the real mechanism so a builder does not edit around `:647` and think they touched it. |
| **W10** | Key seams (`:34`–`:36`): `TryBeginResumeAsync` "nulls `ExtraJson` unconditionally, **which would erase a user pause's own reason**". | Half wrong. `ExtraJson=NULL` is unconditional *within the SET clause*, but the statement is `… WHERE Id=@Id AND State=@Expected` with `@Expected = WaitingForInput` (`AgentRunService.cs:339`/`:343`). A run at `Paused(4)` matches **0 rows**, so today this method cannot touch a user pause at all. | The third claim method is a **sibling** with `@Expected = Paused` that *also* nulls `ExtraJson` — **deliberately**, because retiring the pause marker on the claim is correct (it is the same reason `:336`–`:337` gives). The sibling pair also keeps each CAS single-source, which is what makes "pin the source state of all three" a real test. |
| **W11** | Key seams (`:36`–`:37`): "there will be **three** of them once D6's child-wait resume is added." | Accurate as a count of *claims*, but the naming matters. This batch's third claim is `TryResumeFromPauseAsync` (`Paused → Running`). The **pause** is a fourth CAS and is not a claim: `TryPauseUserAsync` (`{Running, Verifying, WaitingForChildren} → Paused`). D6's predicted "third claim method" is delivered; D6's implied "re-dispatch the paused children" is **not** a claim on the children at all (§1 D6: they are superseded, not resumed). | §5 tabulates all four CASes with their source sets, and G2 pins each source state from both sides. |
| **W12** | Not in the spec at all, and it defeats D3's **edit** verb outright. | `StepRowViewModel.Title` is `{ get; init; }` (`RunProgressViewModel.cs:1015`) and `SyncSteps`'s else-branch updates only `Status` and persona attribution (`:747`–`:748`, rationale at `:756`–`:758`: "rows are replaced only when step IDS change (R23)"). `ReplaceStepsAsync` raises **no** `RunChanged` (contrast `:697`/`:747`), and `RunProgressViewModel` refreshes only from `RunChanged` (`:275`). **Reorder is worse:** `StepRowViewModel` has no `Ordinal` at all and `SyncSteps` only ever *inserts* new rows at the plan index — it never MOVES an existing row. So an edit that preserves the step Id has no UI effect, and a reorder that preserves Ids leaves the collection in its old order forever. | G8 makes `Title` an `[ObservableProperty]`, assigns it in the else-branch, and adds an index-reconciling `Steps.Move` pass. G6's new mutation member raises `RunChanged` itself (that is *why* it lives on `IAgentRunService` — see §1 D3). `ReplaceStepsAsync` is left event-less so the replan path is untouched. |
| **W13** | Not in the spec, and it erases the user's decision. | `KeepDoneAsync` filters `s.Status == AgentStepStatus.Done` (`:407`), so the **next replan deletes a user-`Skipped` step from the plan** and `SyncSteps:726`–`:729` removes its row. Nothing in `src/` writes `Skipped(4)` today, so nothing has ever exercised it. | G6 widens `KeepDoneAsync` to the explicit set `Done or Skipped` and updates its doc comment. `SafeSeedResumeContext:448` stays at `== Done` — a skipped step did not run and must not enter `ctx.CompletedSteps`. The two filters differ **deliberately**; say so in both comments so a later reader does not "align" them. |
| **W14** | Not in the spec: it reads as though the panel VM is on Batch 12's dispatcher abstraction. | `RunProgressViewModel` does **not** use `IUiDispatcher`. It captures a raw `SynchronizationContext` in its ctor (`:273`–`:274`) and posts to it at four sites (`:325`, `:369`, `:634`, `:866`). The four VMs Batch 12 converted do not include it. | Every steering command marshals through the existing `_uiContext.Post` + `TaskCompletionSource` shape, template `ApplyWorkspaceOutcomeAsync` (`:366`–`:402`). **Do not introduce `IUiDispatcher` here** — that is a Batch 12 refactor and it would change a ctor that is constructed **positionally** in production (`AssistantViewModel.cs:403`–`:406`) and in at least three test files. |
| **W15** | D7 reads as though a range remains to be removed. | Exactly **one** ordinal range over `AgentRunState` survives in `src/`: `AgentRunService.cs:445` `WHERE State < @Terminal` with `@Terminal = WaitingForInput` (`:448`). It is the startup sweep, it is **sanctioned**, and `AgentEnums.cs:44`–`:45` plus `AgentRunService.cs:438` both name it as the *reason* `WaitingForChildren` was appended at 8. Both former C# ranges are confirmed converted to explicit sets (`AgentRunService.cs:888`, `HeadlessRunLauncher.cs:669`–`:670`). | **Do not touch `:445`.** D7 is buildable as stated: no *new* range. Every predicate this batch adds is an explicit set. §19 Q5 records the forward hazard (a 10th member at ordinal 9 escapes `State < 3` silently). |
| **W16** | `Controls/Assistant/RunProgressPanel.xaml.cs:7` still reads "…gated by the host … **No commands.**" | False since the Continue (`:33`) and Publish (`:40`) buttons landed. | G8 corrects it in the same commit that adds the third and fourth commands. A stale doc on the file a batch is editing is not someone else's problem. |

---

## 1. Decisions D1–D7 as BUILD INSTRUCTIONS

Owner-resolved. Not re-argued. Each states the seams it touches and, where the decision delegates a
sub-choice, the choice made here with its evidence.

**No decision is unbuildable.** All seven were checked against the measured code before this section was
written and every one has a path. Four needed the *route* corrected rather than the decision: D1's stated
hazard was wrong about the step's post-abort status (W2) and its option (b) needed a mechanism neither option
names (W3); D4's stated restore seam does not exist (W7); D6's stated edit site is one of two and does not by
itself deliver the parent transition (W5/W6); D5's stated mechanism is in a different file from the one it
cites (W9). None of those changes what the owner decided. **Two delegated sub-choices were mine and are
recorded as such:** D6 (a)-versus-(b) → **(a)**, and D4 persist-versus-scope-to-dispatch →
**scope-to-dispatch**. A third, smaller one is recorded inside D4: **all steering is pause-gated**, so there
is no nudge-a-running-run control.

### D1 — a pause CANCELS the in-flight step immediately

**BUILD:**

1. **Discriminator = sub-choice (b), the flag read before the `Cancelled` settle.** Implemented as a
   process-level per-dispatch registry, `IRunSteeringStore` / `RunSteeringStore` (§5), modelled on the shipped
   `IExecutingRunStore` / `ExecutingRunStore` — the same shape (a `Services`-layer singleton written by
   `HeadlessRunLauncher` *and* by a ViewModel-layer `ChatSessionManager`, lock-free
   `ConcurrentDictionary`, nothing throws).
2. **The registry carries a cancel SINK as well as the intent.** That sink is the part of the design that is
   in neither of D1's two named options, and it is what earns it: on Live the sink is `session.Cancel()`,
   which is the only thing that touches a step blocked at `WaitingForTool` (`ChatSession.cs:1015`–`:1018`
   awaits `ActionCardInfo.WaitForUserDecisionAsync()`, which takes **no** `CancellationToken` —
   `ActionCardInfo.cs:226`). Candidate (a), a second linked CTS, cannot reach it at all, and for an
   interactive Planned run the action card is the **normal** path, not an edge
   (`ChatSessionManager.cs:785`–`:789`: "every `write_file` goes through an action card the user clicks").
3. **Consume the request BEFORE testing `r.Cancelled` (W3).** Not after, not `&&`-ed with it. The pause branch
   is unconditional on the step's own result. Evidence in W3; this is the highest-value line in the batch.
4. **The aborted step goes back to `Pending` and is never recorded.** Two orchestrator sites:
   - between `:295` and `:296` — the pause branch returns before `SafeRecordStep`, so no `Failed` row, no
     `First/LastMessageId`, and **no `ctx.RecordStep`** (which would burn a step against
     `ctx.StepsExecuted` and feed the critic a step that did not finish);
   - inside `catch (OperationCanceledException)` at `:378` — needs a `Guid? inflightStepId` hoisted to
     `RunAsync` scope beside `cancelled`/`failed`/`runFirst`/`runLast` (`:93`–`:96`), assigned at `:293` and
     cleared after the step settles. `step` is scoped to the `while` at `:218` and is **not** in scope in the
     catch.
   Reuse the call shape the parked-child arm already has: `SafeSetStepStatus(id, AgentStepStatus.Pending, …)`
   (`:656`) — the only `Pending` restore in `src/` outside the startup sweep.
5. **Pause and Stop cannot collide, and the collision is closed four ways** (Ground A named the race for (b);
   these are the four hardenings):
   - **registration-scoped**: `RecordPauseRequest` returns `false` when no dispatch of that run is registered
     in *this* process, so the intent cannot exist for a run nothing here is running;
   - **cleared on entry**: `RunAsync` revokes any request for its own `run.Id` before the `try` at `:116`, so
     no request can survive a dispatch boundary (`_inflight`'s per-runId overwrite at
     `HeadlessRunLauncher.cs:602` is the same hazard on the headless side, and this closes it);
   - **revoked on terminal intent**: the five sites in §5.3 revoke before they cancel, so chat delete, a
     superseded fan-out generation, the parent's terminal cascade, Stop and Clear-conversation can never be
     read as a pause;
   - **CAS at the settle**: `TryPauseUserAsync` is a CAS over an explicit source set, so a lost race writes
     **nothing** rather than resurrecting a run somebody else settled (R11).
6. **Ordering invariant — numbered because a tidy-up reorder breaks a scheduled job.** In the pause branch, in
   this order: (i) `SafeSetStepStatus(Pending)`; (ii) `SafeAddUsage(run.Id, r.Usage, …)` (D2); (iii)
   `PinRange()`; (iv) `TryPauseUserAsync` — the row becomes `Paused`; (v) `SafeOnPaused`; (vi) `return`. The
   row must read `Paused` **before the dispatch task returns**, because
   `ScheduledJobBackgroundService.cs:244` reads the row *after* `await handle.Completion` (`:230`) and its
   park branch is an `else if` (`:251`): a row that is not yet `Paused`/`WaitingForInput` at that instant
   lands on `:271` → `MarkRunFailedAsync` + a failure toast + a strike against
   `ScheduledJobService.MaxConsecutiveFailures = 5`, and a `RecurrenceType.Once` job is **retired on the
   first strike** (`ScheduledJobService.cs:340`–`:354`). Measured by Ground F, pinned by G5's scheduled fact.
7. **Consequence of (6), stated so nobody widens it needlessly:** because the step is `Pending` before the row
   is `Paused`, a `Paused` run can never carry a `Running` step. So `FailInterruptedRunsAsync` statement 1b
   (`AgentRunService.cs:465`–`:476`, `Status=@Running AND RunId IN (… State=@Waiting)`) needs **no widening**.
   Ground B raised it as a possible edit site; it is not one, for this reason.
8. **Pause is refused while `Planning`.** The CAS source set excludes it, and so does the steering service's
   pre-check. Reason: a resume runs `RunAsync(resume: true)`, which skips planning entirely (`:132`), so a run
   paused mid-plan would resume with **no plan**, drain zero steps and settle `Completed` having done nothing.
   `RunProgressState.Planning` therefore renders no Pause button; `Verifying` folds to `Running` in the
   projection (`:528`) and **is** pausable.

**Seams:** `AgentRunOrchestrator.cs:91`–`:96`, `:116`, `:218`, `:293`–`:317`, `:378`–`:387`;
`HeadlessRunLauncher.cs:335`, `:421`–`:423`, `:437`, `:514`, `:597`–`:602`, `:807`–`:811`;
`ChatSessionManager.cs:871`–`:873`; `AssistantViewModel.cs:738`–`:741`, `:750`–`:759`;
`AgentRunService.cs` (two new CASes); new `RunSteeringStore.cs`.

### D2 — the aborted step's text is DISCARDED; its tokens are still BILLED

**BUILD:**

- The pause branch calls `SafeAddUsage(run.Id, r.Usage, cts.Token)` — `stepId: null`, i.e. the run-level
  ledger the API already supports (`IAgentRunService.cs:70`–`:71`). `SafeAddUsage` already null-guards
  (`:467`–`:472`), so "bill nothing and synthesize nothing" is free: **no estimate, no synthesis, no fallback
  number, ever.**
- **Today this call is provably a no-op on both executors, and that is a fact to pin, not to fix.**
  `HeadlessTurnExecutor.cs:372` returns `Usage: null` on the cancel arm; `ChatSession.cs:684` + `:752` assign
  `usage` only at `:692`, after the throw point. Ground A found this matches D2 by accident rather than by
  design. Write the call anyway — the day an executor returns partial usage on an abort, D2 is already
  honoured — and pin the null case so nobody "helpfully" synthesizes one later.
- Because the branch never reaches `SafeRecordStep`, the aborted step gets **no** per-step ledger entry and
  **no** `First/LastMessageId`. That is D2's "text is discarded" made literal: the step re-runs clean.
- `ctx.RecordStep` is **not** called (see D1 build item 4).

**Seams:** `AgentRunOrchestrator.cs:296`–`:301`, `:467`–`:472`.

### D3 — full mutation scope: edit, insert, reorder, skip

**BUILD:**

1. **One new member on `IAgentRunService`**, not a separate service:
   `Task<PlanMutationResult> ApplyPlanMutationAsync(Guid runId, IReadOnlyList<PlanStepEdit> pendingSteps, CancellationToken ct = default)`.
   It lives there because **`RunChanged` is the deciding constraint** — the panel refreshes only from that
   event (`RunProgressViewModel.cs:275`), the event is on this interface, and `ReplaceStepsAsync` raises none
   (W12). A separate service could validate but could not make the panel repaint.
2. **The gate is the state, and it removes D3's race by construction.** The method re-reads the run and
   refuses unless `State == AgentRunState.Paused` — one explicit state, no set, no range. This is D3's own
   "the UI can require a pause first… state it as the design rather than discovering it as a bug." The two
   races Ground C traced (a mutation between `:218` and `:293` executing a step no longer in the plan; a
   `ReplaceStepsAsync` during `:295` deleting the in-flight row so `SafeRecordStep` silently updates 0 rows
   with no ledger, no event, no log) are both unreachable when the only writer is a paused run.
3. **Ordinals are assigned by the service, never supplied by the caller.** That makes four of Ground C's eight
   must-rejects **structural** instead of validated: duplicate ordinal, negative ordinal, non-contiguous
   ordinal, and reorder across the Done boundary are all impossible, because the immutable prefix is
   re-ordinaled `0..k-1` and the submitted tail `k..n-1`. Say this in the doc comment; it is why the validator
   is short.
4. **Immutable prefix = every step whose `Status` is not `Pending`** — `Done`, `Skipped` **and `Failed`**.
   `Failed` is in the set because a `Paused` run genuinely can carry one: a step fails, `:296` records
   `Failed`, and the user pauses during `TryReplanAfterFailureAsync`'s provider call, so `KeepDoneAsync` never
   ran. Preserved verbatim with original Ids (which is what keeps their per-step ledger entries, keyed by
   `StepId` string at `AgentRunService.cs:734`, and their timeline rows attached).
5. **Validator rejections, the whole list:** `NotPaused` (run missing, or state ≠ `Paused`); `UnknownStep` (an
   entry names a step that is not a `Pending` step of this run, or the same `StepId` appears twice);
   `TitleRequired` (title blank after normalization); `EmptyPlan` (zero rows in total — unreachable while no
   verb deletes a row, kept because Ground C traced empty-plan → `NextPendingStepAsync` null → `Verifying` →
   `SafeVerify` degrades to accept → **silent `Completed`**); `TooLong` (total rows >
   `RunProfile.MaxStepsCap` = 48 — the only run-independent bound, because the per-run `MaxSteps` lives in the
   ephemeral `RunProfile` and a resume is granted a fresh budget anyway); `WriteFailed`.
6. **Skip writes `AgentStepStatus.Skipped(4)`, which nothing in `src/` has ever written.** No enum work
   (`AgentEnums.cs:70`), no DDL work (`SqliteContext.cs:327` is a bare `INTEGER NOT NULL`, no CHECK), and the
   panel already renders it: `StepStatusToGlyphConverter` has an explicit `Skipped` arm
   (`RunProgressConverters.cs:21`, `DismissCircle20`) and the brush falls to `TextMutedBrush` with a comment
   that names it (`:39`). **Do not add a brush arm.**
7. **`KeepDoneAsync` preserves `Skipped` (W13).** `s.Status is AgentStepStatus.Done or AgentStepStatus.Skipped`.
   `SafeSeedResumeContext:448` stays `== Done`.
8. **Two things a skip does NOT break, verified end to end — state them so nobody "fixes" them.** (i) A
   `Skipped` step's `ExpectedArtifact` is never probed and never reaches the verify prompt: `ctx.CompletedSteps`
   has exactly two writers (`RunContext.RecordStep`, called only from orchestrator `:297`/`:708`, and
   `SeedCompletedSteps`, whose filter is `== Done`), so a step that never executed cannot enter it. (ii) There
   is no progress/percentage/"N of M" computation anywhere in the panel, so a skip has no denominator to
   corrupt.
9. **Normalization at write time (W8):** flatten `\r`/`\n`/`\t` → space, `Trim()`, then cap — Title 200
   (matching `AgentVerifier.MaxDeclarationChars`, `:180`), Intent 400, `ExpectedArtifact` 200, each with a
   trailing `…` when cut. This bounds `AgentVerifier.cs:144`, `AgentPlanner.cs:525`,
   `HeadlessTurnExecutor.cs:546` and `ChatSession.cs:790` at once.
10. **Privacy:** title/intent are user content. `AgentStep.cs:16`/`:19` already annotate them
    ("log only via `SensitiveDebug`"). The mutation logs a run id + a **count** at `Information`, and the text
    only via `SensitiveDebug` — the paired shape at `AgentRunService.cs:156`–`:159` and `:321`–`:322`.

**Seams:** `IAgentRunService.cs:166`–`:167`; `AgentRunService.cs:601`–`:653`, `:657`–`:670`;
`AgentRunOrchestrator.cs:401`–`:412`; `RunProgressViewModel.cs:723`–`:751`, `:1010`–`:1062`;
`Controls/Assistant/RunProgressPanel.xaml:74`–`:108`.

### D4 — a nudge is a TRANSIENT context injection — **SCOPED TO THE DISPATCH**

**DELEGATED SUB-CHOICE, MADE: scope it to the current dispatch. Do not persist it.**

Evidence: (i) the spec's own restore seam is unreachable — `TryBeginResumeAsync` nulls `ExtraJson` before
`SafeSeedResumeContext` reads the row (W7); (ii) `AgentSteps.ExtraJson` on a Pending step is destroyed by any
replan, and a nudge is exactly the thing likely to precede one; (iii) persisting would need either a new
column (schema work in an already-L batch) or a widened SET clause on the resume CAS, which is the erasure
hazard the Key-seams bullet warns about; (iv) `RunContext.Scratchpad` (`RunContext.cs:72`) is dead — no
writer, no reader, no test — and `00-OVERVIEW.md:820` has already earmarked it for `emit_step_result`, so it
is not the carrier.

**BUILD:**

1. **All steering is pause-gated, nudge included.** There is **no** nudge-a-running-run control. The flow is
   pause → type → Continue. This is the second delegated call in this decision and it is the one that keeps
   the batch small: it means nothing ever mutates a live loop (the standing guardrail), the nudge needs no
   process-level queue, and a nudge has exactly the same precondition an edit already has under D3. The cost
   is D1's accepted cost: the paused step's text is discarded and its tokens are billed
   ("A user who pauses repeatedly pays repeatedly. That is the accepted cost of D1").
2. **The nudge rides the resume call.** `IAgentRunResumeService.ResumeAsync(Guid runId, string? nudge = null, CancellationToken ct = default)`
   — inserted **after `runId`, before `ct`**. A pre-existing positional call that passed a `CancellationToken`
   second would become a **compile error**, not a silent bind, because `CancellationToken` is not `string?`;
   that is the safe direction. Both current callers pass one argument
   (`RunProgressViewModel.cs:581`, `FlowItemViewModel.cs:188`) — the Flow card passes `null`.
3. `HeadlessRunLauncher.ResumeAsync` threads it to `orchestrator.RunAsync(..., resume: true, nudge: nudge)`
   (trailing optional on `RunAsync` too, after `resume`), which calls `ctx.SetNudge(nudge)` immediately after
   `:92` and before `:118`.
4. **`RunContext` gains one member and one method**, and the method is the only way the text reaches a
   provider:
   ```csharp
   public string? Nudge { get; private set; }
   public void SetNudge(string? text);                 // flatten-trims, caps at MaxNudgeChars, null/blank ⇒ null
   public string AppendNudge(string userText);          // userText when Nudge is null; fenced append otherwise
   ```
   `MaxNudgeChars = 1000`, **head kept** (`text[..cap] + "…"`), the shape `AgentPlanner.cs:243`–`:249` uses
   for `MaxAnalysisChars` and pinned from both ends by `AgentPlannerTests.cs:581`–`:601`.
5. **Four one-line call sites, all `ChatRole.User`, and that is the whole privacy argument:**
   | Site | Edit |
   |---|---|
   | `ChatSession.cs:795` (Live step instruction) | `new ChatMessage(ChatRole.User, ctx.AppendNudge(instruction))` |
   | `HeadlessTurnExecutor.cs:283` (Headless step instruction) | `ctx.AppendNudge(BuildInstruction(...))` — composed in `ExecuteStepAsync`, which has `ctx`; `RunExchangeStepAsync` keeps taking a plain `string` |
   | `AgentVerifier.cs:164` (critic) | `new(ChatRole.User, ctx.AppendNudge(ctx.Goal))` |
   | `AgentPlanner.cs:540` (replan) | `new(ChatRole.User, ctx.AppendNudge(ctx.Goal))` |
   D4 says the critic and the replan seeing it should be **intentional**; sites 3 and 4 are that intent, made
   explicit and one line each.
6. **Two places it must NOT go.** `HeadlessTurnExecutor.BeginRunAsync:252`–`:261` seeds `ctx.Goal` as the
   first **persisted** user message — appending there would write user steering into the durable transcript as
   if it were the goal. `RunSingleTurnFallbackAsync:294` passes `ctx.Goal` for the R10 degrade turn, which is
   unreachable on a resumed run (`resume: true` skips planning). Neither is edited.
7. **NEVER the System prompt.** `TokenizingAiClientService.TokenizeMessages:267` short-circuits on
   `msg.Role != ChatRole.User`, so a System-prompt nudge ships the user's raw keystrokes to the provider
   byte-for-byte while their tokenization setting is ON — silently, with no exception and a reply that still
   detokenizes fine. The fence shape is Batch 05's `AgentPlanner.cs:483`–`:511`: goal leads, one User message
   (request shape stays `[System, User]`), explicit begin/end delimiters, capped, never System.
8. **Privacy + Flow.** Nudge text → `SensitiveDebug` only, never an `Information` line. The Flow surface's
   Title/Body stay generic and keyed (`AgentRunNotificationSurface.cs:149`–`:155`); a nudge is the opposite of
   an app-owned token (`RunPauseEnvelope.cs:16`–`:20`) so it **may key nothing and appear in no Flow field**.
9. **The UI says it is scoped** — `Run_Nudge_Scope_Note`, §14, rendered under the box whenever the box is
   visible. D4 requires this in so many words.

**Considered and rejected:** a process-level nudge queue that would let a *running* run be nudged. It buys a
control nobody needs once pause is instant, introduces a third lifetime ("survives a resume but not a
restart") that no reader can reason about, and puts a mutation on a live loop.

### D5 — EVERY run is pausable, including scheduled runs

**BUILD:**

1. **Pin the premise first (G1), before anything is built on it.** Ground F measured all three halves and
   wrote the file; lift it. The existing coverage was **vacuous** on two of them: all four scheduler park
   tests hand the service `Task.CompletedTask` as `HeadlessRunHandle.Completion`
   (`ScheduledJobBackgroundServiceTests.cs:379`, `:424`, `:471`, `:503`), so nothing in `tests/` had ever
   awaited a `Completion` produced by a genuinely parked run.
2. **The scheduler needs no edit.** `ScheduledJobBackgroundService.cs:251` is already
   `else if (run?.State is AgentRunState.WaitingForInput or AgentRunState.Paused)`, already an explicit set,
   and already calls `AdvanceMissedRunAsync` (`:265`). Verified verbatim. Do not touch this file.
3. **The mechanism is not `:647` (W9).** `Completion` **is** the dispatch task; it self-catches everything and
   always runs its `finally`, so it settles on any exit of `RunAsync` — return, cancel or throw.
4. **D1 build item 6's ordering is what makes (2) hold for a *user* pause.** Row `Paused` before the dispatch
   task returns, or the job is booked as a failure. G5 pins it.
5. **D5's new consequence is real and this batch does not fix it.** `AdvanceMissedRunAsync` moves `NextFireAt`
   forward, so a paused scheduled run and a fresh run of the same job coexist — measured by Ground F, with
   distinct `RunId` + distinct `ChatId` + the same `TriggerRef`, both `WaitingForInput`, and no guard
   anywhere: `AgentRuns.TriggerRef` is written (`HeadlessRunLauncher.cs:276`) and indexed
   (`SqliteContext.cs:315`) and **read by no query**. Already true at a budget pause; a user pause makes it
   reachable on demand. §19 Q4.

### D6 — pausing a `WaitingForChildren` parent CASCADE-PAUSES its children

**DELEGATED SUB-CHOICE, MADE: (a) — widen the fan-out parked arm to an explicit `WaitingForInput or Paused`
set.** D6 names (a) as the recommendation; Ground B's decisive facts confirm it. Under (b) the pause-versus-
budget distinction is lost at the *state* level and recoverable only from the reason token — and
`ScheduledJobBackgroundService.cs:251`, `ChatSessionManager.cs:590`–`:592` and `:612`–`:613` all key on
**state**, not reason. (b) is also not free: it still needs new reason tokens plus arms in **both**
`PausedBodyKey` (`AgentRunNotificationSurface.cs:88`–`:93`) and `DescribePause`
(`RunProgressViewModel.cs:544`–`:549`) × 3 resx, because both default to the budget string — otherwise a
user-paused run tells the user it "Stopped at budget".

**BUILD — the cascade is D1's machinery plus two explicit-set widenings. There is no new pool, no new wait
primitive and no `_slots` risk.**

1. **`AgentRunSteeringService.PauseAsync(runId)`** reads the run. If `State == WaitingForChildren`: record the
   parent's pause request, then for every child from `GetChildRunsAsync(runId)` whose state is not
   `Completed`/`Failed`/`Cancelled`, record a request and **fire that child's cancel**.
2. **Deliberately do NOT fire the parent's own cancel.** `AgentRunOrchestrator.cs:672` checks
   `cts.IsCancellationRequested` *before* the un-park CAS and returns `Cancelled: true`, which the caller
   turns into `SafeFail(cancelled: true)` at `:265` — the exact guardrail failure the batch calls "the single
   easiest thing in the batch to get wrong". The parent needs **no** signal: `Task.WhenAll` at `:612`
   completes naturally once every child dispatch task returns, and it deliberately has no
   `.WaitAsync(cts.Token)` (`:602`–`:605`, D16). The two-operation store API (`RecordPauseRequest` /
   `FireCancel`, never one call that does both) exists so this rule is visible in the API rather than only in
   a comment.
3. **Each child pauses through its own D1 abort** — its dispatch is registered, its request is consumed by its
   own `RunAsync`, its in-flight step goes `Pending`, its row CASes to `Paused`, `SafeOnPaused` is the headless
   no-op (`:540`–`:544`), the dispatch returns, `handle.Completion` settles.
4. **Widening 1 — `:647`:** `case AgentRunState.WaitingForInput or AgentRunState.Paused:`. Keeps `null` in
   `default:` (W4). Without it every cascade-paused child is recorded a **failed sibling** — `SafeRecordStep`
   *and* `ctx.RecordStep` (`:705`–`:709`), so the critic and any replan see failed work, and the error string
   `"child run did not settle"` becomes the sibling step's recorded outcome via `FanOutStepResult`
   (`:691`–`:693`). Note the second half of that defect: `default:` is the only arm that fails **and** rolls
   up nothing, so today a `Paused` child would be charged as a failure *and* have its tokens dropped.
5. **Widening 2 — `:816` (W5):** `if (old.State is AgentRunState.WaitingForInput or AgentRunState.Paused)`.
   Without it a cascade-paused child from a previous process leaks forever with its own stub chat.
6. **The parent's own transition — a new arm at `:269`.** When `children.AnyParked` **and** this run's pause
   request is still pending, take `TryPauseUserAsync` instead of `SafePause(ChildrenParkedReason)`; otherwise
   keep the existing budget-shaped park byte for byte. By this point the row is already `Running` (the `:680`
   CAS ran inside `TryFanOutAsync`), which is why the CAS source set includes `Running` (W6). Ledger clocks
   line up: `TryEndChildWaitAsync` opened a segment, `TryPauseUserAsync` closes it, mirroring
   `PauseAsync:308`.
7. **The third claim method** is `TryResumeFromPauseAsync` (`Paused → Running`), and
   `HeadlessRunLauncher.ResumeAsync` dispatches to it by the row's state (§5.2). The children are **not**
   resumed: the resumed parent re-enters `TryFanOutAsync`, `SafeCancelStaleChildrenAsync` supersedes the
   paused generation (widening 2 is what makes that reach them), and a **fresh** generation is dispatched via
   `LaunchChildAsync` on `_childSlots`. This is the shipped D13 park→resume shape exactly.
8. **Why there is no deadlock, spelled out because Ground B's arithmetic makes the alternative fatal.** A
   resumed parent queues on `_slots` (`HeadlessRunLauncher.cs:541`) and its fresh children queue on
   `_childSlots` (`:172`), so nothing nested waits on the pool it holds. If instead the *children's rows* were
   resumed via `IAgentRunResumeService` and awaited from inside the parent's `RunAsync`, they would take
   `_slots` — and with **two** concurrent headless parents that is 0 free permits and a permanent deadlock
   that `StopAsync`'s bounded 5 s wait cannot break, leaving both runs dangling `Running` until the next
   sweep. **Never resume a child from inside a parent.**
9. **A cascade-paused child's work is DISCARDED, not resumed.** Consistent with D2's discard-the-aborted-step,
   but larger than D2 says (the whole child run, not its last step). Its tokens never reach the parent's
   ledger — §19 Q1, inherited from D13, not introduced here.
10. **A child must never get its own Flow card.** `AgentRunNotificationSurface.cs:128`–`:134` already refuses
    (`if (run.ParentRunId is not null) return;`) and the reason it gives is exactly this transition. The
    parent's card is the only surface. G8's widening of `IsPublishableState` must not disturb that filter.

### D7 — no new ordinal range over `AgentRunState`

**BUILD:** every predicate this batch adds is an explicit set. The complete list of new ones:

| Predicate | Set |
|---|---|
| `TryPauseUserAsync` CAS source (SQL `State IN (@S1,@S2,@S3)`) | `Running`, `Verifying`, `WaitingForChildren` |
| `TryResumeFromPauseAsync` CAS source | `Paused` |
| `AgentRunSteeringService.PauseAsync` pre-check | `Running`, `Verifying`, `WaitingForChildren` |
| `HeadlessRunLauncher` resume-claim dispatch | `WaitingForInput` → `TryBeginResumeAsync`; `Paused` → `TryResumeFromPauseAsync`; else refuse |
| fan-out parked arm `:647` | `WaitingForInput`, `Paused` |
| stale-child settle `:816` | `WaitingForInput`, `Paused` |
| `IsPublishableState` | existing five **+ `Paused`** |
| `RunProgressViewModel.CanContinue` | `RunProgressState.WaitingForInput`, `RunProgressState.Paused` |
| `RunProgressViewModel.CanPause` | `RunProgressState.Running` (+ `WaitingForChildren`, see G8) |
| `ApplyPlanMutationAsync` gate | `Paused` (single value) |
| `KeepDoneAsync` step filter | `AgentStepStatus.Done`, `AgentStepStatus.Skipped` |

**Do NOT touch `AgentRunService.cs:445`** — see W15. And a tripwire worth knowing: widening `CanContinue` to
the two-member set trips nothing, but a careless `!IsTerminal(state)` widening reds
`RunProgressViewModelChildrenTests.cs:310` (`Assert.False(vm.CanContinue)` at `WaitingForChildren`,
documented at `:294`). That red is D7's rule showing up as a live failure.

---

## 2. Hazards — read this block before every group

Each is an instruction, not a warning.

1. **The pause path MUST be demonstrated to leave a RESUMABLE run, not a `Cancelled` one, on BOTH executors.**
   The failure is silent in the sense that the run *does* settle — just terminally, with `CompletedAt` stamped
   (`AgentRunService.cs:286`). The assertion is four-part, on Live and Headless alike:
   `State == AgentRunState.Paused` · `CompletedAt is null` · the aborted step's `Status == Pending` ·
   `RunPauseEnvelope.ReadReason(run) == AgentRunService.UserPausedReason`. Then **actually resume it** and
   assert it completes. A fact that only checks the state has not checked the thing.
2. **Consume the pause request BEFORE testing `r.Cancelled`, at both sites (W3).** `if (r.Cancelled && …)`
   is wrong and it is wrong in the direction that makes the run replan around work it thinks failed. There is
   no scenario in which the request exists and a pause is not wanted: only the pause command writes it, and
   `RunAsync` revokes stale ones on entry.
3. **NO ordinal range over `AgentRunState`.** `WaitingForChildren = 8` sits above the terminal band, so any
   range lies about it (`AgentEnums.cs:41`–`:57`). Explicit sets only, in C# **and** in SQL. The one surviving
   range (`AgentRunService.cs:445`, the startup sweep) is sanctioned — leave it (W15).
4. **Executor parity: every steering path works on Live AND Headless, or is refused on both.** The three
   differences that matter: Live marshals the step body onto the UI thread (`LiveTurnExecutor.PostAsync`
   `:226`–`:236`), so an unwind is only as prompt as the dispatcher; Live has a **second** OCE escape hatch at
   `:232` that makes `ExecuteStepAsync` *throw* instead of return, landing at orchestrator `:378`; and the
   action-card gate exists only on Live, where it can block a step indefinitely on an uncancellable TCS. The
   parity guardrail binds at the **UI** commit (G8): G3 and G4 are intermediate commits with no user-reachable
   pause on either executor, which is why the split is legal. Say so in each of those commit bodies.
5. **Privacy.** Nudge text and edited step titles/intents are USER CONTENT → `SensitiveDebug` only, never a
   release-visible line, and **never the System prompt**: `TokenizingAiClientService.TokenizeMessages:267`
   rewrites only `ChatRole.User` and hands the reply back detokenized, so user text on a System message ships
   restored PII straight past the tokenizer, silently. The Flow item's Title/Body stay generic and keyed. The
   pause **reason** token (`"user"`) is app-owned and may be logged and keyed
   (`RunPauseEnvelope.cs:16`–`:20`); nothing else about a pause may.
6. **Reuse the resume-once CAS + `Safe*` discipline; a steer must never corrupt or double-run a live loop.**
   Every new bookkeeping call goes through a `Safe*` wrapper or is a CAS that reports its own loss. Never a
   blind `SetStateAsync` for a steering transition — that is what would flip a `Cancelled` run back to
   `Running` (R11). Off-thread `RunChanged` stays marshaled: the panel VM posts to the raw
   `SynchronizationContext` it captured (`RunProgressViewModel.cs:274`), **not** `IUiDispatcher` (W14), and
   the awaitable template is `ApplyWorkspaceOutcomeAsync` (`:366`–`:402`).
7. **Register the dispatch's cancel sink and release it in the SAME `finally` that already calls
   `RemoveInflight`, with the same ownership guard.** `ReleaseDispatch(runId, ownCancel)` removes only when the
   stored delegate is reference-equal to its own, mirroring `RemoveInflight` (`HeadlessRunLauncher.cs:859`–`:863`)
   — a resume dispatch overwrites the entry at `:602` while the previous one is still unwinding, and an
   unguarded release would drop the *new* registration. **`ReleaseDispatch` must also drop any unconsumed
   pause request**, or the `!started` arms (`:394`–`:398`, `:565`–`:569`) — which settle the row themselves and
   never enter the orchestrator — leak a request that the next dispatch would consume.
8. **`-t:Rebuild` ALWAYS.** An incremental build reuses stale BAML, so a XAML change can appear to work while
   the file on disk says something else. There is no quick incremental check of a XAML change in this batch.
9. **CRLF on every new file.** Repo `.cs`/`.md` are CRLF and the `Write` tool emits LF. Convert immediately
   (`unix2dos <file>`) and verify CR count == LF count before committing.
10. **Batch 14 pinned the run panel. Adding a binding path is fine; renaming or re-hosting one trips those
    facts. Update them by ADDING an anchor, never by lowering a floor or deleting an assertion.** Specifically:
    `RunProgressPanelParseTests.cs:36` `MinimumBoundPaths = 18` may only be **RAISED** (it is exactly the
    walkable-tuple count at-or-before the Steps `ItemsControl` at `RunProgressPanel.xaml:74`); the eight
    anchors at `:84`–`:113` may not be deleted or re-anchored onto a now-multi-occurrence path (the
    single-occurrence rule is stated at `:88`–`:94`); `unresolved.Length == 0` (`:116`) may not be weakened;
    and `ViewHostDataContextTests.cs:111`–`:113` requires **exactly one** `RunProgressPanel` in
    `AssistantView`'s logical tree — **a second "steering" panel reds it, so all steering UI lives inside the
    existing panel.**
11. **A per-step command button gets ZERO coverage from the path walker.** `BindingPathWalker.TargetsDataContext`
    filters out `RelativeSource` bindings by design (breaking that filter puts every `loc:Str` markup
    extension back in the walker's path), and a `DataTemplate`'s content is never in the logical tree. Cover
    per-row commands with the shipped recipe in `ScheduledJobsRowTemplateTests.cs` — locate the `ItemsControl`
    by its declared `ItemsSource` path and `.Single()` it (`:49`–`:51`), `ItemTemplate.LoadContent()` into a
    throwaway `ItemsControl { ItemTemplate = parsed.ItemTemplate, DataContext = vm }`, find buttons by
    `PathOf(b, ButtonBase.CommandProperty)` (`:233`–`:234`), assert `ReferenceEquals(b.Command, expected)` and
    that `CommandParameter` is the row (`:308`, `:318`–`:327`), and assert the declared `IsEnabled` **PATH**,
    never its value (`:241`–`:242`).
12. **A DP at its default value is not evidence.** `Button.IsEnabled` defaults to `True` and
    `TextBlock.Visibility` to `Visible`, so every boolean-to-DP path must be observed in the direction only
    reachable through the binding. Batch 14's review found four booleans co-varying, which is why the shipped
    recipe asserts paths.
13. **New persisted enum members are APPEND-ONLY.** This batch adds **none** — `AgentRunState.Paused = 4` and
    `AgentStepStatus.Skipped = 4` both already exist and both are ordinal-pinned
    (`AgentRunServiceChildWaitTests.cs:53`–`:69` asserts `Assert.Equal(4, (int)AgentRunState.Paused)` at `:60`
    and `Assert.Equal(9, all.Length)` at `:67`). Likewise **no new `RunProgressState` member**:
    `RunProgressConvertersTests.cs:84`–`:85` asserts `Assert.Equal(8, Enum.GetValues<RunProgressState>().Length)`
    exactly, and `:39`–`:52` asserts N−1 distinct label keys. If you find yourself appending one, re-read
    §1 D6.
14. **Do not merge the two step-status filters.** `KeepDoneAsync` keeps `Done or Skipped`;
    `SafeSeedResumeContext` keeps `Done` only. They differ on purpose (W13) and each comment must say so.
15. **`RunProgressViewModel`'s localization field is `_localization`, not `_localizationService`, so none of
    its keys are covered by `LocalizationTests`' three regexes** (`:82`–`:87`), and `AllTranslations_MustBeComplete`
    only compares resx to resx — a key missing from all three files is invisible. Prefer `loc:Str` in the
    panel for static labels (auto-covered by `:52`–`:74`); for VM-formatted strings, **G2** widens the regex
    array with `_localization\[` and `_localization\.Format\(` — G2, not G8, because G2 is where the first
    VM-consumed new key lands (`Run_Activity_UserPaused`), and because the widening's first run is the moment
    it might red on a pre-existing violation: find that out in a two-file commit, not inside G8's
    floor-raise + anchor + Flow-arm work. (`_localization\[` does **not** match `_localizationService[` — the
    `[` must follow immediately.) That widening also retro-covers `Run_Unverified`,
    `Run_Publish_Failed`, `Run_Output_Branch` and `Run_Activity_*`; if it goes red, that is a **found defect**
    — add the missing key with its DE and FR.
16. **`[Collection("WpfApplicationStatic")]` on every new view-test class**, and dispose every
    `RunProgressViewModel` a fact constructs, in a `finally`, inside a `Run` body — it subscribes to
    `RunChanged` in its ctor (`:275`) and the STA host outlives every test. Add zero frame pushes and zero
    layout passes; the host's own notes (`WpfStaHost.cs:208`–`:213`) record what an eighth frame-pushing fact
    did to the gate.
17. **After every red demo, `git diff --stat -- src/` must be EMPTY before the group commit, and the commit
    body must say you ran it.**

---

## 3. THE GATE — run this verbatim at every group commit

```
dotnet build -t:Rebuild -v:n
dotnet build -t:Rebuild -v:n -c Release
dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj -- --filter-not-namespace "Pia.Wpf.Tests.Integration.Providers"
```

- **`0 Warning(s)` and `0 Error(s)` in BOTH configurations**, read off MSBuild's `N Warning(s)` summary line.
  At `-v:n` every warning prints **twice** (inline + summary), so grepping the log double-counts.
- **Confirm the rebuild was genuine.** Redirect to a log and check
  `grep -c "Roslyn.bincore.csc.exe /noconfig" build.log` **== 6** (4 code assemblies + 2 satellite de/fr
  resource compiles). Do **not** count `CoreCompile:` lines — parallel MSBuild reprints that header on every
  node resume and you will read 12.
- **The suite must reach `failed: 0`.**

**MEASURED BASELINE on this tree** (measured by the orchestrator, not read from a doc):

> Debug `0 Warning(s) / 0 Error(s)` · Release `0 Warning(s) / 0 Error(s)` ·
> suite **2734 total / 0 failed / 2733 passed / 1 skipped**

**EVERY GROUP RECORDS ITS OWN POST-COMMIT TOTAL** in its commit body, so the final report closes the
arithmetic as a *measured chain* (`2734 → … → N`), not inferred from a diff and not extrapolated from the
number of `[Fact]`s added.

**Two known intermittents — do not chase them.** Re-run the class isolated and say in the commit that you
did:

- `AssistantChatConcurrencyTests.DeleteAllAsync_WithAnotherConnectionCommittingThroughout_Completes`
- `TaskExtensionsTests.SafeFireAndForget_SlowTask_DoesNotBlock`

Isolated re-run form (note the namespace — `RootNamespace` is `Pia.Tests`, so a `Pia.Wpf.Tests.…` filter
matches zero tests; on this runner that is loud, `Zero tests ran / error: 1 / exit 8`, but read the `total:`
anyway):
`dotnet test tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj --no-build -- --filter-class "Pia.Tests.Services.<Class>"`

---

## 4. Ordering and dependency

```
G1 premise pin ──► G2 Paused transitions ──► G3 discriminator + restore (Headless)
                                              └─► G4 Live parity
                                                    └─► G5 D6 cascade
G2 ──► G6 mutation API
G3 ──► G7 nudge
G5,G6,G7 ──► G8 UI + strings ──► G9 docs
```

- **G1 first, non-negotiable.** The spec demands the D5 premise be pinned *before* building on it, the file is
  already written and green, and it establishes the measured chain.
- **G2 before G3.** G2 lands the two CASes with no callers, so the tree stays green and each CAS's source
  state is pinned in isolation before any loop depends on it.
- **G3 and G4 are one feature split across two commits** for reviewability. Both are green; neither exposes a
  user-reachable pause (no UI until G8), which is how the executor-parity guardrail is satisfied across the
  split. Do not stop after G3.
- **G6 and G7 are independent of each other** and of G4/G5; either order. Both need G2/G3 respectively.
- **G8 is the group most likely to be abandoned half-done, so it splits at a stated line:** commit 8a is
  pause/resume UI + Flow + the `CanContinue`/brush/anchor/floor work — that is what makes `Paused` reachable
  at all and it is independently green and shippable; commit 8b is the plan-mutation row commands + the nudge
  box + `SyncSteps` Title/`Move`. If the round runs short, land 8a and say 8b is outstanding.
- **G9 (docs) is last**, because two of its numbers are not known until G8 lands.

---

## 5. The contracts this batch adds

### 5.1 `IRunSteeringStore` / `RunSteeringStore` — NEW, `src/Pia.Wpf/Services/Interfaces/` + `src/Pia.Wpf/Services/`

Singleton, lock-free, nothing throws. Modelled on `ExecutingRunStore.cs` (read it first — same shape, same
`ConcurrentDictionary` discipline, same "the UI thread never waits on the run pool" property).

```csharp
public interface IRunSteeringStore
{
    /// <summary>Register THIS dispatch's cancellation sink (the OUTER cancel — HeadlessRunLauncher's
    /// per-run CTS, or ChatSession.Cancel(), which also releases pending action cards). Overwrites, like
    /// IExecutingRunStore.Register, and for the same reason: a resume dispatch may start while the
    /// previous one is still unwinding.</summary>
    void RegisterDispatch(Guid runId, Action cancel);

    /// <summary>Drop this dispatch AND any pause request it never consumed. Ownership-guarded: removes
    /// only when the stored delegate is the caller's own, mirroring HeadlessRunLauncher.RemoveInflight.</summary>
    void ReleaseDispatch(Guid runId, Action ownCancel);

    /// <summary>Record a user pause request. FALSE ⇒ no dispatch of this run is registered in this
    /// process, i.e. the pause is refused rather than silently dropped.</summary>
    bool RecordPauseRequest(Guid runId);

    /// <summary>Invoke the registered cancel sink. No-op when nothing is registered; never throws (a
    /// disposed source must not break a cascade — the rule CancelAsync already follows).</summary>
    void FireCancel(Guid runId);

    /// <summary>Take the pause request, if any. Removes it: a request is honoured exactly once.</summary>
    bool TryConsumePauseRequest(Guid runId);

    /// <summary>Drop a request WITHOUT honouring it — the terminal-intent cancel paths, and RunAsync's
    /// clear-on-entry.</summary>
    void RevokePauseRequest(Guid runId);
}
```

`RecordPauseRequest` and `FireCancel` are **two operations on purpose**: D6's cascade records the parent's
intent without ever firing the parent's token (§1 D6 build item 2), and one combined call would make that rule
invisible.

### 5.2 `IAgentRunService` — four new members

| Member | Source state (explicit set) | Target | ExtraJson | Ledger clock | Event |
|---|---|---|---|---|---|
| `PauseAsync` (existing) | *any* (blind) | `WaitingForInput` | `{paused:true,reason}` | close | `RunChanged(WaitingForInput)` |
| `TryBeginResumeAsync` (existing) | `WaitingForInput` | `Running` | `NULL` | open on win | `RunChanged(Running)` on win |
| `TryEndChildWaitAsync` (existing) | `WaitingForChildren` | `Running` | untouched | open on win | `RunChanged(Running)` on win |
| **`TryPauseUserAsync`** NEW | `Running`, `Verifying`, `WaitingForChildren` | **`Paused`** | `{paused:true,reason:"user"}` | close on win | `RunChanged(Paused)` on win |
| **`TryResumeFromPauseAsync`** NEW | `Paused` | `Running` | `NULL` (deliberate — W10) | open on win | `RunChanged(Running)` on win |
| **`ApplyPlanMutationAsync`** NEW | `Paused` (read-gate, not a CAS) | — | untouched | — | `RunChanged(state, stepId: null)` on Applied |
| **`UserPausedReason`** NEW const | — | — | `"user"` | — | — |

`TryPauseUserAsync` writes **no `CompletedAt`** — that is the difference between a pause and `FailAsync`
(`:286`). Its SQL is one self-contained statement, `UPDATE AgentRuns SET State=@New, UpdatedAt=@Now,
ExtraJson=@Extra WHERE Id=@Id AND State IN (@S1,@S2,@S3)`, with `MoveLedgerClock(runId, LedgerClock.CloseSegment)`
as a separate statement inside the same `_gate` hold, gated on `affected > 0` — the shape
`TryBeginResumeAsync:346`–`:350` established. The envelope is serialized through the **same** `new { paused =
true, reason }` shape and the same `JsonOptions` every other writer uses, never hand-written JSON
(`AgentRunService.cs:500`–`:501` says why).

`UserPausedReason = "user"` joins the closed, app-owned reason vocabulary and therefore needs arms in **both**
mappings that read it, or a user-paused run says "Stopped at budget":
`RunProgressViewModel.DescribePause` (`:544`–`:549`) and `AgentRunNotificationSurface.PausedBodyKey`
(`:88`–`:93`). Add `RunPauseEnvelope.cs:16`–`:20` to the doc-comment list of tokens in the same commit.

### 5.3 `IAgentRunSteeringService` / `AgentRunSteeringService` — NEW

```csharp
public interface IAgentRunSteeringService
{
    /// <summary>Request a USER pause of a run this process is dispatching. Returns false when the run is
    /// not found, is not in a pausable state, or is not dispatched here (a run parked in a previous
    /// process has no loop to interrupt). D6: a WaitingForChildren parent cascades to its children and
    /// deliberately does NOT fire its own token.</summary>
    Task<bool> PauseAsync(Guid runId, CancellationToken ct = default);
}
```

Registered `AddSingleton` beside `IExecutingRunStore` (`Bootstrapper.cs:513`) and `IRunSteeringStore`.
Depends on `IAgentRunService` + `IRunSteeringStore` + `ILogger`. **It never writes a row** — every state
transition is the loop's, via the CAS.

**Registration sites (producers) — three:**

| Site | Sink | Release |
|---|---|---|
| `HeadlessRunLauncher.LaunchCoreAsync`, after `:335` (`var runCts = …`), before `Task.Run` | `Action cancel = () => { try { runCts.Cancel(); } catch { } };` | `ReleaseDispatch(run.Id, cancel)` in the `finally` beside `RemoveInflight` (`:421`) |
| `HeadlessRunLauncher.ResumeAsync`, after `:514` | same shape | `finally` beside `:597` |
| `ChatSessionManager`, before `:871` | `Action cancel = () => session.Cancel();` — the cards matter (§1 D1 item 2) | a local `async Task` wrapper around the `RunAsync` await with a `finally`, still `.SafeFireAndForget(_logger)` |

**Revocation sites (terminal intent) — five, each revoking *before* it cancels:**

1. `AssistantViewModel.ExecuteCancelStreaming` (`:738`–`:741`) — the Stop button. Revoke for
   `ActiveSession?.ActiveRunId` (`ChatSession.cs:141`).
2. `AssistantViewModel.ExecuteClearConversation` (`:750`–`:759`) — destructive; same revoke.
3. `HeadlessRunLauncher.CancelThenTearDownWorkspaceAsync` (`:807`–`:811`), reached only from
   `OnChatsChanged` — chat delete must never become a pause.
4. `AgentRunOrchestrator.SafeCancelStaleChildrenAsync`, before `launcher.CancelAsync(old.Id)` (`:811`) — a
   superseded generation settles terminal.
5. `AgentRunOrchestrator`'s fan-out cascade registration (`:606`–`:610`) — the parent's token fired for a
   terminal reason, so its children must not pause.

**Deliberately NOT revoked, and say so in the code:** `HeadlessRunLauncher.StopAsync` (`:618`, app shutdown)
and `ChatSession.Dispose()` (`:1132`–`:1139`, window teardown / LRU retire — and the reaper never touches an
in-flight session, `ChatSessionManager.cs:437`–`:442`). A pending pause at shutdown yielding a **resumable**
run is strictly better than a `Cancelled` one, and that is the recoverable direction.

### 5.4 `AgentRunOrchestrator` — one new trailing-optional dependency

```csharp
/// <param name="steering">Batch 08. TRAILING and DEFAULTED, like every dependency this loop has gained:
/// null ⇒ no pause request can ever be consumed ⇒ this loop is byte-for-byte the pre-Batch-08 one, which
/// is what keeps a dozen positional test constructions unchanged.</param>
IRunSteeringStore? steering = null
```

That null-means-identical property is the reason the whole batch is additive. `RunProgressViewModel` gains
`IAgentRunSteeringService? steering = null` **last** and defaulted for the same reason — the discipline its
ctor states three times over (`:253`–`:277`) and which `AssistantViewModel.cs:403`–`:406` constructs
positionally.

---

## 6. G1 — pin the D5 premise (test-only)

**Commit boundary:** one commit, no production change. **Subject:**
`Tests: pin the D5 premise — a parked run settles handle.Completion and frees the scheduler`

**Files:** **NEW** `tests/Pia.Wpf.Tests/Services/D5PausePremiseTests.cs` (CRLF, ns `Pia.Tests.Services`).

Lift Ground F's measured file verbatim from
`C:\projects\Pia.Wpf\.claude\worktrees\wf_ea69488c-855-6\tests\Pia.Wpf.Tests\Services\D5PausePremiseTests.cs`
(read-only reference; that worktree is disposable). It was built and run at `1941e3c`: Debug and Release
`0 Warning(s) 0 Error(s)`, `total: 7 failed: 0`, re-run three times with no flake, and its two neighbouring
suites (`ScheduledJobBackgroundServiceTests` + `HeadlessRunLauncherTests`) stayed 57/0, so it collides with
nothing.

**The seven facts and what each asserts:**

| Fact | Assertion |
|---|---|
| `Park_SettlesTheHandleCompletion_AndLeavesTheRunResumable` | a wall-clock park settles `Completion` (a `TimeoutException` here **is** the premise being false), the row is `WaitingForInput`, `ExtraJson` contains `wall-clock`, the planner really planned (`PlanCalls == 1`), and the single step is still `Pending` |
| `DispatchThatThrows_AlsoSettlesTheHandleCompletion_NeverFaultsIt` | the structural half: `Completion` settles on **any** exit, so the premise carries to every D1 pause shape |
| `ParkedScheduledRun_DoesNotBlockTheNextDueJobOfTheSameTick` | end to end with the real launcher and the real scheduler: job 1 parks at its step cap and job 2 of the same tick still completes; the park advanced the schedule and failed nothing |
| `UnsettledCompletion_HoldsTheTick_SoTheSecondDueJobWaits` | the **control** — without it "job 2 ran" is consistent with a scheduler that never serialises |
| `RunLock_IsHeldAcrossAnUnsettledCompletion_AndFreedWhenItSettles` | probes `_runLock` directly through `RunNowAsync`, not the tick's `foreach` |
| `RunNotParkedWhenItsCompletionSettles_IsBookkeptAsAJobFailure` | the ordering caveat: a row reading `Cancelled` when `Completion` settles → `MarkRunFailedAsync("Cancelled")` + a failure toast + no schedule advance. **This is the fact that makes §1 D1 item 6 a testable invariant.** |
| `ParkedScheduledRun_AndTheNextOccurrenceOfTheSameJob_Coexist_WithNoGuard` | D5's new consequence, produced: two live resumable runs of one job, distinct ids, same `TriggerRef`, no guard |

**Two fixture facts to carry forward** (they will bite G5): the launcher passes `req.Budget` **verbatim**
(`:330`) so `WallClock: TimeSpan.Zero` parks on the first drain iteration with no AI call, whereas the
scheduler's budget goes through `RunProfile.FromBudget` (`:211`) which clamps wall-clock to ≥ 1 minute — so a
park via the scheduler must come from `ScheduledMaxSteps = 1` plus a ≥ 2-step plan. And
`HeadlessRunLauncherTests.BuildLauncher`'s `FakePlanner` returns `PlanResult.Fallback` **unconditionally**, so
no run built with it can ever park; Ground F's `StepPlanner` is the replacement and it lives in the new file
rather than by editing the shared fixture.

**Decides alone:** nothing — it is a lift. **Escalate:** if any of the seven is red on this tree, stop. The
premise moved between Ground F's run and yours, and D5 rests on it.

---

## 7. G2 — the `Paused` transitions and their CASes (service-only, no callers)

**Subject:** `Runs: make Paused a writable state — a user-pause CAS and a resume claim from it`

**Files:** `src/Pia.Wpf/Services/Interfaces/IAgentRunService.cs` · `src/Pia.Wpf/Services/AgentRunService.cs` ·
`src/Pia.Wpf/Services/RunPauseEnvelope.cs` (doc only) · `src/Pia.Wpf/ViewModels/RunProgressViewModel.cs`
(`DescribePause` arm) · `src/Pia.Wpf/Services/AgentRunNotificationSurface.cs` (`PausedBodyKey` arm) ·
`ViewStrings.resx` + `.de.resx` + `.fr.resx` · `tests/Pia.Wpf.Tests/Architecture/LocalizationTests.cs`
(the `_localization\[` / `_localization\.Format\(` regex widening — hazard 15, here because this commit adds
the first VM-consumed new key) · **NEW**
`tests/Pia.Wpf.Tests/Services/AgentRunServiceUserPauseTests.cs`.

**What lands:** `UserPausedReason`, `TryPauseUserAsync`, `TryResumeFromPauseAsync` (§5.2), the two mapping arms
(`Run_Activity_UserPaused`? **no** — see below; `Flow_Run_UserPaused` **yes**), and the resx entries. Nothing
calls the two CASes yet; that invariance is what makes this commit safe.

**`DescribePause` needs no new string.** `ComputeActivity` returns `null` for `AgentRunState.Paused`
(`RunProgressViewModel.cs:567`, "the state chip already carries it"), so the activity line is not rendered for
a paused run at all and `DescribePause` is never reached with `"user"`. Add the arm anyway — one line, and it
is the difference between a defensible default and a latent "Stopped at budget" the day someone makes the
activity line render for `Paused`. Map it to the existing `Run_State_Paused`? **No** — that is a chip label.
Map it to `Run_Activity_UserPaused` (§14) and accept the one extra string; a mapping arm that resolves to a
label written for another control is the kind of reuse that reads fine and breaks on the next copy edit.

**Facts (all in the new file, ns `Pia.Tests.Services`), each naming the assertion it makes:**

| Fact | Asserts |
|---|---|
| `TryPauseUser_FromRunning_WritesPausedWithTheUserReason_AndNoCompletedAt` | `State == Paused` · `CompletedAt is null` · `RunPauseEnvelope.ReadReason(run) == "user"` — the four-part resumability shape minus the step half |
| `TryPauseUser_FromVerifying_AndFromWaitingForChildren_AlsoWin` | `[Theory]` over the two other source states — the explicit set, pinned member by member |
| `TryPauseUser_FromEveryOtherState_LosesAndWritesNothing` | `[Theory]` over `Planning`, `WaitingForInput`, `Paused`, `Completed`, `Failed`, `Cancelled`: returns false **and** the row is byte-identical afterwards. The `Planning` row is §1 D1 item 8 made testable; the `Completed`/`Failed`/`Cancelled` rows are R11 |
| `TryPauseUser_ClosesTheLedgerWorkSegment` | worked time stops accruing at the pause, mirroring `PauseAsync` (`:306`–`:308`) |
| `TryResumeFromPause_ClaimsOnce_AndNullsTheEnvelope` | first call true, second false (guardrail 2), `ExtraJson is null` after the win — W10's deliberate erasure |
| `TryResumeFromPause_DoesNotClaimAWaitingForInputRun_AndTryBeginResumeDoesNotClaimAPausedOne` | the two claims are **disjoint** — the "pin the source state of all three" instruction, from both sides |
| `TryPauseUser_RaisesRunChangedPausedOnTheWinOnly` | no event on a lost CAS (a spurious `RunChanged(Paused)` would retract a Flow card for a run nobody paused) |
| `TheSweepStillLeavesAUserPausedRunAlone` | extends the existing theory shape at `AgentRunServiceChildWaitTests.cs:199`–`:218`: a `Paused` row with `{reason:"user"}` survives `FailInterruptedRunsAsync` **and keeps its envelope** |

**Decides alone:** SQL formulation, log wording, where in `AgentRunService.cs` the members sit (beside
`TryBeginResumeAsync`/`TryEndChildWaitAsync`, `:327`–`:421`).
**Escalate:** if `MoveLedgerClock` turns out not to be idempotent across a pause→resume→pause cycle. Ground B
read it as symmetric with `PauseAsync`/`TryBeginResumeAsync`; nobody measured a *double* cycle.

---

## 8. G3 — the pause/cancel discriminator and the `Pending` restore (Headless)

**Subject:** `Runs: tell a user pause from a stop, and give the aborted step back to the plan`

**Files:** **NEW** `src/Pia.Wpf/Services/Interfaces/IRunSteeringStore.cs`, `src/Pia.Wpf/Services/RunSteeringStore.cs`,
`src/Pia.Wpf/Services/Interfaces/IAgentRunSteeringService.cs`, `src/Pia.Wpf/Services/AgentRunSteeringService.cs` ·
`src/Pia.Wpf/Services/AgentRunOrchestrator.cs` · `src/Pia.Wpf/Services/HeadlessRunLauncher.cs` ·
`src/Pia.Wpf/Bootstrapper.cs` · **NEW** `tests/Pia.Wpf.Tests/Services/RunSteeringStoreTests.cs`,
`tests/Pia.Wpf.Tests/Services/AgentRunOrchestratorUserPauseTests.cs`.

**Orchestrator edits, precisely:**

1. `:91`–`:96` — hoist `Guid? inflightStepId = null;` beside `cancelled`/`failed`/`runFirst`/`runLast`.
2. before `:116` — `_steering?.RevokePauseRequest(run.Id);` with the comment that this is what makes a stale
   request from a previous dispatch structurally impossible (`_inflight`'s overwrite at
   `HeadlessRunLauncher.cs:602`).
3. `:293` — set `inflightStepId = step.Id;` next to the existing `SafeSetStepStatus(step.Id, Running, …)`.
4. **between `:295` and `:296`** — the pause branch, in the exact order §1 D1 item 6 fixes, gated on
   `_steering?.TryConsumePauseRequest(run.Id) == true` and **not** on `r.Cancelled` (W3):
   `SafeSetStepStatus(step.Id, Pending, CancellationToken.None)` → `SafeAddUsage(run.Id, r.Usage, CancellationToken.None)`
   → `PinRange()` → `TryPauseUserAsync` via a new `SafePauseUser` wrapper → `SafeOnPaused` → `return`.
   `CancellationToken.None` throughout: `cts.Token` is already cancelled, and the existing `catch` at `:384`
   already relies on these writes ignoring it. Neither `SetStepStatusAsync` nor `SetRunMessageRangeAsync`
   tests `ct` today, but passing `None` states the intent instead of depending on that.
5. after the step settles (`:301`) — `inflightStepId = null;`.
6. `:378` `catch (OperationCanceledException)` — the same branch, with the step restore conditional on
   `inflightStepId is { } sid`. Covers the `StepPersonaResolver`-OCE window (step left `Running`, W2), a pause
   during `SafeVerify` (no step in flight), and Live's second OCE escape hatch (`LiveTurnExecutor.cs:232`).
7. new `private async Task<bool> SafePauseUser(Guid runId)` beside `SafePause` (`:976`–`:980`) — failure-isolated,
   returns the CAS result so a lost CAS can be logged as "another writer owns this run" rather than silently
   read as success.
8. §5.3 revocations 4 and 5 (`:811`, `:606`–`:610`).

**Launcher edits:** the two `RegisterDispatch`/`ReleaseDispatch` pairs (§5.3), and the resume claim becoming
state-dispatched:

```csharp
// Batch 08 D6: TWO claims now, disjoint by source state, chosen from the row we already read. Explicit
// dispatch, never a range (D7) and never "try one then the other": a run whose state moved between the
// read and the CAS is not ours, and the loser's log line says so.
var claimed = run.State switch
{
    AgentRunState.WaitingForInput => await _agentRunService.TryBeginResumeAsync(runId, ct)…,
    AgentRunState.Paused          => await _agentRunService.TryResumeFromPauseAsync(runId, ct)…,
    _ => false,
};
```
replacing `:437`'s single call, keeping the `:439` "not claimable" log verbatim.

**Facts:**

| File | Fact | Asserts |
|---|---|---|
| `RunSteeringStoreTests` | `RecordPauseRequest_WithNoRegisteredDispatch_IsRefused` | a run this process is not dispatching cannot be paused — the first of the four collision hardenings |
| | `ReleaseDispatch_OnlyRemovesItsOwnRegistration` | reference-equality guard; simulate the resume-overwrites-while-unwinding order and assert the NEW sink survives (hazard 7) |
| | `ReleaseDispatch_DropsAnUnconsumedRequest` | the `!started` arms cannot leak a request into the next dispatch |
| | `TryConsumePauseRequest_HonoursARequestExactlyOnce` | second call false |
| | `FireCancel_WithADisposedSink_DoesNotThrow` | a disposed CTS must not break a cascade |
| `AgentRunOrchestratorUserPauseTests` | `UserPause_MidStep_LeavesTheRunResumable_OnHeadless` | the **four-part** shape of hazard 1 plus `CompletedAt is null`, then **resume and assert the run completes** |
| | `UserPause_DoesNotRecordTheAbortedStep` | the step's `Status == Pending`, `FirstMessageId`/`LastMessageId` still null, **no** per-step ledger entry — i.e. `:296` never ran (W2) |
| | `UserPause_WhoseStepReturnsSucceededFalseAndCancelledFalse_StillPauses` | **the W3 fact.** Drive an executor that returns `Succeeded:false, Cancelled:false` (what a declined action card produces) and assert the run is `Paused`, not replanned. Red before the fix, green after — the one red demo this group must produce |
| | `UserPause_BillsTheRunLevelLedger_AndSynthesizesNothingWhenUsageIsNull` | D2, both halves: a non-null `Usage` lands run-level with `stepId: null`; a null one writes nothing |
| | `GenuineCancel_StillSettlesCancelled` | no request ⇒ the pre-Batch-08 path, byte for byte. The no-interactive-regression guardrail |
| | `StalePauseRequest_FromAPreviousDispatch_IsNotHonoured` | clear-on-entry |
| | `NullSteeringStore_BehavesExactlyAsBeforeThisBatch` | the additive property §5.4 rests on |

**Decides alone:** file layout, log wording, whether `SafePauseUser` also logs the lost CAS.
**Escalate:** if `TryConsumePauseRequest` before `r.Cancelled` turns out to swallow a genuine Stop in some
path §1 D1 item 5 does not cover. That would mean a sixth revocation site, which is a decision, not a fix.

---

## 9. G4 — Live parity for the pause

**Subject:** `Runs: pause a live run too — and release the action card that would hold it`

**Files:** `src/Pia.Wpf/ViewModels/Models/ChatSessionManager.cs` ·
`src/Pia.Wpf/ViewModels/AssistantViewModel.cs` · `src/Pia.Wpf/Services/HeadlessRunLauncher.cs`
(revocation 3) · **NEW** `tests/Pia.Wpf.Tests/Services/AgentRunOrchestratorUserPauseLiveTests.cs` (or extend
G3's file — either, but the facts must be executor-labelled).

**What lands:** the live `RegisterDispatch` + wrapper release (§5.3 row 3), revocations 1–3, and the parity
facts. Nothing user-reachable yet.

**Facts:**

| Fact | Asserts |
|---|---|
| `UserPause_MidStep_LeavesTheRunResumable_OnLive` | the identical four-part shape as G3's headless fact, on `LiveTurnExecutor` + a real `ChatSession`. Executor parity, literally |
| `UserPause_ReleasesAStepBlockedOnAnActionCard` | a step parked at `ChatState.WaitingForTool` on an uncancellable TCS (`ActionCardInfo.cs:226`) is released by the pause, and the run reaches `Paused`. **This is the fact candidate (a) could not have passed** — assert the card's `State == Declined` and that no tool executed |
| `UserPause_DoesNotSettleTheLiveSessionCompleted` | `SafeOnPaused` → `OnPausedAsync` (`LiveTurnExecutor.cs:177`–`:189`) drops the session to `Idle` with no `TurnCompleted` and no `ChatState.Completed`; `IsStreaming` clears so Send/RunInBackground re-enable while the run sits paused (guardrail 5) |
| `StopButton_RevokesAPendingPause_AndTheRunSettlesCancelled` | revocation 1: Stop always wins over an unconsumed pause |
| `ClearConversation_RevokesAPendingPause` | revocation 2 |
| `ChatDelete_RevokesAPendingPause` | revocation 3, headless side |
| `Shutdown_DoesNotRevoke_SoAPendingPauseYieldsAResumableRun` | the deliberate asymmetry (§5.3), asserted rather than commented |

**A note the builder will need:** after `SafeOnPaused` the live executor **disposes `session.Cts`**
(`LiveTurnExecutor.cs:177`–`:189`), i.e. the pause destroys the very source the pause fired. That is fine —
the sink is `session.Cancel()`, `ReleaseDispatch` removes the registration in the wrapper's `finally`, and
`FireCancel` is documented never to throw. Do not "fix" the ordering.

**Escalate:** if releasing the card turns out to require touching `ActionCardInfo` or the tool loop. Adding a
`CancellationToken` to `WaitForUserDecisionAsync` is a change to the interactive gate and belongs to its own
batch (§19 Q2).

---

## 10. G5 — D6: the cascade pause of a fan-out

**Subject:** `Runs: pausing a delegating parent parks every child instead of failing them`

**Files:** `src/Pia.Wpf/Services/AgentRunOrchestrator.cs` (`:269`, `:647`, `:816`) ·
`src/Pia.Wpf/Services/AgentRunSteeringService.cs` (the cascade) · **NEW**
`tests/Pia.Wpf.Tests/Services/AgentRunOrchestratorCascadePauseTests.cs` · extend
`tests/Pia.Wpf.Tests/Services/D5PausePremiseTests.cs` with the scheduled-pause fact.

**Edits:** exactly the four in §1 D6 (cascade in the steering service; `:647` widened; `:816` widened; the
user-pause arm at `:269`). Nothing else. In particular **`:672` is not edited** — it is protected by not
firing the parent's token, and the commit body should say so, because a builder reaching for
`.WaitAsync(cts.Token)` to "release the parent" turns the whole feature into a cancel.

**Facts:**

| Fact | Asserts |
|---|---|
| `PausingAParent_ParksEveryChild_AndRecordsNoneAsFailed` | parent `Paused`, both children `Paused`, both sibling steps `Pending`, `ctx` recorded **no** failed sibling, and no step carries the `"child run did not settle"` error text (`FanOutStepResult:691`–`:693`). Red before the `:647` widening — the mandatory red demo |
| `PausingAParent_DoesNotFireItsOwnToken_SoItNeverSettlesCancelled` | the `:672` guard, from the outside: the parent's row is `Paused` and `CompletedAt is null` |
| `ResumingAPausedParent_SupersedesThePausedGeneration_AndDispatchesAFreshOne` | after Continue: the old children are terminal (not lingering), a new generation exists, and the parent completes. Red before the `:816` widening (drive it with the children NOT in `_inflight`, i.e. the restart shape) |
| `APausedChild_LeftBehind_IsNotOrphaned` | no non-terminal child row survives a completed parent — the leak `:813`–`:815` exists to prevent |
| `CascadePause_NeverAcquiresTheParentSlotPoolForAChild` | asserts the fresh generation went to `_childSlots`; the guard against §1 D6 item 8's two-parent deadlock |
| `PausedScheduledRun_AdvancesTheScheduleAndFailsNothing` (in `D5PausePremiseTests`) | **§1 D1 item 6 as a test**: user-pause a scheduled agent run, then assert `jobs.Failed` is empty, `jobs.Advanced` contains the job, and the notification surface recorded no failure. This is the fact that catches a builder who reorders the pause branch's three lines |

**Decides alone:** whether the cascade enumerates children once or re-reads after firing (once is enough — a
child that settles between read and fire simply has no registration).
**Escalate:** if a cascade-paused child's tokens turn out to be *newly* lost by this batch rather than
inherited from D13's supersede. Ground B's reading is that `default:`'s no-roll-up and `:817`'s bare `FailAsync`
already lose them; if that is wrong, D2 and D6 genuinely conflict and the owner decides (§19 Q1).

---

## 11. G6 — D3: the validated plan-mutation API (service-only)

**Subject:** `Runs: a validated plan mutation, and a skip that survives the next replan`

**Files:** `src/Pia.Wpf/Services/Interfaces/IAgentRunService.cs` · `src/Pia.Wpf/Services/AgentRunService.cs` ·
`src/Pia.Wpf/Services/AgentRunOrchestrator.cs` (`KeepDoneAsync:401`–`:412`) · **NEW**
`tests/Pia.Wpf.Tests/Services/AgentRunServicePlanMutationTests.cs`.

**Shape:**

```csharp
/// <summary>One submitted PENDING step. A null Id inserts; a non-null Id must name a Pending step of
/// this run. Ordinals are NEVER supplied — the service assigns them, which is what makes duplicate,
/// negative, non-contiguous and cross-the-Done-boundary ordinals structurally impossible (08 D3).</summary>
public sealed record PlanStepEdit(Guid? StepId, string Title, string? Intent, string? ExpectedArtifact, bool Skip = false);

public enum PlanMutationOutcome { Applied, NotPaused, UnknownStep, TitleRequired, EmptyPlan, TooLong, WriteFailed }

public readonly record struct PlanMutationResult(PlanMutationOutcome Outcome, int StepCount);
```

Implementation notes that are contract, not taste:

- Whole operation inside `lock (_gate)` + one `BeginTransaction`, so it is atomic against
  `NextPendingStepAsync` (same field, same class) and rolls back intact on any fault — the property
  `ReplaceStepsAsync:603`–`:651` already has.
- **Immutable prefix** = persisted steps with `Status != Pending`, in persisted ordinal order, original Ids,
  re-ordinaled `0..k-1`, all other columns verbatim (including `ExtraJson` — `AgentStep.cs:50`–`:61`:
  clobbering `parallelGroup` "quietly makes every fan-out plan sequential again").
- **Mutable tail** = the submitted list in submitted order, ordinals `k..n-1`; `Skip = true` writes
  `Status = Skipped`, everything else `Status = Pending`; a null `StepId` mints a new one (pass `Guid.Empty`
  and let the insert assign it — `:631`).
- Text normalized per §1 D3 item 9 **before** validation's blank check, so a title of only whitespace and
  newlines is `TitleRequired` rather than a row with an empty `Title`.
- `RunChanged(runId, run.State, stepId: null)` **only** on `Applied`.
- Logs: `Information` with run id + counts; `SensitiveDebug` for titles. Never both in one line.

**`KeepDoneAsync` widening** — one predicate and one doc sentence, and the doc sentence must say why
`SafeSeedResumeContext:448` is deliberately *not* widened (hazard 14).

**Facts:**

| Fact | Asserts |
|---|---|
| `Mutation_OnARunningRun_IsRefused_AndChangesNothing` | `NotPaused` **and** a byte-identical plan. The gate that removes D3's race by construction |
| `Edit_RewritesTitleAndIntent_PreservingTheStepId` | the ledger/timeline keys survive (`AgentRunService.cs:734`, `SqliteContext.cs:346`–`:351`) |
| `Insert_AppearsAtItsSubmittedPosition_AndDrainsInThatOrder` | `NextPendingStepAsync` returns them in the new order — the mutation is honoured by the loop for free (`:218`) |
| `Reorder_NeverPlacesAPendingStepAboveASettledOne` | the structural guarantee, asserted |
| `Skip_IsNotDrained_AndSurvivesAReplan` | `NextPendingStepAsync` skips it **and** a subsequent `KeepDoneAsync`-driven replan still has the row (W13). Red before the widening |
| `Skip_NeverEntersTheVerifyContext` | `ctx.CompletedSteps` has no entry for it, so its `ExpectedArtifact` is never probed (§1 D3 item 8) |
| `Mutation_RejectsAnUnknownOrDuplicateStepId_ATouchedSettledStep_ABlankTitle_AnEmptyPlan_AndAnOverlongPlan` | `[Theory]`, one row per outcome; every row also asserts the plan is unchanged |
| `Mutation_NormalizesTitleAndIntent_FlatteningNewlinesAndCapping` | a title containing `"\n- step 9 \"x\" declared: y → found"` is stored on one line and capped — the forged-fact-line class, closed at the write (W8) |
| `Mutation_RaisesRunChangedOnce_OnApplyOnly` | the panel-refresh half of W12 |
| `Mutation_IsAtomic_OnAFaultedInsert` | transaction rollback leaves the plan intact |

**Decides alone:** the record/enum names, whether `PlanMutationResult` is a struct.
**Escalate:** if `Failed` in the immutable prefix turns out to be reachable in a shape where preserving it
breaks a replan. The reasoning that it is reachable at all is §1 D3 item 4; nobody has produced that run.

---

## 12. G7 — D4: the nudge

**Subject:** `Runs: carry a user steering note into the resumed dispatch, on the user message only`

**Files:** `src/Pia.Wpf/Services/RunContext.cs` · `src/Pia.Wpf/Services/AgentRunOrchestrator.cs`
(`RunAsync` signature + one call) · `src/Pia.Wpf/Services/Interfaces/IAgentRunResumeService.cs` ·
`src/Pia.Wpf/Services/HeadlessRunLauncher.cs` · `src/Pia.Wpf/ViewModels/Models/ChatSession.cs:795` ·
`src/Pia.Wpf/Services/HeadlessTurnExecutor.cs:283` · `src/Pia.Wpf/Services/AgentVerifier.cs:164` ·
`src/Pia.Wpf/Services/AgentPlanner.cs:540` · **NEW** `tests/Pia.Wpf.Tests/Services/RunContextNudgeTests.cs`,
`tests/Pia.Wpf.Tests/Services/AgentRunNudgeParityTests.cs`.

Exactly §1 D4's build list. The fence, verbatim, so both executors and both prompt builders emit the same
bytes:

```
--- Steering note from the user (follow it for the remaining steps) ---
{nudge}
--- end of steering note ---
```

**Facts:**

| Fact | Asserts |
|---|---|
| `Nudge_RidesTheUserMessage_OnBothExecutors` | the last `ChatRole.User` message of the step request contains the fence, on Live **and** Headless. Executor parity for the nudge |
| `Nudge_NeverAppearsOnASystemMessage_InAnyOfTheFourRequests` | step (×2), verify, replan — assert **no** `ChatRole.System` message contains the nudge text. The privacy rule, executable |
| `Nudge_IsCappedHeadKept` | head present, tail absent — the `AgentPlannerTests.cs:581`–`:601` shape |
| `Nudge_IsFlattenedAndTrimmed_AndBlankBecomesNull` | whitespace-only ⇒ no fence at all, not an empty fence |
| `Nudge_ReachesTheCriticAndTheReplan` | D4's "both are correct and both should be intentional", asserted rather than assumed |
| `Nudge_IsNotSeededIntoThePersistedTranscript` | `HeadlessTurnExecutor.BeginRunAsync`'s persisted goal message is `ctx.Goal` verbatim (§1 D4 item 6) |
| `Nudge_DoesNotSurviveASecondResume` | scope-to-dispatch, asserted from the outside: resume with a nudge, park again, resume without one, and the second dispatch's request carries no fence. **This is the fact the UI note (`Run_Nudge_Scope_Note`) promises** |
| `Nudge_IsNeverLoggedAtInformationOrAbove` | scan the captured log for the text, the `AgentPlannerTests.cs:710`–`:724` shape |

**Decides alone:** `MaxNudgeChars` (1000 proposed), the fence wording (English, app-owned, not localized — it
is prompt scaffolding, not UI).
**Escalate:** nothing. If threading `nudge` through `RunAsync` proves awkward, the fallback is
`ctx.SetNudge` called by the launcher *before* `RunAsync` — but then `RunContext` would have to be
constructed outside the orchestrator, which is a bigger change than a trailing optional parameter.

---

## 13. G8 — the UI and the strings

**Two commits, split at a stated line.** 8a is independently green and shippable; 8b is the rest.

### 8a — `Runs: pause and resume a run from its panel`

**Files:** `src/Pia.Wpf/ViewModels/RunProgressViewModel.cs` ·
`src/Pia.Wpf/Controls/Assistant/RunProgressPanel.xaml` + `.xaml.cs` (W16) ·
`src/Pia.Wpf/Converters/RunProgressConverters.cs` · `src/Pia.Wpf/Services/AgentRunNotificationSurface.cs` ·
`src/Pia.Wpf/ViewModels/AssistantViewModel.cs` (ctor arg) · `src/Pia.Wpf/Bootstrapper.cs` ·
`ViewStrings.resx` ×3 ·
`tests/Pia.Wpf.Tests/Views/RunProgressPanelParseTests.cs` (floor + anchor) ·
`tests/Pia.Wpf.Tests/ViewModels/RunProgressViewModelSteeringTests.cs` NEW ·
`tests/Pia.Wpf.Tests/Converters/RunProgressConvertersTests.cs` (one theory row).

**VM:**

- `IAgentRunSteeringService? steering = null` **last** and defaulted (§5.4).
- `[ObservableProperty] private bool _isPausing;` with `[NotifyPropertyChangedFor(nameof(CanPause))]` +
  `[NotifyCanExecuteChangedFor(nameof(PauseCommand))]` — the `IsResuming` quartet exactly (`:76`–`:79`).
- ```csharp
  // PARENTHESIZE THE OR-PATTERN. Both predicates mix a pattern combinator with `&&`, and while `is` binds
  // tighter than `&&` so the bare form does compile as `(State is A or B) && …`, the unbracketed reading is
  // exactly the kind a builder "fixes" by guessing — and the wrong guess yields a button visible on a
  // terminal run. The existing `:83` never met this because it is an `==`, not a pattern.
  public bool CanPause =>
      _steering is not null
      && (State is RunProgressState.Running or RunProgressState.WaitingForChildren)
      && !IsPausing;

  public bool CanContinue =>
      (State is RunProgressState.WaitingForInput or RunProgressState.Paused) && !IsResuming;
  ```
  `CanPause`: explicit set. `Running` covers real `Running` **and** `Verifying` (which folds at `:528`);
  `Planning` is excluded by §1 D1 item 8; `WaitingForChildren` is D6.
  `CanContinue`: the two-member widening (D7's table; trips nothing per Ground E, and the `!IsTerminal` form
  would red `RunProgressViewModelChildrenTests.cs:310`).
- `[RelayCommand(CanExecute = nameof(CanPause))] private async Task Pause()` — `IsPausing = true`, `await
  _steering.PauseAsync(_runId)`, `catch` → log the **run id only**, `finally` → leave `IsPausing` true until
  `Project` sees a non-`Running` state (a pause that has been asked for but not yet landed must not re-enable
  the button). `Project` clears it. State in the doc comment that a run which never leaves `Running` leaves the
  button disabled — honest, and the alternative is a timer nobody can test.
- `PauseLabel` — `IsPausing ? _localization["Run_Action_Pausing"] : _localization["Run_Action_Pause"]`,
  notified from `OnIsPausingChanged`. Consumed from the VM, which is why hazard 15's regex widening ships in
  this commit.
- `[ObservableProperty] private string? _nudgeText;` lands here (8a) so `Continue()` can carry it:
  `await _resumeService.ResumeAsync(_runId, NudgeText)` and `NudgeText = null` in the `finally`. The **box**
  is 8b; a null property is inert.

**XAML** — header-right `StackPanel` (`:29`–`:45`), a third `ui:Button` **before** the ledger `TextBlock`:

```xml
<!-- Batch 08 D1: user pause. Visibility hides it off the pausable states; CanExecute also disables it
     while a request is in flight (the CAS in the loop is the hard guard, exactly as for Continue). -->
<ui:Button Content="{Binding PauseLabel}" Command="{Binding PauseCommand}"
           Appearance="Secondary" Padding="10,3" Margin="0,0,8,0" FontSize="12"
           Visibility="{Binding CanPause, Converter={StaticResource BooleanToVisibilityConverter}}" />
```

Three buttons is the header's stated ceiling before the ledger crowds — `CanPause` and `CanContinue` are
mutually exclusive by construction, and `CanPublish` only appears on a settled run, so at most two are ever
visible.

**Converter:** `RunStateToBrushConverter` `:92` `RunProgressState.Paused => "PiaAccentBrush"` — a paused run
now carries the same action-needed affordance `WaitingForInput` does (`:91`, "invites the Continue"). The
spinner stays **collapsed** for `Paused` (`:110`, pinned by `InlineData(Paused, false)`): that is what makes
the German participle `Pausiert` safe, and it is the whole lesson of `967d761`'s `Delegiert` → `Verteilt
Arbeit` fix. **Do not touch `Run_State_Paused` in any locale and do not add a spinner arm.**

**Flow:** widen `IsPublishableState` (`:78`–`:80`) with `or AgentRunState.Paused`, **and in the same edit**
widen the arm at `:143` to `if (state is AgentRunState.WaitingForInput or AgentRunState.Paused)`. The trap is
stated at `:72`–`:76`: any state past the filter with no arm of its own publishes a "run finished" item for a
run that is still working. Add `PausedBodyKey`'s `"user"` arm (G2 already shipped it). The parent-only filter
at `:133` stays exactly as it is (§1 D6 item 10). Without the Flow card a run the user paused in a background
chat is invisible forever — the sweep never touches `Paused`.

**Batch-14 pins:** raise `MinimumBoundPaths` to the **measured** walkable-tuple count at-or-before
`RunProgressPanel.xaml:74`, and add the anchor `Assert.Contains(bindings, b => b.Contains("=PauseCommand "))`
(single-occurrence in the markup, per the rule at `:88`–`:94`). Expect 18 → 20–21 for 8a (`PauseLabel`,
`PauseCommand`, `CanPause`) — **print the array and count it; do not take the number from this document.** If
the measured count is *lower* than 18, a region stopped reporting logical children and that is a finding, not
a reason to lower anything.

**Facts (8a):**

| Fact | Asserts |
|---|---|
| `CanPause_IsTrueOnlyForRunningAndWaitingForChildren` | `[Theory]` over all eight `RunProgressState` members — the explicit set, pinned member by member |
| `CanPause_IsFalseWhenNoSteeringServiceWasInjected` | the trailing-optional-null property: a build without the service renders exactly today's panel |
| `CanContinue_IsTrueForWaitingForInputAndPaused` | the widening, and false for `WaitingForChildren` (the existing `:310` fact stays green) |
| `Pause_InvokesTheSteeringService_AndLogsTheRunIdOnly` | privacy: scan the log for the goal and the step title |
| `Continue_CarriesTheNudgeTextAndClearsIt` | the D4 wiring, from the VM side |
| `PausedRun_PublishesAnActionRequiredCardWithContinueRun` | the Flow widening + arm; and a **child** run publishes nothing (`:133`) |
| `EveryNonTemplatedBindingPath_ResolvesOnTheViewModelThatHostsThePanel` (existing, extended) | raised floor + the new anchor; `unresolved.Length == 0` untouched |
| `RunProgressPanel_PauseButton_IsBoundToThePauseCommand` | `PathOf(b, ButtonBase.CommandProperty) == "PauseCommand"` and `ReferenceEquals(b.Command, vm.PauseCommand)`; assert the `Visibility` **path**, not its value (hazard 12) |

**RED demo (mandatory):** rename `PauseCommand`'s binding in the XAML, `-t:Rebuild`, confirm the build is
still `0 Warning(s)` and the anchor fact **fails**; revert; `git diff --stat -- src/` empty.

### 8b — `Runs: edit, insert, reorder and skip a paused run's plan, and nudge the next step`

**Files:** `RunProgressViewModel.cs` (row commands, `SyncSteps`, `StepRowViewModel`) ·
`RunProgressPanel.xaml` (row buttons + inline edit + nudge row) · `ViewStrings.resx` ×3 · **NEW**
`tests/Pia.Wpf.Tests/Views/RunProgressStepRowTemplateTests.cs` · extend
`tests/Pia.Wpf.Tests/ViewModels/RunProgressViewModelSteeringTests.cs`.

- **`StepRowViewModel.Title` becomes `[ObservableProperty]`** and `SyncSteps`'s else-branch assigns it. W12.
  `AssistantViewParseTests.cs:352`–`:359` constructs the row with an object initializer and still compiles.
- **`SyncSteps` gains an index-reconciling pass**: after the drop pass and the insert/update pass, for each
  plan ordinal, if the matching row is not at that index, `Steps.Move(oldIndex, ordinal)`. Without it a
  reorder that preserves Ids never repaints (W12). Add `Ordinal` to `StepRowViewModel` only if a row needs to
  *show* it; the `Move` needs no such property.
- **Row commands** live on the VM and reach the row via `CommandParameter="{Binding}"` +
  `Command="{Binding DataContext.<X>Command, RelativeSource={RelativeSource AncestorType=ItemsControl}}"` —
  the shipped shape at `Views/SettingsViews/AssistantView.xaml:583`–`:596`. `StepRowViewModel` gains
  `IsMutable` (`Status == Pending`) to gate `IsEnabled`, and `IsEditing` + `EditTitle`/`EditIntent` for the
  inline editor (inline, not a dialog — the panel is embedded in a chat).
- Verbs: `EditStep` (opens the inline editor) / `SaveStepEdit` / `CancelStepEdit` / `InsertStepBelow` /
  `MoveStepUp` / `MoveStepDown` / `SkipStep`. Each builds the full submitted Pending list from `Steps` and
  calls `ApplyPlanMutationAsync` **once**; a non-`Applied` outcome sets a localized
  `PlanMutationNote` (muted line, the `PublishNote` shape at `:64`–`:67`) and the VM re-projects from the DB
  so the panel never shows a mutation that did not land.
- **All row commands are gated on `State == RunProgressState.Paused`** — one `CanMutatePlan` the row
  templates bind, plus the muted `Run_Plan_Note_PauseFirst` line whenever the run is live. That is D3's "the
  UI can require a pause first… state it as the design."
- **Nudge row** between the note lines (`:71`) and the step list (`:74`): a `TextBox` bound to `NudgeText`
  with `Visibility="{Binding CanContinue, …}"`, a `loc:Str` label, and the `Run_Nudge_Scope_Note` muted line.
  Placed before `:74` deliberately, so its bindings count toward the raised floor.
- **Marshaling:** every mutation writes off the UI thread and re-projects through `_uiContext.Post` + a
  `TaskCompletionSource`, the `ApplyWorkspaceOutcomeAsync` template (`:366`–`:402`) — which is also what lets
  a fact await the mutation instead of racing it (W14, hazard 6).

**Facts (8b):** the row-template file follows `ScheduledJobsRowTemplateTests.cs` character for character
(hazard 11) and asserts, per verb: the command **path**, `ReferenceEquals(button.Command, vm.<X>Command)`,
`CommandParameter` is the row instance, and the declared `IsEnabled` **path** (`"IsMutable"`), never its
value. Plus: `EveryVerb_RoundTripsThroughApplyPlanMutationAsync_Once`;
`AFailedMutation_ShowsALocalizedNote_AndDoesNotChangeTheRows`;
`EditingAStepTitle_RepaintsTheRow_WithoutReMintingItsId` (red before the `[ObservableProperty]` change);
`ReorderingSteps_MovesTheExistingRows` (red before the `Move` pass);
`RowCommands_AreDisabledWhileTheRunIsLive`.

**RED demos:** the two named above, both against real markup/real VM, both reverted, `git diff --stat -- src/`
empty before commit.

**Escalate (either commit):** if the step row cannot carry five affordances without the panel becoming
unreadable. Collapsing them into a per-row overflow menu is a design decision — and note there is **no**
`ContextMenu` anywhere in this feature today, so adding one is net-new surface, not a reuse.

---

## 14. Every new user-visible string

All land in `src/Pia.Wpf/Resources/Strings/ViewStrings.resx` **and** `.de.resx` **and** `.fr.resx`.
`Designer.cs` is **never** hand-edited. `AllTranslations_MustBeComplete`
(`tests/Pia.Wpf.Tests/Architecture/LocalizationTests.cs:114`–`:155`) is bidirectional: a key in `.de` that is
absent from the base file is an **orphan** and also red.

**The state chip needs none** — `Run_State_Paused` already exists in all three (`ViewStrings.resx:909`
`Paused` · `.de.resx:937` `Pausiert` · `.fr.resx:937` `En pause`). **The resume needs none** —
`Run_Action_Continue` already exists (`:918` / `:946` / `:946`). **Save/Cancel need none** — reuse
`Common_Save` / `Common_Cancel` from `CommonStrings.resx:34`–`:35` (present in all three).

| Key | EN | DE | FR | Group |
|---|---|---|---|---|
| `Run_Action_Pause` | `Pause` | `Pausieren` | `Mettre en pause` | 8a |
| `Run_Action_Pausing` | `Pausing…` | `Wird pausiert…` | `Mise en pause…` | 8a |
| `Run_Activity_UserPaused` | `You paused this run` | `Sie haben diese Ausführung pausiert` | `Vous avez mis cette exécution en pause` | G2 |
| `Flow_Run_UserPaused` | `You paused this run. Continue?` | `Sie haben diese Ausführung pausiert. Fortsetzen?` | `Vous avez mis cette exécution en pause. Reprendre ?` | G2 |
| `Run_Nudge_Label` | `Note for the rest of this run` | `Hinweis für den Rest dieser Ausführung` | `Remarque pour la suite de cette exécution` | 8b |
| `Run_Nudge_Placeholder` | `Optional — for example, keep the summary under 200 words` | `Optional – zum Beispiel: Zusammenfassung unter 200 Wörter halten` | `Facultatif – par exemple : limiter le résumé à 200 mots` | 8b |
| `Run_Nudge_Scope_Note` | `The note is sent with the next step of this continuation only. It is not saved and does not survive a restart.` | `Der Hinweis wird nur mit dem nächsten Schritt dieser Fortsetzung gesendet. Er wird nicht gespeichert und übersteht keinen Neustart.` | `La remarque n'est envoyée qu'avec la prochaine étape de cette reprise. Elle n'est pas enregistrée et ne survit pas à un redémarrage.` | 8b |
| `Run_Plan_Action_Edit` | `Edit step` | `Schritt bearbeiten` | `Modifier l'étape` | 8b |
| `Run_Plan_Action_Insert` | `Insert step below` | `Schritt darunter einfügen` | `Insérer une étape en dessous` | 8b |
| `Run_Plan_Action_MoveUp` | `Move step up` | `Schritt nach oben` | `Déplacer l'étape vers le haut` | 8b |
| `Run_Plan_Action_MoveDown` | `Move step down` | `Schritt nach unten` | `Déplacer l'étape vers le bas` | 8b |
| `Run_Plan_Action_Skip` | `Skip step` | `Schritt überspringen` | `Ignorer l'étape` | 8b |
| `Run_Plan_NewStep_Title` | `New step` | `Neuer Schritt` | `Nouvelle étape` | 8b |
| `Run_Plan_Note_PauseFirst` | `Pause the run to change its plan.` | `Pausieren Sie die Ausführung, um den Plan zu ändern.` | `Mettez l'exécution en pause pour modifier son plan.` | 8b |
| `Run_Plan_Error_NotPaused` | `The plan can only be changed while the run is paused.` | `Der Plan kann nur geändert werden, während die Ausführung pausiert ist.` | `Le plan ne peut être modifié que pendant la pause de l'exécution.` | 8b |
| `Run_Plan_Error_UnknownStep` | `That step is no longer part of the plan.` | `Dieser Schritt ist nicht mehr Teil des Plans.` | `Cette étape ne fait plus partie du plan.` | 8b |
| `Run_Plan_Error_TitleRequired` | `A step needs a title.` | `Ein Schritt braucht einen Titel.` | `Une étape doit avoir un titre.` | 8b |
| `Run_Plan_Error_EmptyPlan` | `A plan needs at least one step.` | `Ein Plan braucht mindestens einen Schritt.` | `Un plan doit comporter au moins une étape.` | 8b |
| `Run_Plan_Error_TooLong` | `The plan has too many steps.` | `Der Plan hat zu viele Schritte.` | `Le plan comporte trop d'étapes.` | 8b |
| `Run_Plan_Error_WriteFailed` | `The plan change could not be saved.` | `Die Planänderung konnte nicht gespeichert werden.` | `La modification du plan n'a pas pu être enregistrée.` | 8b |

Twenty keys. Copy each row into the three files **one key at a time**, not one file at a time: the parity test
checks a key's *presence* in every locale and never its *language*, so a block copy-paste that lands German in
`.fr.resx` is green. That is the one defect class this whole table cannot protect against.

**Placement:** append into the existing `Run_*` block (`ViewStrings.resx` around `:909`–`:930`, and the
matching `:937`–`:958` regions in `.de`/`.fr`) so the three files stay diff-comparable line for line.

**Coverage:** `Run_Plan_Action_*`, `Run_Nudge_Label`, `Run_Nudge_Placeholder`, `Run_Nudge_Scope_Note`,
`Run_Plan_Note_PauseFirst` and `Run_Action_Pause` should be consumed via `loc:Str` in the panel, which
`LocalizationTests.cs:52`–`:74` covers automatically. `Run_Action_Pausing`, `Run_Activity_UserPaused` and the
six `Run_Plan_Error_*` are consumed from the VM through `_localization[...]`, which **no** existing regex
sees (hazard 15) — **G2** widens the regex array with `_localization\[` and `_localization\.Format\(`, in the
same commit that adds the first of them (`Run_Activity_UserPaused`), so the remaining seven are covered from
the moment they land. `Flow_Run_UserPaused` is reached through `PausedBodyKey`, which
`LocalizationTests`' code regex does cover via `_localizationService[...]` at
`AgentRunNotificationSurface.cs:155`.

---

## 15. Acceptance

1. A user can **pause** a live interactive run mid-step from the run panel, and the run settles
   `AgentRunState.Paused(4)` with `CompletedAt` null, the aborted step back to `Pending`, and
   `{"paused":true,"reason":"user"}` in `ExtraJson`. Demonstrated on **both** executors.
2. **Continue** claims a `Paused` run through `TryResumeFromPauseAsync` and the run completes. The two claim
   methods are disjoint by source state, pinned from both sides.
3. `Paused` is a **real driven state**: written by `TryPauseUserAsync`, rendered with an accent chip and a
   Continue affordance, published as a Flow `ActionRequired` card with `ContinueRunAction`, read as
   not-executing by the composer, excluded from the crash sweep, and advanced-past by the scheduler.
4. Pausing a `WaitingForChildren` parent parks every child at `Paused`, records **none** as a failed sibling,
   orphans none, and re-dispatches a fresh generation on `_childSlots` on resume.
5. Editing, inserting, reordering and skipping a **paused** run's pending steps is validated, atomic,
   `RunChanged`-visible, and honoured by the drain loop on the next step. A user `Skipped` step survives a
   replan.
6. A nudge reaches the next step's **user** message on both executors, reaches the critic and the replan,
   appears on **no** System message, is capped head-kept, is logged only via `SensitiveDebug`, and is visibly
   gone after a second resume — which is what the UI note promises.
7. Every steering path works on Live **and** Headless. No new ordinal range over `AgentRunState`. No new
   persisted enum member. Every new string in all three resx files.
8. `0 Warning(s) / 0 Error(s)` in **both** configurations under `-t:Rebuild`, suite `failed: 0`, and the
   measured chain `2734 → … → N` closed in the final report.

---

## 16. What this batch adds to the Rank-1 manual round

`00-OVERVIEW.md` tracks this as a first-class number and Batch 08 **lengthens** it. The batch spec named six
items; refined against what the grounding measured, there are **seven**, and the wording of two changes
because the design does.

1. **Pause a live interactive run mid-step; confirm it resumes and completes rather than settling cancelled.**
   Unautomatable in the part that matters: the step must be genuinely mid-provider-call with a real model
   streaming, which is the only condition under which the executors' cancel arms behave as they do in
   production. The automated facts drive fakes.
2. **Pause a live run that is sitting on an action card** (a `write_file` awaiting the user's click) and
   confirm it pauses rather than declining-and-continuing. **New item, and the highest-value one** — it is
   the W3 failure mode, and no test can reproduce a human not clicking a card while a real provider holds the
   tool loop open.
3. **Pause a scheduled run and confirm another due job dispatches while it sits paused** — the D5 premise,
   observed rather than reasoned. Now partly automated (G1 + G5), so the manual half narrows to: a real
   scheduled job, a real clock, a real Flow card, and the schedule visibly advanced in Settings.
4. ~~**Pause a fan-out parent; confirm every child parks, none is orphaned, and every one resumes.**~~
   **CORRECTED 2026-08-01, as-built at `3de2ac1` — twice, and neither correction re-argues D6.** Read:
   **Pause a fan-out parent; confirm every child THAT WAS PAUSABLE parks, none is orphaned, and Continue
   SUPERSEDES the paused generation and re-dispatches the group fresh.** F20: "every one resumes" is the
   opposite of what D6 does —
   `ResumingAPausedParent_SupersedesThePausedGeneration_AndDispatchesAFreshOne` pins the supersede and
   `SafeCancelStaleChildrenAsync` cancels the old generation on the way in. F1's fix: a child outside the
   pausable set (still `Planning`, or still queued behind the child slot pool) is deliberately **left running**
   rather than cancelled, so "every child parks" was never achievable either. A smoke script written from the
   struck wording would have failed against reality. The
   unautomatable half is unchanged and is the **restart**: kill the app with children at `Paused`, relaunch, and
   confirm the startup sweep leaves them and the parent's Continue supersedes them (the `:816` widening across a
   process boundary, which no in-process fact reaches).
5. **Edit, insert, reorder and skip a pending step of a PAUSED run and watch the run honour each on its next
   step.** Reworded: the batch spec said "a pending step" without the pause precondition, and under D3 the
   controls do not exist on a live run. A tester looking for them on a running run will report a bug that is
   the design.
6. **A nudge that visibly changes the next step's behaviour — and that it is visibly gone after a second
   resume.** Reworded: the observable sequence is **pause → type the note → Continue → the next step differs**.
   There is no nudge control on a running run (D4). The tester must know that, or item 6 reads as a missing
   feature.
7. **DE/FR for all ~~twenty~~ TWENTY-FIVE new strings without clipping** (**corrected 2026-08-01**: twenty was
   right at `c4d141b`; the should-fix pass `3de2ac1` added five more — `Run_Pause_Error_Refused`,
   `Run_Plan_Edit_Title`, `Run_Plan_Edit_Intent`, `Run_Activity_ResumeInterrupted`, `Flow_Run_ResumeInterrupted`
   — for **25** `<data name=` entries added to **each** of the three resx files, measured by
   `git diff 1941e3c..3de2ac1`. Two of the five are long: `Run_Pause_Error_Refused` is 138 chars in DE, and the
   FR `Run_Nudge_Scope_Note` grew to 200 under F18's rewording, still the longest new string in any locale),
   specifically: the three-button header at DE widths
   (`Pausieren` + `Fortsetzen` + `Dateien veröffentlichen` never co-occur, but `Wird pausiert…` +
   `Fortsetzen` can), the FR `Run_Nudge_Scope_Note` (the longest new string in any locale), and the five row
   verb tooltips inside a step row that already carries a glyph, an avatar, a trimmed title and a token
   count.

**Genuinely unautomatable, and why**, in one line each: (2) needs a human not-clicking; (4) needs a real
process death; (7) needs pixels; (1) and (6) need a real provider mid-stream; (3) needs a real clock. (5) is
the one item that is *mostly* automatable and stays manual only for the drag-free reorder ergonomics.

---

## 17. Files touched, by group

| Group | src/ | tests/ | docs/ |
|---|---|---|---|
| G1 | — | `Services/D5PausePremiseTests.cs` NEW | — |
| G2 | `AgentRunService.cs`, `IAgentRunService.cs`, `RunPauseEnvelope.cs`, `RunProgressViewModel.cs`, `AgentRunNotificationSurface.cs`, 3× resx | `Services/AgentRunServiceUserPauseTests.cs` NEW, `Architecture/LocalizationTests.cs` MOD (regex widening) | — |
| G3 | `IRunSteeringStore.cs` NEW, `RunSteeringStore.cs` NEW, `IAgentRunSteeringService.cs` NEW, `AgentRunSteeringService.cs` NEW, `AgentRunOrchestrator.cs`, `HeadlessRunLauncher.cs`, `Bootstrapper.cs` | `Services/RunSteeringStoreTests.cs` NEW, `Services/AgentRunOrchestratorUserPauseTests.cs` NEW | — |
| G4 | `ChatSessionManager.cs`, `AssistantViewModel.cs`, `HeadlessRunLauncher.cs` | `Services/AgentRunOrchestratorUserPauseLiveTests.cs` NEW | — |
| G5 | `AgentRunOrchestrator.cs`, `AgentRunSteeringService.cs` | `Services/AgentRunOrchestratorCascadePauseTests.cs` NEW, `Services/D5PausePremiseTests.cs` MOD | — |
| G6 | `IAgentRunService.cs`, `AgentRunService.cs`, `AgentRunOrchestrator.cs` | `Services/AgentRunServicePlanMutationTests.cs` NEW | — |
| G7 | `RunContext.cs`, `AgentRunOrchestrator.cs`, `IAgentRunResumeService.cs`, `HeadlessRunLauncher.cs`, `ChatSession.cs`, `HeadlessTurnExecutor.cs`, `AgentVerifier.cs`, `AgentPlanner.cs` | `Services/RunContextNudgeTests.cs` NEW, `Services/AgentRunNudgeParityTests.cs` NEW | — |
| G8 | `RunProgressViewModel.cs`, `RunProgressPanel.xaml(.cs)`, `RunProgressConverters.cs`, `AgentRunNotificationSurface.cs`, `AssistantViewModel.cs`, `Bootstrapper.cs`, 3× resx | `Views/RunProgressPanelParseTests.cs` MOD, `Views/RunProgressStepRowTemplateTests.cs` NEW, `ViewModels/RunProgressViewModelSteeringTests.cs` NEW, `Converters/RunProgressConvertersTests.cs` MOD | — |
| G9 | — | — | `00-OVERVIEW.md`, `08-live-steering.md` status line |

---

## 18. G9 — the docs commit (last)

**Subject:** `Docs: record Batch 08, and say which manual items it added`

- `00-OVERVIEW.md`: a chronicle row for Batch 08 with its **pinned start and end commits** (the shape Batch 09's
  row uses); the Rank-1 count **lengthened by seven** with items 5 and 6 reworded per §16 — and note that this
  is the row Batch 14 last moved, so credit both in one sentence rather than leaving a number two batches
  behind. If `RunContext.Scratchpad` is still dead after this batch (it is — §1 D4 rejected it as the nudge
  carrier), the open item at `:820` stands and its **line number is stale**: the field is at `RunContext.cs:72`,
  not `:85`. Fix the reference, do not close the item.
- `08-live-steering.md`: status line → shipped, with the commit range, and a one-line pointer to this file for
  the four spec-line corrections.
- **Do not** claim a shortening. This batch adds manual debt; it removes none.

---

## 19. Open questions — none blocking, all for the owner

**Q1 — a cascade-paused child's tokens never reach the parent's ledger.** D2's headline is "its tokens are
still BILLED", and this is the one place the batch cannot honour it. The widened `:647` arm rolls up nothing
(deliberately, D13: "its tokens are pushed once, from a terminal branch"), and
`SafeCancelStaleChildrenAsync:817` supersedes the child with a bare `FailAsync` that also rolls up nothing.
So a paused-then-superseded child's spend is real and invisible. **Inherited from D13, not introduced here** —
the same hole exists today for a budget-parked child that gets superseded. Fixing it means rolling up at
supersede time, which touches D15's "pushed once from a terminal branch" invariant. Not this batch.

**Q2 — `ActionCardInfo.WaitForUserDecisionAsync()` takes no `CancellationToken`** (`:226`), so the only way to
release a step at `WaitingForTool` is `card.CancelCommand`. This batch routes through it and it works, but it
means the *pause* mechanism on Live is two mechanisms (token + card command) where Headless has one. Adding a
token to that wait is a change to the interactive tool gate and belongs to its own batch.

**Q3 — an edited step title lands on a System prompt one step later.** `AgentVerifier.cs:144` and
`AgentPlanner.cs:525` interpolate `Title`/`Intent` from `ctx.CompletedSteps` into a **`ChatRole.System`**
message, and `TokenizingAiClientService` rewrites only `ChatRole.User`. Once an edited Pending step runs it
becomes Done and its user-authored title reaches System untokenized. G6's write-time normalization closes the
**forged-fact-line** half; the **PII** half is unchanged, and it is a property the code already has for
planner-authored titles that paraphrase the user's goal. Fixing it means moving those System fact blocks to the
User message, which changes every plan and verify request shape. **Owner call, out of this batch's scope, and
stated here rather than buried in a hazard bullet so it is not mistaken for a fix.**

**Q4 — two live runs of one scheduled job.** Measured (Ground F fact 7). `AgentRuns.TriggerRef` is written and
indexed and read by nothing. A user pause makes it reachable on demand. A guard would be a
`TriggerRef`-scoped "is a non-terminal run of this job already live" query before dispatch — cheap, but it
changes scheduled-job dispatch semantics, so it is a decision.

**Q5 — the sweep's set-vs-threshold asymmetry.** `AgentRunService.cs:445` is the one sanctioned range, and
under append-only a tenth member at ordinal 9 escapes `State < 3` **silently** even if it is
crash-recoverable. The mirror image of the hazard D7 forbids. A `[Theory]` over
`Enum.GetValues<AgentRunState>()` asserting the intended sweep verdict for every member would catch it;
`AgentRunServiceChildWaitTests.cs:199`–`:218` is already that shape and could simply gain a non-vacuity count
assertion.

**Q6 — `MoveLedgerClock` across a double pause→resume→pause cycle is unmeasured.** G2 pins one cycle. Ground B
read the open/close pairing as symmetric; nobody has run two.

**Q7 — `IsPausing` has no timeout.** A run whose step never returns leaves the Pause button disabled and the
label at "Pausing…". Honest, testable and recoverable (the run's own budget eventually parks it), but if the
owner wants a visible "this is taking a while" the place is `Project`, not a timer.
