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
  this gate — the user already approved the run; pausing again mid-recovery would make failure-recovery
  feel broken. This falls out naturally from the insertion point: those branches live outside the
  `!resume || rePlanAfterClarification` block.
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

`LiveTurnExecutor` overrides it `true`. The ten existing hand-written test fakes get the default (`false`)
for free — only `LiveTurnExecutor` needs a change.

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

### UI

`RunProgressViewModel.Project` (`RunProgressViewModel.cs:914-922` is the existing precedent for
`IsToolApprovalPause`) gains a parallel `IsPlanApprovalPause` bool, read the same way:

```csharp
IsPlanApprovalPause = run.State == AgentRunState.WaitingForInput
    && RunPauseEnvelope.ReadReason(run) == AgentRunOrchestrator.PlanApprovalReason;
```

- **Approve** reuses the existing `ContinueCommand` (`RunProgressViewModel.cs:1249-1289`) — no new resume
  semantics needed, this is an ordinary resume that lets the drain loop start. The button's label swaps
  to "Approve" specifically for this reason (new loc key), vs. "Continue" for every other parked reason.
- **Reject** is a **new** command + a new `IAgentRunResumeService` member. It cannot reuse `DeclineAsync`
  (that resumes the run with a tool denial recorded — wrong shape entirely) and it cannot reuse
  `ChatSession.Cancel()` (that cancels an in-flight run's CTS; a parked run has already returned from
  `RunAsync`, so there is no in-flight loop to cancel). It must CAS `WaitingForInput` (reason
  `plan-approval`) directly to `Cancelled` — no re-dispatch of `RunAsync` at all — pin the (empty) range,
  post a short "Plan rejected" note into the chat, and leave the composer free (it already is: the Live
  session was released at park time).
- The panel shows **no inline step mutation** on this card — plain binary Approve/Reject. Per your
  answer, the expected redirect path after Reject is an ordinary chat message (new `SendMessage` →
  a fresh planned run), not editing the rejected plan in place.
- No settings toggle to disable the gate — always-on above the threshold for interactive runs.

### Loc keys needed (3-resx parity — `en`/`de`/`fr`, per existing localization tests)

- A plan-approval title/lead line for the signal band (e.g. "Review this plan before it runs").
- `Run_Action_Approve` (distinct from the existing `Run_Action_Continue`).
- `Run_Action_RejectPlan` (distinct from the existing `Run_Action_Deny`, which is tool-approval-specific
  copy).
- A short "plan rejected" chat-note string.

### Explicitly out of scope (decided during brainstorming, not oversights)

- No mechanical multi-file-split detector for change 1 — prompt-only.
- No approval re-trigger on replan — first plan only.
- No settings toggle to disable the approval gate.
- No inline step editing on the approval card.
- No approval gate for headless/background runs at any step count.
