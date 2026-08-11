# Plan-step collapsing + plan-approval gate — design

**Status:** Approved by user 2026-08-11. Ready for `writing-plans`.

Two independent changes to the agent-run planning phase (`src/Pia.Wpf/Services/AgentPlanner.cs`,
`src/Pia.Wpf/Services/AgentRunOrchestrator.cs`). Both are scoped to `RunShape.Planned` runs; neither
touches the `SingleTurn` degrade path.

---

## 1. Collapse multi-file steps (prompt-only)

**Problem:** the planner sometimes emits one step per file for what is really one logical change
("update file A", "update file B", "update file C" instead of one step touching all three).

**Root cause:** confirmed there is no structured "which files does a step touch" signal anywhere in
the codebase — `AgentStep`/`PlanStepArg` carry only `Title`/`Intent`/`ExpectedArtifact`/`PersonaKey`/
`ParallelGroup` (`AgentPlanner.cs:156-169`), and the audit timeline deliberately never records paths
(`AgentTimelineEvent.cs:41-44`). This is a pure natural-language habit the model falls into, not
something a mechanical grouping pass could detect reliably from persisted data. Fix is prompt-only —
no mechanical safety net (explicitly decided against: false-positive risk, extra code, revisit only if
this recurs after the prompt fix ships).

### Changes

1. **`AgentPlanner.BuildPlanMessages`** (`AgentPlanner.cs:774-814`) — add one line to the system prompt,
   immediately after `"Keep the plan tight — only the steps genuinely needed to accomplish the goal."`:

   ```
   Group by logical change, not by file: if one reason requires editing several files, that is ONE step
   listing every file in expectedArtifact — never split it into "update file A", "update file B", "update
   file C".
   ```

2. **`AgentPlanner.BuildReplanMessages`** (`AgentPlanner.cs:816-841`) — same line, added to its system
   prompt too (right after `"Call emit_plan with the revised ordered steps (only the steps still
   needed)."`). A replan can just as easily fragment a recovery step by file as an initial plan can.

3. **`PlanStepArg.ExpectedArtifact`** description (`AgentPlanner.cs:159`) — reword to explicitly allow
   multiple artifacts, since this record is shared by both `EmitPlanTool` and `EmitRevisedPlanTool` so
   one edit covers plan and replan:

   ```
   Before: "The concrete artifact/result this step should produce"
   After:  "The concrete artifact(s)/result this step should produce — may name several files when they
            are one logical change"
   ```

No schema shape change (still `string?`), no `ValidatePlan` change, no new fields, no new state. Applies
to plan and replan uniformly.

---

## 2. Plan-approval gate (Live interactive only)

**Problem:** an agent run currently starts executing steps the instant planning finishes — for a
multi-step plan, the user never sees what's about to happen before it happens.

**Scope:** interactive (Live) runs only. Headless/background/scheduled runs have no user present to ask,
so they always skip this gate regardless of step count — confirmed against `IAgentTurnExecutor`'s own
doc comment, which already treats Live vs. Headless as a *capability* distinction resolved through the
executor instance, never through `RunProfile`/`AgentRunTrigger` (those are provenance/budget-only per
`AgentEnums.cs:16-18` and `IAgentTurnExecutor.cs:6-10`).

### Precedent this reuses

Contrary to first impression, the closest existing precedent is **not** the tool-approval park. Per
`IAgentTurnExecutor.cs:144-145`, `ApprovalRequiredTool` is *never* set "on every step of every LIVE run —
the interactive gate has a card and never parks": Live resolves tool approval inline, mid-turn, via its
own action card. The `WaitingForInput` park *is* already used on Live runs, though — for
`NeedsGoalReason`/`NeedsInputReason` clarification questions, which post+mirror into the chat
(`PostAndMirrorClarificationQuestionAsync`) and whose `OnPausedAsync` override releases the Live session
so Send/RunInBackground re-enable while parked (`AgentRunOrchestrator.cs:733-757`,
`IAgentTurnExecutor.cs:226-235`). The plan-approval gate follows *that* shape.

### Trigger

Right after the **first** successful `PlanAsync` call produces a valid plan — i.e. inside
`AgentRunOrchestrator.RunAsync`'s `if (!resume || rePlanAfterClarification)` block
(`AgentRunOrchestrator.cs:204-230`), after `SafeReplaceSteps` and before the outer drain loop starts.

- Condition: `plan.Steps.Count >= 3` (fixed threshold, no settings knob) **and** the executor supports
  plan approval (see below).
- **First plan only.** A later replan-after-failure or replan-after-failed-verify (the `TryReplanAfterFailureAsync`
  branch and the verify-fail branch, `AgentRunOrchestrator.cs:241-271` and `:502-519`) never re-triggers
  this gate — when the first plan already cleared the threshold, the user already approved the run, and
  pausing again mid-recovery would make failure-recovery feel broken. Accepted edge case: a plan that
  starts *under* 3 steps skips the gate entirely, and a later replan can still grow it past the threshold
  with no approval ever having happened — deliberately out of scope rather than overlooked, on the same
  "first plan only" reasoning taken to its edge. This falls out naturally from the insertion point: those
  branches live outside the `!resume || rePlanAfterClarification` block.
- A clarification round-trip (`NeedsGoalReason`) still counts as "the first plan" — the gate applies to
  whatever plan eventually lands in this block, however many clarification hops it took to get there.

### Live-only gate mechanism

New `IAgentTurnExecutor` default-interface member, following the exact convention this interface already
uses for `RunGraceTurnAsync`/`MirrorClarificationQuestionAsync` (default no-op/false, `LiveTurnExecutor`
overrides):

```csharp
/// Whether this executor can pause a run for a human to approve a non-trivial plan before it executes.
/// Headless has no live conversation to post the plan into, so it keeps the default and this run's
/// plans always execute unapproved, regardless of step count.
bool SupportsPlanApproval => false;
```

`LiveTurnExecutor` overrides it `true`. Every other existing `IAgentTurnExecutor` implementation
(`HeadlessTurnExecutor` plus the hand-written test fakes) gets the default (`false`) for free — only
`LiveTurnExecutor` needs a change.

### Park shape

New reason token beside the existing ones (`AgentRunOrchestrator.cs:45-66`):

```csharp
private const string PlanApprovalReason = "plan-approval";
```

Same `WaitingForInput` + `ExtraJson` envelope shape as every other park (`AgentRunService.PauseAsync`,
`RunPauseEnvelope`) — `{"paused":true,"reason":"plan-approval"}`. Steps are already persisted `Pending`
by `SafeReplaceSteps` at this point; none are dispatched. `SafePause(reason: PlanApprovalReason)` →
`SafeOnPaused(executor, run, ctx)` (same non-terminal executor release every other park uses) → post the
proposed plan into the chat and mirror it, the same call shape `PostAndMirrorClarificationQuestionAsync`
uses for a question, but posting a statement ("Proposed plan: ...") instead. This keeps the plan visible
in scrollback even after the run-progress panel's state moves on.

### Composer behavior while parked

`ChatSessionManager.ReadClarificationParkReasonAsync` (line 1017) only treats `NeedsGoalReason`/
`NeedsInputReason` as "answerable via chat text," and `RestoreActiveRunAsync` deliberately leaves
`WaitingForInput`/`Paused` out of `ForeignRunActive` so "the parked 'continue in chat' path stays open"
(lines 636-637) — i.e. nothing today blocks starting a new turn while any `WaitingForInput` park
(including a future plan-approval one) sits active. Left unaddressed, a user could start a second,
unrelated turn against the same chat while the proposed plan sits parked and unresolved — and not only via
Send: `ExecuteSendMessage`, `RegenerateCore`, and `SwitchToAgent` are three separate
`AssistantViewModel` callers that each reach `ChatSessionManager.StartTurnAsync`, and each guards its own
entry with its own local `IsStreaming`/`ForeignRunActive` check rather than a shared one — a composer-only
fix would have to be replicated (and kept in sync) across all three, and `SwitchToAgent` is the sharpest
case, since accepting an Agent-mode suggestion chip while a plan sits parked would start a **second
concurrent Planned run** on the same chat.

**Resolution: a shared pre-check flag, plus `StartTurnAsync` as the backstop.** A `StartTurnAsync`-only
guard fires too late for two of the three callers: `RegenerateCore` cancels pending action cards and
truncates `session.Messages` (removing the target message and everything after it) *before* it calls
`StartTurnAsync`, and `ExecuteSendMessage` clears `InputText`/`PendingAttachment` before calling it too —
so a bare refusal inside `StartTurnAsync` would let the transcript truncation happen (Regenerate) or eat
the typed draft (Send) and only then silently refuse, which is worse than doing nothing. So the check has
to happen before those side effects, at each caller's existing early-return site — the same site each
already checks `IsStreaming`/`ForeignRunActive` — not only inside the shared dispatch method.

Add one more shared observable (e.g. a `PlanApprovalParkActive`-style bool on the session, read the same
way `ForeignRunActive` is) and check it in all three existing early-return guards:
`CanExecuteSendMessage`, `RegenerateCore`'s pre-truncation check (`AssistantViewModel.cs:1140`), and
`SwitchToAgent`'s (`AssistantViewModel.cs:1512`) — before any of the three touches the transcript, the
input box, or dispatches. `CanExecuteSendMessage` returning false for this condition also fixes a second
gap: today it only checks `!IsStreaming && !ForeignRunActive`, so the Send button would otherwise stay
enabled throughout the park, contradicting the composer hint that tells the user not to send.

`StartTurnAsync`'s own guard (beside `TryAnswerParkedRunAsync`, line 667, checking `State ==
WaitingForInput` **and** `RunPauseEnvelope.ReadReason(run) == PlanApprovalReason` — the same two-part read
`IsPlanApprovalPause` uses) stays as the backstop for any caller reached outside these three (defense in
depth, same shape the codebase already uses elsewhere). Both checks re-read live state rather than caching
it, so both compose correctly with Reject: once Reject's CAS lands, the run is `Cancelled`, neither
condition matches any more, and the very next call through any of the three paths — including the one the
user's post-reject chat message triggers — proceeds normally.

### Flow notification path

`AgentRunNotificationSurface`'s park-publish branch (`AgentRunNotificationSurface.cs:188-223`) picks the
Flow card's action by reason: `ToolApprovalReason` → `ToolApprovalRunAction` (renders its own Approve/Deny
bar), `needsAnswerElsewhere` reasons → `OpenParkedRunAction` (routes back to the chat, where the
clarification question is visible), everything else → `ContinueRunAction` (a one-click, unconditional
`ResumeAsync` — no card, no second look). `PlanApprovalReason` must join the `needsAnswerElsewhere`
predicate, **not** fall through to the default. Falling through would mean a Flow notification's one-click
"Continue run" link silently approves a ≥3-step plan the user has not looked at — precisely the problem
this feature exists to prevent, and precisely the failure mode the existing `NeedsGoalReason`/
`NeedsInputReason` clarification parks already avoid by using `OpenParkedRunAction` instead of a bare
resume link. No new `FlowActionKind`, no new `FlowItemViewModel` branch — the plan-approval card itself
lives only in `RunProgressPanel`, matching the clarification precedent (their Flow card is "go look at the
chat", not "approve inline").

### Three obligatory switch arms/allowlist joins

`AgentRunOrchestrator.cs`'s own doc comment on `ToolApprovalReason` (lines 47-54) states the rule for
every reason token added to this vocabulary: it OBLIGES an arm in `RunProgressViewModel.DescribePause`
(`RunProgressViewModel.cs:1182-1203`) and in `AgentRunNotificationSurface.PausedBodyKey`
(`AgentRunNotificationSurface.cs:94-113`) — both fall back to budget wording ("stopped at its budget") for
any reason they don't recognize. `PlanApprovalReason` needs its own arm in both, alongside the dedicated
Approve/Reject card below — the card doesn't exempt the token from this, exactly as `ToolApprovalReason`
gets both a `DescribePause` arm *and* its own `IsToolApprovalPause` card.

A third, easy-to-miss site: `HeadlessRunLauncher.InterruptedReasonFor` (lines 151-156) only preserves
`NeedsGoalReason`/`NeedsInputReason` across an interrupted resume (a pre-dispatch failure, or
`ReParkInterruptedResumeAsync`) — every other reason, including any new one, silently collapses to the
generic `ResumeInterruptedReason`. The method's own doc comment explains why the allowlist exists:
overwriting the original reason "would break the resume's re-plan guard or its answer-persistence gate,
both of which key on the specific reason." `PlanApprovalReason` is exactly such a reason — `IsPlanApprovalPause`
and `TryRejectParkedPlanAsync`'s CAS both key on it surviving. `PlanApprovalReason` must join this
allowlist too, or a failed Approve dispatch silently downgrades to a plain one-click Continue with no
Approve/Reject affordance left — i.e. a plan running unapproved after all, through a fourth site the
`needsAnswerElsewhere` fix above does not cover.

### UI

`RunProgressViewModel.Project` (`RunProgressViewModel.cs:914-922` is the existing precedent for
`IsToolApprovalPause`) gains a parallel `IsPlanApprovalPause` bool, read the same way:

```csharp
IsPlanApprovalPause = run.State == AgentRunState.WaitingForInput
    && RunPauseEnvelope.ReadReason(run) == AgentRunOrchestrator.PlanApprovalReason;
```

- **Approve** reuses the existing `ContinueCommand` (`RunProgressViewModel.cs:1249-1266`) — no new resume
  semantics needed, this is an ordinary resume that lets the drain loop start. The button's label swaps
  to "Approve" specifically for this reason (new loc key `Run_Action_ApprovePlan` — **not**
  `Run_Action_Approve`, which already exists as the Flow surface's tool-approval label, `ViewStrings.resx`,
  value "Allow", `FlowItemViewModel.cs:129` — reusing it would either collide or silently repurpose an
  unrelated label), vs. "Continue" for every other parked reason.
- **Reject** is a **new** command + a new service member — and a genuinely new terminal-transition shape,
  not a variant of an existing one. Confirmed there is no existing precedent for "cancel an already-parked
  (already-*returned*-from-`RunAsync`) run from outside a dispatch": every existing path to `Cancelled`
  pairs `SafeFail` with `SafeEndRun` from *inside* an active `RunAsync` call (`AgentRunOrchestrator.cs`
  cancel/fail branches). Reject cannot reuse `DeclineAsync` (that resumes the run with a tool denial
  recorded — wrong shape) and cannot reuse `ChatSession.Cancel()` (that cancels an in-flight CTS; a parked
  run has no in-flight loop to cancel). It needs a new `IAgentRunService` primitive — call it
  `TryRejectParkedPlanAsync(runId)` — that CASes on **both** `State == WaitingForInput` **and**
  `RunPauseEnvelope.ReadReason(run) == PlanApprovalReason` (not state alone, so a stale Reject click can
  never race a different park type that resumed/re-parked in the meantime) directly to `Cancelled`. This
  deliberately skips `SafeEndRun`/`TurnCompleted`/`EndRunAsync` — safe only because `OnPausedAsync` already
  released the Live session to `Idle` at park time, so nothing is stranded — pins the (empty) range, posts
  a short "Plan rejected" note into the chat, and leaves the composer free (it already is). On the winning
  CAS it raises `RunChanged(Cancelled)`, mirroring every other terminal/state-changing primitive on
  `IAgentRunService` (`TryBeginResumeAsync` → `Running`, `TryPauseUserAsync` → `Paused`, etc.) — both
  `RunProgressViewModel` (repaints `Project`/clears `IsPlanApprovalPause` off this event) and
  `AgentRunNotificationSurface.OnRunChanged` (retracts the parked Flow card on `Cancelled`,
  `AgentRunNotificationSurface.cs:140-158`) depend on it firing; a silent CAS would leave both surfaces
  showing a stale Approve/Reject affordance for an already-cancelled run. The persistence write must
  **stamp `CompletedAt`**, matching every other writer of `Cancelled` (`AgentRunService.FailAsync`; the
  first of `FailInterruptedRunsAsync`'s two statements — whose *second* statement explicitly omits
  `CompletedAt` for a re-park, proving the field is a deliberate terminal-only invariant, not incidental).
  It must **not** call `MoveLedgerClock(runId, LedgerClock.CloseSegment)` — unlike `FailAsync`, which
  closes a segment that is still open, the plan-approval park already closed its segment when it parked,
  and closing it a second time would double-close. It must also **null out `ExtraJson`** on the winning
  CAS — `FailAsync`/`FailInterruptedRunsAsync` are the wrong precedent here (neither carries a pause
  envelope to clear); the apposite precedent is `TryBeginResumeAsync`/`TryResumeFromPauseAsync`, the only
  other primitives that consume a `WaitingForInput` park, both of which explicitly null `ExtraJson` on
  their winning CAS ("or a cleanly-completing resumed run would keep reporting itself paused" —
  `HeadlessRunLauncher.cs:693-695` states the same convention independently). Skipping this would leave
  the stale `{"paused":true,"reason":"plan-approval"}` envelope sitting on the `Cancelled` row —
  harmless in practice, since every reader (including the `StartTurnAsync` guard above) gates on
  `State == WaitingForInput` first and `State` is now `Cancelled`, but it is the established convention
  for exactly this kind of transition and leaving it in place would make this the one writer that doesn't
  follow it.
- The panel shows **no inline step mutation** on this card — plain binary Approve/Reject. Per your
  answer, the expected redirect path after Reject is an ordinary chat message (new `SendMessage` →
  a fresh planned run), not editing the rejected plan in place.
- **The existing steering-note box (Region D) must be suppressed for this card.** It's gated on
  `ShowContinueButton => IsResumableState` (`State is WaitingForInput or Paused`) — a strict superset of
  `IsPlanApprovalPause` — so without an explicit exclusion it renders here too, with copy written for
  mid-run steering ("note for the rest of this run") that is nonsensical before any step has executed.
  Worse, `Continue()` (which Approve reuses) passes whatever text sits there straight into
  `ResumeAsync(_runId, NudgeText)`, which would let Approve silently ship a nudge — contradicting the
  plain-binary decision above. Add `&& !IsPlanApprovalPause` to Region D's visibility condition.
- No settings toggle to disable the gate — always-on above the threshold for interactive runs.

### Loc keys needed (3-resx parity — `en`/`de`/`fr`, per existing localization tests)

- A plan-approval title/lead line for the signal band (e.g. "Review this plan before it runs").
- `Run_Action_ApprovePlan` — distinct from both the existing `Run_Action_Continue` *and* the existing
  `Run_Action_Approve` (already taken: the Flow surface's tool-approval label, value "Allow",
  `FlowItemViewModel.cs:129`).
- `Run_Action_RejectPlan` (distinct from the existing `Run_Action_Deny`, which is tool-approval-specific
  copy).
- A short "plan rejected" chat-note string.
- A composer-hint string for the `StartTurnAsync` block above (e.g. "Approve or reject the proposed plan
  before sending another message") — **not** a reuse of `Assistant_BackgroundRunActive_Hint`, whose text
  ("A background run is writing to this chat. Sending resumes when it finishes.") is factually wrong here
  (nothing is writing; Send never auto-resumes, only Approve does) and whose `TextBlock` binds
  `Visibility` to `ForeignRunActive` alone (`AssistantView.xaml:574`), which stays false throughout a
  plan-approval park. Needs its own loc key and its own visibility binding (keyed on the same
  `IsPlanApprovalPause`-style condition), not a shared control.

Mirroring `CanDeclineTool`/`ShowDenyButton` (`RunProgressViewModel.cs:148,160`), the panel also needs a
`CanRejectPlan`/`ShowRejectPlanButton` gating pair alongside `IsPlanApprovalPause` (Approve reuses the
existing `CanContinue` gate, since it reuses `ContinueCommand`) — the same in-flight/double-click guard
shape `IsResuming` already gives every other parked-state button.

### Explicitly out of scope (decided during brainstorming, not oversights)

- No mechanical multi-file-split detector for change 1 — prompt-only.
- No approval re-trigger on replan — first plan only.
- No settings toggle to disable the approval gate.
- No inline step editing on the approval card.
- No approval gate for headless/background runs at any step count.
