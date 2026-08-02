# Batch 08 — Live steering (plan mutation / nudge / pause / resume)

**Phase 4 · Size L · Work on `feature/agent-run-spine`**, after the budget-pause batch (shipped) and
sub-agents (see the chronicle in [`00-OVERVIEW.md`](00-OVERVIEW.md))

_**Status 2026-08-01 — ✅ SHIPPED.** Build `8166f4a` → `c4d141b` (G1–G8 plus a simplify, 11 commits), impl spec
`7772602` **before** that range, adversarial review `12601ef`, and a **three-commit** fix pass
`59dfbde` → `3de2ac1`. Gate on the final tree: Debug and Release `-t:Rebuild -v:n` both
**0 Warning(s) / 0 Error(s)**, suite **2882 / 0 failed / 2881 passed / 1 skipped**, **+148**. Read
[`08-live-steering.impl.md`](08-live-steering.impl.md) **before this file**: its §0 tabulates **sixteen** places
this spec was wrong against the tree at `1941e3c` — four of them `src/` line references — and where the two
disagree, the impl spec wins. Read
[`08-live-steering.review.md`](08-live-steering.review.md) next: **this batch shipped its own named central risk
(the Guardrails bullet below) as a defect on three reachable paths**, and only an adversarial review with
executing verifiers caught it; all four must-fixes are closed. Dispositions, open items and the seven Rank-1
manual items this batch adds live in "Opened by Batch 08" in [`00-OVERVIEW.md`](00-OVERVIEW.md)._

_**Superseded status, kept because it is the record.** Rank 3 — behind Rank 1 (the manual Windows smoke round)
and Rank 2 ([Batch 14](14-view-coverage-debt.md)). Its dependency on Batch 07 cleared when Phase 3 shipped, and
its seven decisions are now **RESOLVED by the owner** — see §Decisions. Not built. The "Key seams" section below
has been re-measured against the tree at `aa5beb9` and **three of its four bullets needed correcting**; read
the corrections, not the original claims._

The run-progress plan is **read-only** in Phases 1–3; the mutation API shape is reserved for Phase 4
(plan §2 Q2 line 20, §7.2 line 300, §9 line 363-364, §15.6 line 1000). This batch makes the plan steerable.

## Goal

Let the user steer a live run: edit/reorder/insert/skip pending steps, nudge the agent mid-run, and
user-initiate **pause/resume** — turning the reserved `AgentRunState.Paused` (4) into a driven state (distinct
from the system-driven `WaitingForInput` budget-pause from the budget-pause batch).

## Key seams — re-measured at `aa5beb9`

- `RunProgressViewModel` / `RunProgressPanel.xaml` — today read-only; add the mutation commands (the Continue
  button from the budget-pause batch is the first sanctioned interaction — extend that surface).
- `AgentRunState.Paused` (4) — **more is already in place than this bullet used to claim, and all of it
  checks out.** The state is declared (`AgentEnums.cs:36`) and never written by anything. It already
  *renders*: `RunProgressState.Paused` maps to `Run_State_Paused`, which exists in **all three** resx files
  (`Paused` / `Pausiert` / `En pause`), so the state chip costs no new string. It is already excluded from
  the crash sweep as a deliberate park (`AgentRunService.cs:436`), already read as *not executing* so the
  composer stays open (`ChatSessionManager.cs:591`/`:603`), and already treated as a deliberate park by the
  scheduler (`ScheduledJobBackgroundService.cs:251`). **What is missing is only a driver.**
- **`TryBeginResume`/`PauseAsync` cannot simply be reused — the original bullet said "reuse" and that is
  wrong.** `PauseAsync` writes `{paused:true,reason}` and sets `WaitingForInput` specifically;
  `TryBeginResumeAsync` CASes `WaitingForInput → Running` specifically **and nulls `ExtraJson`
  unconditionally**, which would erase a user pause's own reason. Expect a parameterized CAS or a sibling
  pair, and pin which state each claim method transitions from — there will be **three** of them once D6's
  child-wait resume is added.
- **`IAgentRunService.ReplaceStepsAsync` is not the validated mutation API this batch assumes.** It is a bare
  replace whose interface comment still reads "API present in 1.1, exercised in 1.2"
  (`IAgentRunService.cs:167`). The validation layer is net-new. What is **not** net-new, and what this batch
  must extend rather than duplicate: `AgentRunOrchestrator.KeepDoneAsync` (`:401`) already re-ordinals a
  rewritten plan while preserving Done steps with their **original Ids**, and both replan sites (`:196`–`:198`
  and `:339`–`:341`) already drive it. Replan has been mutating a live plan safely since Phase 1; 08 is a
  second caller of that machinery, not a second copy of it.
- **The orchestrator loop already honours mid-run mutations, for free.** `AgentRunOrchestrator.cs:218`
  re-queries `NextPendingStepAsync` on every iteration (§16 R2 — never iterate a snapshot), so any mutation
  landing between two steps is picked up with no new code at all. A "nudge" injects context (D4).

## Decisions — RESOLVED by the owner, 2026-08-01

Seven decisions, taken before any code was written. Each records the consequence, because in every case the
consequence is larger than the decision.

### D1 — A pause CANCELS the in-flight step immediately

Not a park at the next step boundary. Consequences, all three of which are the real work:

- **A pause-vs-cancel discriminator is required.** Today a fired CTS means *this run is over* on every path:
  `HeadlessRunLauncher._inflight`'s per-run CTS, `ChatSession.Cts` on the live path, and the orchestrator's
  own `cts.IsCancellationRequested` checks, which settle the run `Cancelled` (e.g. `:668`–`:672`). A pause
  must unwind the dispatch **without** settling the run terminal.
- **The aborted step must go back to `Pending`.** A step left `Running` is invisible to `NextPendingStepAsync`
  and the resumed run silently drops it while the panel still shows it active. The orchestrator already
  documents this exact hazard, for a parked *child*, at `:652`–`:656`, and already has the call shape
  (`SafeSetStepStatus(sibling.Id, AgentStepStatus.Pending, …)`). Reuse the reasoning and the shape.
- **On the live path `ChatSession.Cts` is also the user's Stop button.** Pause and Stop must not collide —
  decide between a second linked CTS and a flag read before the `Cancelled` settle, and pin whichever it is
  with a test, because the failure mode is a paused run that reports itself cancelled.

### D2 — The aborted step's text is DISCARDED; its tokens are still BILLED

The step re-runs clean on resume; the ledger keeps what the provider actually charged. Consequences:

- Per-step ledger accrual happens in `RecordStepResultAsync`, which an aborted step never reaches. So the
  abort path needs a **run-level** `AddUsageAsync(runId, stepId: null, usage)` — the API already supports
  exactly that (`IAgentRunService.cs:71`, "run-level ledger when stepId is null").
- If the provider returns no usage for a cancelled stream, **bill nothing and synthesize nothing.** An
  estimated number in a ledger the plan calls "transparent" (§Q7) is worse than a missing one.
- A user who pauses repeatedly pays repeatedly. That is the accepted cost of D1 and it should be visible in
  the ledger rather than smoothed away.

### D3 — Full mutation scope: edit, insert, reorder, skip

- **Only `Pending` steps are mutable; `Done` steps are immutable** (§13.2 KeepDone). `Running` is the
  interesting case and D1 resolves it pleasantly: an immediate pause returns the current step to `Pending`,
  at which point it becomes mutable. That is a **feature** — the user pauses because a step is going wrong,
  and the thing they want to fix is that step.
- `AgentStepStatus.Skipped` (4) is already a persisted member; skip needs no enum work.
- **An edited title/intent is user free text entering the run context.** `SensitiveDebug` only, never a
  release-visible log line. And confirm it inherits `AgentVerifier`'s existing flatten-and-cap of the step
  title (`:349`): that sanitizer exists so planner/model text cannot forge a fact line in the verify prompt,
  and user text needs it for exactly the same reason.
- **Mutation versus the drain loop.** Between steps is free (the re-query). *During* a step, a
  `ReplaceStepsAsync` races `RecordStepResultAsync`. Under D1 the UI can require a pause first, which removes
  the race **by construction** — prefer that to a lock, and state it as the design rather than discovering it
  as a bug.

### D4 — A nudge is a TRANSIENT context injection, not a plan edit

The persisted plan stays a record of intended work. Consequences:

- The nudge is folded into `RunContext` for the next step, and is therefore seen by the critic and by any
  replan. Both are correct and both should be intentional.
- **It must never reach the System prompt**, for the reason Batch 05 records:
  `TokenizingAiClientService` rewrites only `ChatRole.User` text and hands the reply back *detokenized*, so
  user content placed in the System prompt ships restored PII straight past the tokenizer. Append it after
  the goal on the **user** message, exactly as Batch 05's analysis block does.
- **A nudge does not survive a resume unless it is persisted.** `RunContext` is rebuilt fresh per `RunAsync`
  (`:92`, and `:120` records why). Decide explicitly: persist it (the `ExtraJson` envelope is the additive
  precedent) or scope it to the current dispatch and say so in the UI. `SafeSeedResumeContext` (`:442`) is
  where a persisted one would be restored.
- User content: `SensitiveDebug`, and the Flow surface's Title/Body stay generic.

### D5 — EVERY run is pausable, including scheduled runs

- **The scheduled half is nearly free, contrary to the concern that raised the question.** R15's head-of-line
  block is *not* extended by a user pause: `HeadlessRunHandle.Completion` settles on a **park**
  (orchestrator `:647`–`:651` states it, and `ScheduledJobBackgroundService.cs:251` only ever reaches its
  `WaitingForInput or Paused` branch because it does), so `_runLock` is released and the next due job
  dispatches. That branch **already matches `Paused`** and already calls `AdvanceMissedRunAsync`, so the
  schedule advances too. **Pin this premise with a test before building on it** — the whole decision rests on
  it and it was read from code, not measured.
- **One consequence that is new.** Because `AdvanceMissedRunAsync` moves `NextFireAt` forward, a paused
  scheduled run and a fresh run of the same job can coexist. That is already true at a budget pause, so it is
  not this batch's defect — but a user-initiated pause makes it reachable **on demand** rather than only at a
  budget edge, which changes how likely anyone is to meet it.

### D6 — Pausing a `WaitingForChildren` parent CASCADE-PAUSES its children

The largest piece of net-new work in the batch, and the one Phase 3 created rather than answered.

- Children have no pause path today: `LaunchChildAsync` dispatches them on the separate `_childSlots` pool
  and the parent awaits their `Completion`. Each child's pause is its own D1 abort.
- The parent sits at `WaitingForChildren(8)`, not `Running`, so its pause is **not** D1's step cancel — it is
  a fan-out-wide operation: pause every child, then move the parent `WaitingForChildren → Paused`.
- The parent's un-park today is `TryEndChildWaitAsync`, a CAS **from `WaitingForChildren`**. A resume from
  `Paused` has to re-dispatch the paused children *and* re-establish the wait, so it is neither that method
  nor `TryBeginResumeAsync`. Expect a **third** claim method and pin the source state of all three.
- **The existing parked-child arm does NOT already cover this, and getting it wrong is silent-ish and bad.**
  The fan-out settle loop's parked arm is `case AgentRunState.WaitingForInput:` at `:647` — an **exact
  match**, not a set. It rolls up nothing, sets `anyParked`, and returns the sibling step to `Pending`, which
  is the behaviour a paused child wants. But a child sitting at `Paused(4)` **falls through to `default:`**
  at `:661`, which sets `anyFailed = true`, `error ??= "child run did not settle"` and calls
  `SettleSiblingAsync(…, FanOutStepResult(false, error))`. So a naive cascade-pause would record **every
  child as a failed sibling** and feed the replan loop — the user clicks Pause and the run replans around
  work it thinks failed. Pick one and write it down:
  **(a) widen `:647` to an explicit `WaitingForInput or Paused` set** — preserves the pause-versus-budget
  distinction this whole batch exists to create, costs one explicit-set edit, and is what D7 asks for
  anyway; or
  **(b) cascade-park children as `WaitingForInput`** — no orchestrator edit, at the price that a resume
  cannot tell a user-paused child from a budget-parked one, which is a distinction the parent may later want.
  (a) is the recommendation. Either way this arm is a required edit site, not a place the cascade lands for
  free.

### D7 — No new ordinal range over `AgentRunState` (derived, non-negotiable)

`AgentEnums.cs:41`–`:56` records that exactly two ordinal ranges existed, that both were converted to
explicit sets at Batch 07's G8, and that **any** range now lies because `WaitingForChildren = 8` sits above
the terminal band. A steering batch that expresses "is this run steerable" as `State < X` writes a third lie.
Use explicit sets.

## Guardrails

- Reuse the resume-once CAS + Safe* discipline; a steer must never corrupt or double-run a live loop.
- No interactive regression; off-thread safety; privacy (nudge text is user content).
- **Executor parity**: every steering path works on Live *and* Headless, or is refused on both.
- **The pause path must be demonstrated to leave a RESUMABLE run, not a `Cancelled` one, on both executors.**
  D1 makes this the single easiest thing in the batch to get wrong, and the failure is silent in the sense
  that the run does settle — just terminally.
- **No ordinal range over `AgentRunState`** (D7).
- Every new user-visible string lands in `ViewStrings.resx` **and** `.de.resx` **and** `.fr.resx`. The state
  chip needs none (`Run_State_Paused` already exists in all three); everything else does.
- Zero warnings in **both** configurations under `-t:Rebuild`, per CLAUDE.md.

## Acceptance

A user can pause/resume and safely edit the pending plan of a live run; `Paused` is a real driven state; build
green.

## What this batch will add to the Rank-1 manual round

Stated up front because `00-OVERVIEW.md` tracks it as a first-class number and this batch will **lengthen**
it. At least six items, none automatable:

_**AS SHIPPED (2026-08-01) THIS IS SEVEN, NOT SIX, AND THE NUMBERING BELOW IS THE OLD ONE.**
[`08-live-steering.impl.md`](08-live-steering.impl.md) §16 refined the list against what the grounding measured
and inserted a new item — **pause a live run that is sitting on an action card**, the W3 failure mode and the
highest-value item on the round, unautomatable because it needs a human **not** to click. Items 5 and 6 below
were reworded there (the pause precondition, and the pause → note → Continue sequence), item 3 again by F20 and
F1's fix, and item 6's string count is **25 per locale as measured at `3de2ac1`**, not the twenty §16 states.
**Read the seven as enumerated in "Opened by Batch 08" in [`00-OVERVIEW.md`](00-OVERVIEW.md)** — that is the
list a tester should work from. The six below are kept because they were written before any code and predicted
the shape correctly._

1. Pause a live interactive run mid-step; confirm it resumes and completes rather than settling cancelled.
2. Pause a **scheduled** run and confirm another due job dispatches while it sits paused — the D5 premise,
   observed rather than reasoned.
3. Pause a fan-out **parent**; confirm every child that was pausable parks, none is orphaned, and the
   Continue **supersedes the paused generation and re-dispatches the group fresh** (D6). *(Batch 08 F20: this
   item originally read "every one resumes", which the shipped D6 behaviour never does —
   `ResumingAPausedParent_SupersedesThePausedGeneration_AndDispatchesAFreshOne` pins the opposite, and
   `SafeCancelStaleChildrenAsync` cancels the old generation on the way in. Not a re-argument of D6: the
   smoke script would simply have failed against reality. F1's fix adds the "that was pausable" clause — a
   child still `Planning` at cascade time is deliberately left running rather than cancelled.)*
4. Edit, insert, reorder and skip a pending step and watch the run honour each on its next step.
5. A nudge that visibly changes the next step's behaviour — and, if D4 is scoped to the dispatch, that it is
   visibly gone after a resume.
6. DE/FR for every new string without clipping.
