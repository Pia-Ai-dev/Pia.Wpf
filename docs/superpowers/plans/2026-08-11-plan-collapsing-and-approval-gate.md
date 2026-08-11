# Plan-step collapsing + plan-approval gate — Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop the agent planner from splitting one logical multi-file change into one step per file, and add a Live-only pause that shows the user a ≥3-step initial plan and requires Approve/Reject before any step executes.

**Architecture:** Change 1 is a pure prompt/schema-description edit to `AgentPlanner`. Change 2 reuses the existing `WaitingForInput`-park machinery (the same shape `NeedsGoalReason`/`NeedsInputReason` already use on Live runs) rather than the tool-approval park, adds one new `IAgentTurnExecutor` capability flag, one new reason token, one new persistence CAS primitive for Reject (a genuinely new terminal-transition shape — no re-dispatch), and touches every "obligatory switch arm" site the codebase's own comments say a new pause reason must join (panel wording, Flow wording, Flow routing, interrupted-resume allowlist) plus a composer-side guard that must fire before three different ViewModel callers do destructive work, not only inside the shared dispatch method.

**Tech Stack:** C#/.NET (net10.0-windows), WPF + CommunityToolkit.Mvvm, xunit v3, SQLite (ADO.NET, hand-rolled, no ORM), 3-resx localization (en/de/fr).

**Spec:** `docs/superpowers/specs/2026-08-11-plan-collapsing-and-approval-gate-design.md` — read in full before starting; it went through 5 rounds of adversarial review and every "must also touch X because of invariant Y" call-out below traces back to a specific paragraph there.

---

## Chunk 1: Collapse multi-file steps (prompt-only)

### Task 1.1: Add the "group by file" rule to the plan prompt + reword the artifact description

**Files:**
- Modify: `src/Pia.Wpf/Services/AgentPlanner.cs:774-814` (`BuildPlanMessages`)
- Modify: `src/Pia.Wpf/Services/AgentPlanner.cs:159` (`PlanStepArg.ExpectedArtifact` description)
- Test: `tests/Pia.Wpf.Tests/Services/AgentPlannerTests.cs` (create the file if it does not already exist — grep first: `Grep -l "class AgentPlannerTests" tests/Pia.Wpf.Tests/Services/*.cs` to confirm)

- [ ] **Step 1: Confirm the current exact text of the two edit sites**

Read `src/Pia.Wpf/Services/AgentPlanner.cs` lines 774-814 and line 159 to confirm they still match the spec's citations before editing (a stale line number would silently edit the wrong line). Expected content at line 159 today:

```csharp
[property: Description("The concrete artifact/result this step should produce")] string? ExpectedArtifact = null,
```

Expected content inside `BuildPlanMessages`'s system-prompt builder (around line 783):

```csharp
sb.AppendLine("Keep the plan tight — only the steps genuinely needed to accomplish the goal.");
```

- [ ] **Step 2: Add the "group by file" rule to `BuildPlanMessages`**

In `AgentPlanner.cs`, immediately after the `"Keep the plan tight..."` line inside `BuildPlanMessages`, add:

```csharp
sb.AppendLine("Group by logical change, not by file: if one reason requires editing several files, that is ONE step listing every file in expectedArtifact — never split it into \"update file A\", \"update file B\", \"update file C\".");
```

- [ ] **Step 3: Reword `ExpectedArtifact`'s description**

Change line 159 from:

```csharp
[property: Description("The concrete artifact/result this step should produce")] string? ExpectedArtifact = null,
```

to:

```csharp
[property: Description("The concrete artifact(s)/result this step should produce — may name several files when they are one logical change")] string? ExpectedArtifact = null,
```

This one edit affects both `EmitPlanTool` and `EmitRevisedPlanTool` (both use `PlanStepArg[]`), so Task 1.2 does not need to repeat it.

- [ ] **Step 4: Write a failing test asserting the new prompt text is present**

`tests/Pia.Wpf.Tests/Services/AgentPlannerTests.cs` already exists (it is a large, ~1000-line file) — do NOT reflect into the private static `BuildPlanMessages`. The file already has a purpose-built pattern for exactly this assertion: it drives a real `PlanAsync` call through a fake `IAiClientService` that captures the system prompt it was sent (see the file's existing `LastPrompt`/`LastUserPrompt`-style captures and its `ReturnsPlan(...)` helper, used by tests like the ones around `ReplanAsync_DeclineMember_IsNotHonoured_...`). Grep the file for `LastPrompt` and `ReturnsPlan` to find the exact fixture shape before writing this, then add:

```csharp
[Fact]
public async Task PlanAsync_SystemPromptIncludesGroupByFileRule()
{
    ReturnsPlan(new[] { ("Step 1", "Do the first thing") }); // or this file's exact equivalent setup

    await BuildPlanner().PlanAsync("goal", ctx, persona, provider, CancellationToken.None);

    Assert.Contains("Group by logical change, not by file", LastPrompt);
}
```

Match the exact fixture/helper names this file already uses (`BuildPlanner()`, `ReturnsPlan(...)`, `LastPrompt`, `ctx`/`persona`/`provider` construction) rather than the illustrative names above — read the file first and mirror an existing test in the same style.

- [ ] **Step 5: Run the test, confirm it fails before the edit / passes after**

This repo's test runner is MTP-based (`Microsoft.Testing.Platform`), not classic VSTest — the `dotnet test --filter "FullyQualifiedName~..."` form does NOT work here (confirmed: it prints `Zero tests ran` and exits with an error). Use the MTP native filter form instead:

```bash
dotnet test -- --filter-method "*PlanAsync_SystemPromptIncludesGroupByFileRule*"
```

Run this once BEFORE Step 2's edit (expect FAIL — text not present) and once AFTER (expect PASS). If this filter form also fails to isolate the test in practice, fall back to the full gate (`dotnet test`, no filter) to confirm red→green — never trust a "0 tests ran" result as a pass.

- [ ] **Step 6: Commit**

```bash
git add src/Pia.Wpf/Services/AgentPlanner.cs tests/Pia.Wpf.Tests/Services/AgentPlannerTests.cs
git commit -m "Stop the planner splitting one logical change into one step per file"
```

### Task 1.2: Add the same rule to the replan prompt

**Files:**
- Modify: `src/Pia.Wpf/Services/AgentPlanner.cs:816-841` (`BuildReplanMessages`)
- Test: same file as Task 1.1

- [ ] **Step 1: Confirm current text**

Expected content inside `BuildReplanMessages` (around line 825):

```csharp
sb.AppendLine("Call emit_plan with the revised ordered steps (only the steps still needed).");
```

- [ ] **Step 2: Add the rule**

Immediately after that line, add the identical rule text from Task 1.1 Step 2:

```csharp
sb.AppendLine("Group by logical change, not by file: if one reason requires editing several files, that is ONE step listing every file in expectedArtifact — never split it into \"update file A\", \"update file B\", \"update file C\".");
```

- [ ] **Step 3: Write a failing test, mirroring Task 1.1's real (non-reflection) pattern**

Same fixture shape as Task 1.1 Step 4 — drive a real `ReplanAsync` call through the existing fake `IAiClientService` and assert on its captured prompt, not reflection into the private `BuildReplanMessages`:

```csharp
[Fact]
public async Task ReplanAsync_SystemPromptIncludesGroupByFileRule()
{
    ReturnsPlan(new[] { ("Step 1", "Do the first thing") }); // or this file's exact equivalent setup

    await BuildPlanner().ReplanAsync(ctx, failure: "something failed", persona, provider, CancellationToken.None);

    Assert.Contains("Group by logical change, not by file", LastPrompt);
}
```

Match the exact fixture/helper names this file already uses, mirroring an existing `ReplanAsync` test in the same file rather than the illustrative names above.

- [ ] **Step 4: Run, confirm fail → pass**

```bash
dotnet test -- --filter-method "*ReplanAsync_SystemPromptIncludesGroupByFileRule*"
```

(Same MTP filter-syntax note as Task 1.1 Step 5 — `--filter "FullyQualifiedName~..."` does not work in this repo's runner.)

- [ ] **Step 5: Run the full gate**

```bash
dotnet test
```

Expected: `failed: 0` (per `CLAUDE.md`'s Test Gate — no filter, no live-provider opt-in needed for this change).

- [ ] **Step 6: Commit**

```bash
git add src/Pia.Wpf/Services/AgentPlanner.cs tests/Pia.Wpf.Tests/Services/AgentPlannerTests.cs
git commit -m "Apply the group-by-file rule to the replan prompt too"
```

---

## Chunk 2: Live-only gate — core plumbing

### Task 2.1: Add the `PlanApprovalReason` constant

**Files:**
- Modify: `src/Pia.Wpf/Services/AgentRunOrchestrator.cs:38-70` (reason-token constants block)

- [ ] **Step 1: Add the constant beside its siblings**

In `AgentRunOrchestrator.cs`, immediately after the `UnverifiedTruncationReason` constant (line 70), add:

```csharp
    /// <summary>
    /// Pause reason when the FIRST plan of a run has 3 or more steps and the executor supports plan
    /// approval (<see cref="IAgentTurnExecutor.SupportsPlanApproval"/>): park before any step runs so a
    /// human can Approve or Reject the plan as shown. Never re-triggers on a replan — only the block that
    /// produces the run's first plan checks this reason (§ design doc "Trigger"). A named constant for the
    /// same reason every other token here is one: adding it OBLIGES an arm in
    /// <c>RunProgressViewModel.DescribePause</c>, in <c>AgentRunNotificationSurface.PausedBodyKey</c>, in
    /// <c>AgentRunNotificationSurface</c>'s <c>needsAnswerElsewhere</c> predicate, and in
    /// <c>HeadlessRunLauncher.InterruptedReasonFor</c>'s allowlist — four sites, not the usual two, because
    /// this reason (unlike <see cref="ToolApprovalReason"/>) needs the SAME "route back to chat, don't
    /// one-click-resume" Flow treatment <see cref="NeedsGoalReason"/>/<see cref="NeedsInputReason"/> get.
    /// </summary>
    internal const string PlanApprovalReason = "plan-approval";
```

- [ ] **Step 2: Build to confirm no syntax error**

```bash
dotnet build src/Pia.Wpf/Pia.Wpf.csproj
```

Expected: `0 Error(s)`. (No test yet — this constant has no behavior until Task 2.3 wires it in; a bare unused-`internal const` does not warn in this project's configuration, but confirm the build is clean before moving on.)

- [ ] **Step 3: Commit**

```bash
git add src/Pia.Wpf/Services/AgentRunOrchestrator.cs
git commit -m "Add the plan-approval pause reason token"
```

### Task 2.2: Add `IAgentTurnExecutor.SupportsPlanApproval` and the `LiveTurnExecutor` override

**Files:**
- Modify: `src/Pia.Wpf/Services/Interfaces/IAgentTurnExecutor.cs:207-277` (interface)
- Modify: `src/Pia.Wpf/ViewModels/Models/LiveTurnExecutor.cs` (override)
- Test: `tests/Pia.Wpf.Tests/Services/AgentRunOrchestratorArmTests.cs` (or wherever a minimal fake `IAgentTurnExecutor` already exists — reuse `StubExecutor` at `AgentRunOrchestratorArmTests.cs:203` if it fits, rather than writing a new fake)

- [ ] **Step 1: Add the interface member**

In `IAgentTurnExecutor.cs`, add this member to the interface, placed after `RunGraceTurnAsync` (which is the interface's own precedent for a defaulted, non-authority-bearing member — see its doc comment at lines 249-256):

```csharp
    /// <summary>
    /// Whether this executor can pause a run for a human to approve a non-trivial plan before it executes.
    /// Headless has no live conversation to post the plan into, so it keeps the default and a headless
    /// run's plans always execute unapproved, regardless of step count.
    /// </summary>
    bool SupportsPlanApproval => false;
```

- [ ] **Step 2: Fix the stale "twelve implementers" doc comment while touching this file**

The doc comment on `RunGraceTurnAsync` (around `IAgentTurnExecutor.cs:249-256`) currently reads "twelve types implement this interface (two production, ten hand-written test fakes)". The actual current count is 18 (2 production + 16 test fakes) — confirmed by grep across `src/` and `tests/`. Update the comment's numbers to match (twelve → eighteen, ten → sixteen) so the next reader of this file is not misled by a doc comment this batch is already touching.

- [ ] **Step 3: Override in `LiveTurnExecutor`**

In `src/Pia.Wpf/ViewModels/Models/LiveTurnExecutor.cs`, add near the top of the class body (after the constructor, before `BeginRunAsync`, mirroring where a simple interface member belongs relative to the existing `MirrorClarificationQuestionAsync` override further down):

```csharp
    /// <summary>Live has a chat to post the proposed plan into and a panel to show the Approve/Reject card in.</summary>
    public bool SupportsPlanApproval => true;
```

- [ ] **Step 4: Confirm `HeadlessTurnExecutor` needs no change**

Open `src/Pia.Wpf/Services/HeadlessTurnExecutor.cs` and confirm it does NOT already declare a `SupportsPlanApproval` member (it shouldn't — grep first). Leave it untouched; it inherits the interface default (`false`).

- [ ] **Step 5: Add a settable `SupportsPlanApproval` to `StubExecutor`, and a test asserting the default**

Task 2.3 needs an `IAgentTurnExecutor` fake it can flip `SupportsPlanApproval` on for, and none of the 16 existing test fakes in the suite expose one today (they all silently take the interface default). In `AgentRunOrchestratorArmTests.cs`, add a settable auto-property to `StubExecutor` (line 203), mirroring its existing `ApprovalRequiredTool { get; init; }`/`UserInputQuestion { get; init; }` pattern, defaulted `false` so every other test using this fake is unaffected:

```csharp
    public bool SupportsPlanApproval { get; init; }
```

Then add:

```csharp
[Fact]
public void SupportsPlanApproval_DefaultsFalseForAnExecutorThatDoesNotOverrideIt()
{
    IAgentTurnExecutor executor = new StubExecutor();
    Assert.False(executor.SupportsPlanApproval);
}
```

- [ ] **Step 6: Write a test asserting `LiveTurnExecutor` overrides it true**

Find `LiveTurnExecutor`'s existing test construction (grep `new LiveTurnExecutor(` under `tests/Pia.Wpf.Tests/`) and add, in the nearest matching test file:

```csharp
[Fact]
public void LiveTurnExecutor_SupportsPlanApproval()
{
    var live = /* construct using the same helper/pattern an existing LiveTurnExecutor test in this file already uses */;
    Assert.True(live.SupportsPlanApproval);
}
```

- [ ] **Step 7: Run both new tests, confirm pass**

```bash
dotnet test -- --filter-method "*SupportsPlanApproval*"
```

- [ ] **Step 8: Commit**

```bash
git add src/Pia.Wpf/Services/Interfaces/IAgentTurnExecutor.cs src/Pia.Wpf/ViewModels/Models/LiveTurnExecutor.cs tests/Pia.Wpf.Tests/Services/AgentRunOrchestratorArmTests.cs
git commit -m "Add IAgentTurnExecutor.SupportsPlanApproval; Live overrides true"
```

### Task 2.3: Add `ParkForPlanApprovalAsync` and wire the gate into `RunAsync`

**Files:**
- Modify: `src/Pia.Wpf/Services/AgentRunOrchestrator.cs` (new private method + `RunAsync` insertion)
- Test: `tests/Pia.Wpf.Tests/Services/AgentRunOrchestratorTests.cs` (or `AgentRunOrchestratorArmTests.cs` — whichever already tests `ParkForUngroundableGoalAsync`/`ParkForUserInputAsync`-style behavior; grep first)

- [ ] **Step 1: Add the new park method**

`ParkForPlanApprovalAsync` parks with NO step in flight (the gate fires right after `SafeReplaceSteps`, before any step runs), so it follows `ParkForUngroundableGoalAsync`'s shape (no `PinRangeAsync`/`ReturnStepToPendingAsync` — nothing has run yet), not `ParkForUserInputAsync`'s. Add this new private method near `ParkForUngroundableGoalAsync` (around line 603, right after it):

```csharp
    /// <summary>
    /// The run's FIRST plan has 3+ steps and the executor can show it to a human: park before any step
    /// runs, exactly the same non-terminal <see cref="AgentRunState.WaitingForInput"/> shape every other
    /// park in this file uses. No PinRange/step handling — nothing has run yet, mirroring
    /// <see cref="ParkForUngroundableGoalAsync"/>. Posts the plan as a STATEMENT (not a question) into the
    /// run's own chat via the same durable-post primitive <see cref="PostAndMirrorClarificationQuestionAsync"/>
    /// uses, so it survives in scrollback after the panel's Approve/Reject card is gone.
    /// </summary>
    private async Task ParkForPlanApprovalAsync(
        IAgentTurnExecutor executor, AgentRun run, RunContext ctx, Persona persona,
        IReadOnlyList<AgentStep> steps, CancellationToken ct)
    {
        _logger.LogInformation(
            "Run {RunId}: first plan has {StepCount} step(s) → parking {Reason} for approval",
            run.Id, steps.Count, PlanApprovalReason);

        await SafePause(run.Id, ct, reason: PlanApprovalReason).ConfigureAwait(false);
        // Non-terminal executor release, same as every other park — clears IsStreaming so Send/RunInBackground
        // re-enable while the run sits parked (the composer-level guard added in Chunk 6 then re-blocks it for
        // THIS specific reason).
        await SafeOnPaused(executor, run, ctx).ConfigureAwait(false);

        // The plan's step titles are the model's own (already validated, non-empty, non-duplicate) text —
        // safe to compose into a chat message the same way a clarification question is. Built here rather
        // than resolved from a loc key: the list of titles is per-run content, not fixed UI copy.
        var summary = BuildPlanSummaryText(steps);
        await PostAndMirrorClarificationQuestionAsync(executor, run, ctx, persona, summary).ConfigureAwait(false);
    }

    /// <summary>Renders the proposed plan's step titles as a chat-postable statement. Public copy only — no
    /// intent/expectedArtifact text, which can be longer and is not what a person skimming needs to decide
    /// Approve vs. Reject.</summary>
    private static string BuildPlanSummaryText(IReadOnlyList<AgentStep> steps)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Proposed plan — review the steps below, then Approve or Reject in the run panel:");
        foreach (var step in steps)
            sb.AppendLine($"{step.Ordinal + 1}. {step.Title}");
        return sb.ToString().TrimEnd();
    }
```

Add `using System.Text;` at the top of the file if it is not already present (check first — the file already uses `JsonSerializer` etc., so `System.Text.Json` is present; `System.Text` for `StringBuilder` may or may not already be implicitly available via a global usings file — check `src/Pia.Wpf/GlobalUsings.cs` or similar before adding a duplicate).

- [ ] **Step 2: Wire the gate into `RunAsync`**

The exact current text at `AgentRunOrchestrator.cs` (from the block ending in `await SafeReplaceSteps(...)` through `var replans = 0;`) is:

```csharp
                await SafeReplaceSteps(run.Id, plan.Steps, cts.Token).ConfigureAwait(false);
            }
            // Resume: TryBeginResumeAsync already CAS'd State→Running; the drain loop re-sets Running per
            // step. The persisted Pending remainder drives the loop — no re-plan, no step wipe.

            var replans = 0;
```

Change it to:

```csharp
                await SafeReplaceSteps(run.Id, plan.Steps, cts.Token).ConfigureAwait(false);

                // The plan-approval gate: ONLY the block that produces the run's first plan reaches here
                // (a later replan-after-failure lives in TryReplanAfterFailureAsync/the verify-fail branch,
                // both outside this `if`), and only when this is not a resume-without-replan (that path never
                // enters this `if` at all — see its own comment below). `!resume` covers a brand-new run;
                // `rePlanAfterClarification` covers a NeedsGoalReason round-trip that still counts as "the
                // first plan" per the design doc.
                if (plan.Steps.Count >= 3 && executor.SupportsPlanApproval)
                {
                    await ParkForPlanApprovalAsync(executor, run, ctx, persona, plan.Steps, cts.Token).ConfigureAwait(false);
                    return;
                }
            }
            // Resume: TryBeginResumeAsync already CAS'd State→Running; the drain loop re-sets Running per
            // step. The persisted Pending remainder drives the loop — no re-plan, no step wipe.

            var replans = 0;
```

This chunk uses `AgentRunOrchestratorArmTests.cs`'s REAL existing fixtures throughout — `Plan(params string[] titles)` (line 181, one step per title, `Intent = "do " + title`), the `RunAsync(run, planner, ct, executor, profile, steering)` helper (line 167), `StubPlanner(PlanResult plan)` (line 188), and `StubExecutor` (line 203) — not the fabricated `PlanStepArg`/`BuildStepsFromArgs` from an earlier draft of this plan. `StubExecutor` needs two more capabilities added for this chunk's tests (Step 3 below covers both); do this BEFORE writing Steps 4-7's tests, since they depend on it.

- [ ] **Step 3: Extend `StubExecutor` and `StubPlanner` for this chunk's tests**

`StubExecutor` (line 203) already has settable `ApprovalRequiredTool`/`UserInputQuestion`/`SupportsPlanApproval` (the last one added in Task 2.2 Step 5). Add one more settable knob so a test can make the first step fail (needed for Step 7's replan test):

```csharp
    public bool FailFirstStep { get; init; }
```

and change `ExecuteStepAsync`'s body to:

```csharp
    public Task<StepTurnResult> ExecuteStepAsync(AgentRun run, AgentStep step, RunContext ctx, CancellationToken ct)
    {
        StepTurns++;
        var failThisOne = FailFirstStep && StepTurns == 1;
        return Task.FromResult(new StepTurnResult(
            Succeeded: !failThisOne, Cancelled: false, Error: failThisOne ? "boom" : null, VisibleText: "done", Usage: null,
            FirstMessageId: Guid.NewGuid(), LastMessageId: Guid.NewGuid(),
            ApprovalRequiredTool: ApprovalRequiredTool, UserInputQuestion: UserInputQuestion));
    }
```

`StubPlanner` (line 188) always returns `PlanResult.Fallback` from `ReplanAsync` — Step 7 needs it to return a real 3-step replan instead. Add a second, trailing-defaulted constructor parameter (every existing call site passes only `plan`, so this stays non-breaking):

```csharp
    private sealed class StubPlanner(PlanResult plan, PlanResult? replan = null) : IAgentPlanner
    {
        public Task<PlanResult> PlanAsync(string goal, RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct)
            => Task.FromResult(plan);

        public Task<PlanResult> ReplanAsync(RunContext ctx, string? failure, Persona persona, AiProvider provider, CancellationToken ct)
            => Task.FromResult(replan ?? PlanResult.Fallback);
    }
```

- [ ] **Step 4: Write a failing test for the gate firing**

```csharp
[Fact]
public async Task AFirstPlanOfThreeOrMoreSteps_ParksForApproval_WhenTheExecutorSupportsIt()
{
    var ct = TestContext.Current.CancellationToken;
    var run = await NewRunAsync(ct);
    var executor = new StubExecutor { SupportsPlanApproval = true };

    await RunAsync(run, new StubPlanner(Plan("A", "B", "C")), ct, executor);

    var settled = (await _runs.GetAsync(run.Id, ct))!;
    Assert.Equal(AgentRunState.WaitingForInput, settled.State);
    Assert.Equal(AgentRunOrchestrator.PlanApprovalReason, RunPauseEnvelope.ReadReason(settled));
    // Nothing ran — the park fires before the drain loop's first iteration.
    Assert.Equal(0, executor.StepTurns);
}
```

- [ ] **Step 5: Write a failing test for the gate NOT firing below threshold**

```csharp
[Fact]
public async Task AFirstPlanOfTwoSteps_DoesNotParkForApproval_EvenWhenTheExecutorSupportsIt()
{
    var ct = TestContext.Current.CancellationToken;
    var run = await NewRunAsync(ct);
    var executor = new StubExecutor { SupportsPlanApproval = true };

    await RunAsync(run, new StubPlanner(Plan("A", "B")), ct, executor);

    var settled = (await _runs.GetAsync(run.Id, ct))!;
    Assert.NotEqual(AgentRunOrchestrator.PlanApprovalReason, RunPauseEnvelope.ReadReason(settled));
    Assert.Equal(AgentRunState.Completed, settled.State);
}
```

- [ ] **Step 6: Write a failing test for the gate NOT firing when the executor doesn't support it**

```csharp
[Fact]
public async Task AFirstPlanOfThreeSteps_DoesNotParkForApproval_WhenTheExecutorDoesNotSupportIt()
{
    var ct = TestContext.Current.CancellationToken;
    var run = await NewRunAsync(ct);
    var executor = new StubExecutor(); // SupportsPlanApproval defaults false — the headless shape

    await RunAsync(run, new StubPlanner(Plan("A", "B", "C")), ct, executor);

    var settled = (await _runs.GetAsync(run.Id, ct))!;
    Assert.NotEqual(AgentRunOrchestrator.PlanApprovalReason, RunPauseEnvelope.ReadReason(settled));
    Assert.Equal(AgentRunState.Completed, settled.State);
}
```

- [ ] **Step 7: Write a failing test for the gate NOT re-firing on a replan-after-failure**

This is the most important regression test — it locks in "first plan only", using an executor that DOES support plan approval throughout, so the only thing that can explain a missing park on the replan is the "first plan only" gating logic itself (not a executor-capability confound, which Step 6 already covers separately):

```csharp
[Fact]
public async Task AReplanAfterAStepFailure_NeverParksForApproval_EvenThoughItHasThreeSteps()
{
    var ct = TestContext.Current.CancellationToken;
    var run = await NewRunAsync(ct);
    var executor = new StubExecutor { SupportsPlanApproval = true, FailFirstStep = true };
    // First plan has only 1 step (stays under the gate's own threshold on its own, so this test isolates
    // the REPLAN path) and fails; the replan comes back with 3 fresh steps.
    var planner = new StubPlanner(Plan("A"), replan: Plan("D", "E", "F"));

    await RunAsync(run, planner, ct, executor);

    var settled = (await _runs.GetAsync(run.Id, ct))!;
    Assert.NotEqual(AgentRunOrchestrator.PlanApprovalReason, RunPauseEnvelope.ReadReason(settled));
    // The failed first step retried inside the replanned steps and this time succeeded (StubExecutor
    // always succeeds after FailFirstStep's one failure), so the run drains straight through.
    Assert.Equal(AgentRunState.Completed, settled.State);
}
```

- [ ] **Step 8: Run all four new tests, confirm each fails before the Step 2 wiring and passes after**

```bash
dotnet test -- --filter-method "*ParksForApproval*" --filter-method "*DoesNotParkForApproval*" --filter-method "*NeverParksForApproval*"
```

If chaining multiple `--filter-method` flags this way is rejected by the runner, run them one at a time instead — do not fall back to the unverified `--filter "FullyQualifiedName~..."` form (see Chunk 1's note on why that form does not work in this repo).

- [ ] **Step 9: Run the full gate**

```bash
dotnet test
```

Expected: `failed: 0`.

- [ ] **Step 10: Commit**

```bash
git add src/Pia.Wpf/Services/AgentRunOrchestrator.cs tests/Pia.Wpf.Tests/Services/AgentRunOrchestratorArmTests.cs
git commit -m "Park a Live run's first 3+-step plan for user approval before executing it"
```

---

## Chunk 3: Obligatory switch arms + Flow routing

### Task 3.1: Add the three new loc keys this chunk needs

**Files:**
- Modify: `src/Pia.Wpf/Resources/Strings/ViewStrings.resx`
- Modify: `src/Pia.Wpf/Resources/Strings/ViewStrings.de.resx`
- Modify: `src/Pia.Wpf/Resources/Strings/ViewStrings.fr.resx`

- [ ] **Step 1: Add `Run_Activity_PlanApproval` (panel activity line) to all three resx files**

Following the exact `<data name="..." xml:space="preserve"><value>...</value></data>` shape already used for `Run_Activity_NeedsInput` (cited in the research above), add to `ViewStrings.resx` near the other `Run_Activity_*` keys:

```xml
<data name="Run_Activity_PlanApproval" xml:space="preserve"><value>Waiting for you to approve the plan</value></data>
```

`ViewStrings.de.resx`:
```xml
<data name="Run_Activity_PlanApproval" xml:space="preserve"><value>Wartet auf Ihre Freigabe des Plans</value></data>
```

`ViewStrings.fr.resx`:
```xml
<data name="Run_Activity_PlanApproval" xml:space="preserve"><value>En attente de votre approbation du plan</value></data>
```

- [ ] **Step 2: Add `Flow_Run_PlanApproval` (Flow card body key) to all three resx files**

Following `Flow_Run_NeedsInput`'s shape:

`ViewStrings.resx`:
```xml
<data name="Flow_Run_PlanApproval" xml:space="preserve"><value>A run is waiting for you to approve its plan</value></data>
```

`ViewStrings.de.resx`:
```xml
<data name="Flow_Run_PlanApproval" xml:space="preserve"><value>Eine Ausführung wartet auf Ihre Freigabe ihres Plans</value></data>
```

`ViewStrings.fr.resx`:
```xml
<data name="Flow_Run_PlanApproval" xml:space="preserve"><value>Une exécution attend votre approbation de son plan</value></data>
```

- [ ] **Step 3: Regenerate `Designer.cs` — MANUAL STEP, `dotnet build` will not do this**

Per this project's convention, `ViewStrings.Designer.cs` is generated by the `PublicResXFileCodeGenerator` Visual Studio single-file generator, NOT by any MSBuild target — `dotnet build`/`dotnet test` will silently NOT regenerate it. Open `ViewStrings.resx` in Visual Studio and save it (this re-invokes the custom tool), or right-click it in Solution Explorer → "Run Custom Tool". Confirm afterward that `ViewStrings.Designer.cs` now contains `Run_Activity_PlanApproval`/`Flow_Run_PlanApproval` properties (grep the generated file to confirm before moving on — do NOT hand-add these properties yourself; that is exactly the drift the project's memory of this file warns against). This step requires Visual Studio's GUI — there is no CLI/MSBuild fallback anywhere in this repo. An agentic worker running headless cannot perform it and should hand it off to a human at this point, resuming once the regenerated `Designer.cs` is confirmed in place.

- [ ] **Step 4: Run the localization parity test**

```bash
dotnet test -- --filter-method "*AllTranslations_MustBeComplete*"
```

Expected: PASS (all three resx files now have matching keys). If it fails, the most likely cause is a typo in one of the three files' key name — diff them.

- [ ] **Step 5: Commit**

```bash
git add src/Pia.Wpf/Resources/Strings/ViewStrings.resx src/Pia.Wpf/Resources/Strings/ViewStrings.de.resx src/Pia.Wpf/Resources/Strings/ViewStrings.fr.resx src/Pia.Wpf/Resources/Strings/ViewStrings.Designer.cs
git commit -m "Add plan-approval panel and Flow-card loc keys"
```

### Task 3.2: `RunProgressViewModel.DescribePause` arm

**Files:**
- Modify: `src/Pia.Wpf/ViewModels/RunProgressViewModel.cs:1182-1203`
- Test: find the existing test file that covers `DescribePause` (grep `DescribePause` under `tests/Pia.Wpf.Tests/ViewModels/`)

- [ ] **Step 1: Add the arm**

The current switch (exact text confirmed above) is:

```csharp
    private string DescribePause(AgentRun run) => RunPauseEnvelope.ReadReason(run) switch
    {
        AgentRunOrchestrator.ToolApprovalReason =>
            _localization.Format("Run_Activity_WaitingForToolApproval", RunPauseEnvelope.ReadApprovalTool(run) ?? string.Empty),
        AgentRunOrchestrator.ChildrenParkedReason => _localization["Run_Activity_ChildrenParked"],
        AgentRunService.ChildrenInterruptedReason => _localization["Run_Activity_ChildrenInterrupted"],
        AgentRunOrchestrator.NeedsGoalReason => _localization["Run_Activity_NeedsGoal"],
        AgentRunOrchestrator.NeedsInputReason => _localization["Run_Activity_NeedsInput"],
        AgentRunService.UserPausedReason => _localization["Run_Activity_UserPaused"],
        HeadlessRunLauncher.ResumeInterruptedReason => _localization["Run_Activity_ResumeInterrupted"],
        _ => _localization["Run_Activity_WaitingAtBudget"],
    };
```

Add a new arm for `PlanApprovalReason`, placed beside `NeedsGoalReason`/`NeedsInputReason` (same "the card is the real UI, this line is secondary" category):

```csharp
        AgentRunOrchestrator.PlanApprovalReason => _localization["Run_Activity_PlanApproval"],
```

- [ ] **Step 2: Add an `InlineData` row to the existing theory, rather than a new bespoke test**

`RunProgressViewModelTests.cs` already covers `DescribePause` end-to-end via a parameterized `[Theory]` (around line 303-327): it pauses a real persisted run with a given reason, refreshes the VM, and asserts `vm.CurrentActivity` equals the expected loc KEY (the test file's fake `ILocalizationService` returns the key itself, so this is a bare key-lookup assertion, not resolved text). Add a new row to this SAME theory:

```csharp
    [Theory]
    [InlineData("children-parked", "Run_Activity_ChildrenParked")]
    [InlineData("children-interrupted", "Run_Activity_ChildrenInterrupted")]
    [InlineData("user", "Run_Activity_UserPaused")]
    [InlineData("resume-interrupted", "Run_Activity_ResumeInterrupted")]
    [InlineData("needs-goal", "Run_Activity_NeedsGoal")]
    [InlineData("needs-input", "Run_Activity_NeedsInput")]
    [InlineData("step-cap", "Run_Activity_WaitingAtBudget")]
    [InlineData("wall-clock", "Run_Activity_WaitingAtBudget")]
    [InlineData("something-a-later-build-invented", "Run_Activity_WaitingAtBudget")]
    [InlineData("plan-approval", "Run_Activity_PlanApproval")] // NEW row for this task
    public async Task AParkedRunsActivityLineNamesWhyItParked(string reason, string expectedKey)
    {
        var run = await NewPlannedRunAsync();
        var vm = CreateVm(run.Id);

        await _runs.PauseAsync(run.Id, reason, TestContext.Current.CancellationToken);
        await vm.RefreshAsync();

        Assert.Equal(RunProgressState.WaitingForInput, vm.State);
        Assert.Equal(expectedKey, vm.CurrentActivity);
        vm.Dispose();
    }
```

Only the new `InlineData` line and the `AgentRunOrchestrator.cs` switch-arm change are new; the method body itself is unchanged from what already exists at `RunProgressViewModelTests.cs:303-327`.

- [ ] **Step 3: Run, confirm fail → pass**

```bash
dotnet test -- --filter-method "*DescribePause_ReturnsPlanApprovalWording*"
```

- [ ] **Step 4: Commit**

```bash
git add src/Pia.Wpf/ViewModels/RunProgressViewModel.cs tests/Pia.Wpf.Tests/ViewModels/RunProgressViewModelTests.cs
git commit -m "Add the plan-approval arm to RunProgressViewModel.DescribePause"
```

### Task 3.3: `AgentRunNotificationSurface` — `PausedBodyKey` arm and `needsAnswerElsewhere` join

**Files:**
- Modify: `src/Pia.Wpf/Services/AgentRunNotificationSurface.cs:94-113` (`PausedBodyKey`)
- Modify: `src/Pia.Wpf/Services/AgentRunNotificationSurface.cs:196-197` (`needsAnswerElsewhere`)
- Test: grep `tests/Pia.Wpf.Tests/Services/` for the existing `AgentRunNotificationSurface` test file

- [ ] **Step 1: Add the `PausedBodyKey` arm**

Current switch (exact text confirmed above):

```csharp
    internal static string PausedBodyKey(string? reason) => reason switch
    {
        AgentRunOrchestrator.ToolApprovalReason => "Flow_Run_ToolApproval",
        AgentRunOrchestrator.ChildrenParkedReason => "Flow_Run_ChildrenParked",
        AgentRunService.ChildrenInterruptedReason => "Flow_Run_ChildrenInterrupted",
        AgentRunOrchestrator.NeedsGoalReason => "Flow_Run_NeedsGoal",
        AgentRunOrchestrator.NeedsInputReason => "Flow_Run_NeedsInput",
        AgentRunService.UserPausedReason => "Flow_Run_UserPaused",
        HeadlessRunLauncher.ResumeInterruptedReason => "Flow_Run_ResumeInterrupted",
        _ => "Flow_Run_WaitingAtBudget",
    };
```

Add, beside the `NeedsGoalReason`/`NeedsInputReason` arms:

```csharp
        AgentRunOrchestrator.PlanApprovalReason => "Flow_Run_PlanApproval",
```

- [ ] **Step 2: Join `needsAnswerElsewhere`**

Current text (`AgentRunNotificationSurface.cs:196-197`):

```csharp
            var needsAnswerElsewhere = reason == AgentRunOrchestrator.NeedsGoalReason
                || reason == AgentRunOrchestrator.NeedsInputReason;
```

Change to:

```csharp
            var needsAnswerElsewhere = reason == AgentRunOrchestrator.NeedsGoalReason
                || reason == AgentRunOrchestrator.NeedsInputReason
                || reason == AgentRunOrchestrator.PlanApprovalReason;
```

This is the fix that keeps a Flow notification's link from silently one-click-approving the plan: without it, `PlanApprovalReason` falls through to the default `ContinueRunAction` (bare `ResumeAsync`, no card); with it, the Flow card uses `OpenParkedRunAction` (routes to chat, resolves nothing on click) — the same treatment the clarification parks already get.

- [ ] **Step 3: Add an `InlineData` row to `AParkedRunsFlowBodyNamesWhyItParked`**

`AgentRunNotificationSurfaceTests.cs` already covers `PausedBodyKey` via a `[Theory]` (around line 39-53). Add a new row:

```csharp
    [Theory]
    [InlineData("children-parked", "Flow_Run_ChildrenParked")]
    [InlineData("children-interrupted", "Flow_Run_ChildrenInterrupted")]
    [InlineData("user", "Flow_Run_UserPaused")]
    [InlineData("resume-interrupted", "Flow_Run_ResumeInterrupted")]
    [InlineData("needs-goal", "Flow_Run_NeedsGoal")]
    [InlineData("needs-input", "Flow_Run_NeedsInput")]
    [InlineData("step-cap", "Flow_Run_WaitingAtBudget")]
    [InlineData("wall-clock", "Flow_Run_WaitingAtBudget")]
    [InlineData(null, "Flow_Run_WaitingAtBudget")]
    [InlineData("plan-approval", "Flow_Run_PlanApproval")] // NEW row for this task
    public void AParkedRunsFlowBodyNamesWhyItParked(string? reason, string expectedKey)
        => Assert.Equal(expectedKey, AgentRunNotificationSurface.PausedBodyKey(reason));
```

- [ ] **Step 4: Add an `InlineData` row to `NeedsClarificationPark_CardIsTokenKeyed_AndRoutesToTheRun` (the more important test — it proves the Flow routing, not just the wording)**

This existing `[Theory]` (around line 211-228) already builds a run parked with a given reason, publishes it through the surface, and asserts the published card uses `OpenParkedRunAction`. Despite its name (written when only the two clarification reasons existed), it is the exact right place for the plan-approval row too — it is generic over `reason`/`expectedKey`:

```csharp
    [Theory]
    [InlineData("needs-goal", "Flow_Run_NeedsGoal")]
    [InlineData("needs-input", "Flow_Run_NeedsInput")]
    [InlineData("plan-approval", "Flow_Run_PlanApproval")] // NEW row for this task
    public async Task NeedsClarificationPark_CardIsTokenKeyed_AndRoutesToTheRun(string reason, string expectedKey)
    {
        var runId = Guid.NewGuid();
        SetupRun(runId, RunShape.Planned, extraJson: $$"""{"paused":true,"reason":"{{reason}}"}""");
        _windows.IsInForeground(WindowMode.Assistant).Returns(false);

        await Create().HandleRunStateAsync(runId, AgentRunState.WaitingForInput);

        _flow.Received(1).Publish(Arg.Is<FlowItemDraft>(d =>
            d.Severity == FlowSeverity.ActionRequired &&
            d.Title == "Flow_Run_Title" &&
            d.Body == expectedKey &&
            d.Action is OpenParkedRunAction));
    }
```

Without this row (and the `needsAnswerElsewhere` join from Step 2), a `plan-approval` park would fall through to `ContinueRunAction` and this exact assertion (`d.Action is OpenParkedRunAction`) is what would catch that regression.

- [ ] **Step 5: Run, confirm fail → pass**

```bash
dotnet test -- --filter-method "*PlanApproval*"
```

- [ ] **Step 6: Commit**

```bash
git add src/Pia.Wpf/Services/AgentRunNotificationSurface.cs tests/Pia.Wpf.Tests/Services/AgentRunNotificationSurfaceTests.cs
git commit -m "Route plan-approval Flow cards to chat instead of a bare one-click resume"
```

### Task 3.4: `HeadlessRunLauncher.InterruptedReasonFor` allowlist join

**Files:**
- Modify: `src/Pia.Wpf/Services/HeadlessRunLauncher.cs:151-156`
- Test: `tests/Pia.Wpf.Tests/Services/HeadlessRunLauncherTests.cs` — add an `InlineData` row to the existing `Resume_InterruptedBeforeDispatch_ReParksWithTheTokenTheNextResumeNeeds` theory (around line 1148-1166). No test anywhere reaches `InterruptedReasonFor` via reflection today — this theory is the real, end-to-end coverage (it simulates a pre-dispatch resume failure via `BuildLauncher(nullDefaultProvider: true)` and asserts the re-park's reason).

- [ ] **Step 1: Add the join**

Current text (exact, confirmed above):

```csharp
    private static string InterruptedReasonFor(string? parkReason) => parkReason switch
    {
        AgentRunOrchestrator.NeedsGoalReason => AgentRunOrchestrator.NeedsGoalReason,
        AgentRunOrchestrator.NeedsInputReason => AgentRunOrchestrator.NeedsInputReason,
        _ => ResumeInterruptedReason,
    };
```

Change to:

```csharp
    private static string InterruptedReasonFor(string? parkReason) => parkReason switch
    {
        AgentRunOrchestrator.NeedsGoalReason => AgentRunOrchestrator.NeedsGoalReason,
        AgentRunOrchestrator.NeedsInputReason => AgentRunOrchestrator.NeedsInputReason,
        AgentRunOrchestrator.PlanApprovalReason => AgentRunOrchestrator.PlanApprovalReason,
        _ => ResumeInterruptedReason,
    };
```

Without this, a failed Approve dispatch (a persona/provider/workspace-resolve error between the CAS win and the orchestrator loop starting) would re-park the run with the generic `ResumeInterruptedReason` instead of `PlanApprovalReason` — silently dropping the Approve/Reject card and leaving only a bare Continue.

- [ ] **Step 2: Add an `InlineData` row to the existing theory**

```csharp
    [Theory]
    [InlineData("needs-goal", "needs-goal")]
    [InlineData("needs-input", "needs-input")]
    [InlineData("step-cap", "resume-interrupted")]
    [InlineData("plan-approval", "plan-approval")] // NEW row for this task
    public async Task Resume_InterruptedBeforeDispatch_ReParksWithTheTokenTheNextResumeNeeds(
        string parkReason, string expectedAfterRePark)
    {
        var ct = TestContext.Current.CancellationToken;
        var (launcher, _) = BuildLauncher(nullDefaultProvider: true);
        var parked = await ParkRunWithNoStepsAsync(parkReason);

        Assert.False(await launcher.ResumeAsync(parked.Id, ct: ct));

        var after = await _runs.GetAsync(parked.Id, ct);
        Assert.Equal(AgentRunState.WaitingForInput, after!.State); // re-parked, still resumable
        Assert.Equal(expectedAfterRePark, ReadPauseReason(after));

        try { Directory.Delete(Path.Combine(_runsBase, parked.Id.ToString()), true); } catch { }
    }
```

Only the new `InlineData` row and the `HeadlessRunLauncher.cs` allowlist change are new; the method body is unchanged from what already exists at `HeadlessRunLauncherTests.cs:1148-1166`.

- [ ] **Step 3: Run, confirm fail → pass**

```bash
dotnet test -- --filter-method "*Resume_InterruptedBeforeDispatch_ReParksWithTheTokenTheNextResumeNeeds*"
```

- [ ] **Step 4: Run the full gate**

```bash
dotnet test
```

- [ ] **Step 5: Commit**

```bash
git add src/Pia.Wpf/Services/HeadlessRunLauncher.cs tests/Pia.Wpf.Tests/Services/HeadlessRunLauncherTests.cs
git commit -m "Preserve the plan-approval reason across an interrupted resume"
```

---

## Chunk 4: The Reject primitive + rejected-plan chat notice

### Task 4.1: `IAgentRunService.TryRejectParkedPlanAsync`

**Files:**
- Modify: `src/Pia.Wpf/Services/Interfaces/IAgentRunService.cs` (interface member, add after `TryResumeFromPauseAsync`, around line 248)
- Modify: `src/Pia.Wpf/Services/AgentRunService.cs` (implementation, add after `TryResumeFromPauseAsync`, around line 546)
- Test: `tests/Pia.Wpf.Tests/Services/AgentRunServiceTests.cs` (grep to confirm the exact file name — it may be split, e.g. a `...PauseTests.cs` sibling)

- [ ] **Step 1: Add the interface member**

```csharp
    /// <summary>
    /// The user's Reject on a plan-approval park: CAS <see cref="AgentRunState.WaitingForInput"/> (reason
    /// <c>AgentRunOrchestrator.PlanApprovalReason</c>) directly to <see cref="AgentRunState.Cancelled"/>.
    /// No re-dispatch — unlike every resume primitive above, a parked run has already RETURNED from
    /// <c>RunAsync</c>, so there is no in-flight loop for this call to cancel via a token; it settles the row
    /// itself. Gated on BOTH state AND reason (checked together, under the same write lock as the CAS) so a
    /// stale Reject click can never land on a run that resumed and re-parked on a DIFFERENT question since —
    /// state alone is not enough here, unlike every sibling CAS in this file, because those never need to
    /// tell one <see cref="AgentRunState.WaitingForInput"/> reason apart from another.
    /// <para>
    /// Stamps <c>CompletedAt</c> (every writer of <see cref="AgentRunState.Cancelled"/> does) and clears
    /// <c>ExtraJson</c> (the same convention <see cref="TryBeginResumeAsync"/>/<see cref="TryResumeFromPauseAsync"/>
    /// use for a claim that retires the pause marker it consumed) but does NOT touch the ledger clock: the
    /// park already closed its work segment, and this transition opens no new one, so re-closing it would
    /// double-close (the same "do not touch" precedent <c>FailInterruptedRunsAsync</c>'s re-park statement
    /// sets). Raises <c>RunChanged(Cancelled)</c> only on the win.
    /// </para>
    /// </summary>
    Task<bool> TryRejectParkedPlanAsync(Guid runId, CancellationToken ct = default);
```

- [ ] **Step 2: Add the implementation**

```csharp
    public Task<bool> TryRejectParkedPlanAsync(Guid runId, CancellationToken ct = default)
    {
        int affected;
        lock (_gate)
        {
            if (_disposed) return Task.FromResult(false);

            var connection = Connection();
            AgentRun? run;
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $"SELECT {RunColumns} FROM AgentRuns WHERE Id=@Id";
                cmd.Parameters.AddWithValue("@Id", runId.ToString());
                using var reader = cmd.ExecuteReader();
                run = reader.Read() ? MapRun(reader) : null;
            }

            // The reason check is why this cannot be a bare `WHERE State=@Expected` CAS like every sibling
            // above: state alone cannot tell "still the plan this Reject click was shown for" apart from
            // "resumed and re-parked on a different question since". Read and write happen under the SAME
            // _gate hold as every other primitive in this file, so nothing can move the row in between.
            if (run is null || run.State != AgentRunState.WaitingForInput
                || RunPauseEnvelope.ReadReason(run) != AgentRunOrchestrator.PlanApprovalReason)
            {
                affected = 0;
            }
            else
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText =
                    "UPDATE AgentRuns SET State=@New, CompletedAt=@Now, UpdatedAt=@Now, ExtraJson=NULL WHERE Id=@Id AND State=@Expected";
                cmd.Parameters.AddWithValue("@New", (int)AgentRunState.Cancelled);
                cmd.Parameters.AddWithValue("@Now", DateTime.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("@Id", runId.ToString());
                cmd.Parameters.AddWithValue("@Expected", (int)AgentRunState.WaitingForInput);
                affected = cmd.ExecuteNonQuery();
                // Deliberately NO MoveLedgerClock call — see the interface doc comment.
            }
        }

        if (affected > 0)
        {
            _logger.LogInformation("Run {RunId} plan rejected → Cancelled", runId);
            RunChanged?.Invoke(this, new AgentRunChangedEventArgs(runId, AgentRunState.Cancelled));
        }
        else
        {
            _logger.LogInformation("Run {RunId} plan-reject not applied — no longer parked on plan-approval", runId);
        }
        return Task.FromResult(affected > 0);
    }
```

Place this method immediately after `TryResumeFromPauseAsync` in `AgentRunService.cs`.

- [ ] **Step 3: Update the four hand-rolled `IAgentRunService` test fakes — REQUIRED, or the test project fails to build**

`IAgentRunService` has no default-interface members, and exactly four test files hand-roll a full implementation of it (confirmed by grep, no others exist):

- `tests/Pia.Wpf.Tests/Services/AgentRunOrchestratorTests.cs` — `FaultyRunService` (wraps `_inner`)
- `tests/Pia.Wpf.Tests/Services/AgentRunClarificationResumeTests.cs` — `SpyRunService` (wraps `_inner`)
- `tests/Pia.Wpf.Tests/Services/AgentRunResumeNoRePlanPremiseTests.cs` — `SpyRunService` (wraps `_inner` — confirm this one also wraps rather than hand-implements before copying the one-liner below; if it doesn't, use the `ThrowingAgentRunService` shape instead)
- `tests/Pia.Wpf.Tests/Services/BackgroundAssistantTurnRunnerRunSpineTests.cs` — `ThrowingAgentRunService` (does NOT wrap an inner — every member throws `InvalidOperationException("boom")`)

Add the new member to each. For the three `_inner`-wrapping fakes, add (matching their existing one-line forwarding style, e.g. beside `TryResumeFromPauseAsync`'s forward):

```csharp
        public Task<bool> TryRejectParkedPlanAsync(Guid runId, CancellationToken ct = default) => _inner.TryRejectParkedPlanAsync(runId, ct);
```

For `ThrowingAgentRunService`, add (matching its existing "boom" pattern):

```csharp
        public Task<bool> TryRejectParkedPlanAsync(Guid runId, CancellationToken ct = default) => throw new InvalidOperationException("boom");
```

Build immediately after this step, before writing any new tests, to confirm the four `CS0535` errors this interface addition would otherwise cause are gone:

```bash
dotnet build tests/Pia.Wpf.Tests/Pia.Wpf.Tests.csproj
```

Expected: `0 Error(s)`.

- [ ] **Step 4: Write a failing test — the happy path**

Mirror this file's existing `TryResumeFromPauseAsync`/`TryPauseUserAsync` test setup (an in-memory or temp-file SQLite-backed `AgentRunService`, a run created and parked):

```csharp
[Fact]
public async Task TryRejectParkedPlanAsync_CancelsAPlanApprovalPark()
{
    var run = await service.CreateAsync(new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.User, Goal: "goal", PolicyJson: null));
    await service.PauseAsync(run.Id, AgentRunOrchestrator.PlanApprovalReason);

    var result = await service.TryRejectParkedPlanAsync(run.Id);

    Assert.True(result);
    var updated = await service.GetAsync(run.Id);
    Assert.Equal(AgentRunState.Cancelled, updated!.State);
    Assert.NotNull(updated.CompletedAt);
    Assert.Null(updated.ExtraJson);
}
```

- [ ] **Step 5: Write a failing test — wrong reason must not match**

```csharp
[Fact]
public async Task TryRejectParkedPlanAsync_DoesNotCancel_WhenParkedForADifferentReason()
{
    var run = await service.CreateAsync(new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.User, Goal: "goal", PolicyJson: null));
    await service.PauseAsync(run.Id, AgentRunOrchestrator.NeedsInputReason); // a DIFFERENT WaitingForInput reason

    var result = await service.TryRejectParkedPlanAsync(run.Id);

    Assert.False(result);
    var updated = await service.GetAsync(run.Id);
    Assert.Equal(AgentRunState.WaitingForInput, updated!.State); // untouched
}
```

- [ ] **Step 6: Write a failing test — `RunChanged` fires only on the win**

```csharp
[Fact]
public async Task TryRejectParkedPlanAsync_RaisesRunChangedCancelled_OnlyOnTheWin()
{
    var run = await service.CreateAsync(new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.User, Goal: "goal", PolicyJson: null));
    await service.PauseAsync(run.Id, AgentRunOrchestrator.PlanApprovalReason);

    AgentRunChangedEventArgs? raised = null;
    service.RunChanged += (_, e) => raised = e;

    await service.TryRejectParkedPlanAsync(run.Id);

    Assert.NotNull(raised);
    Assert.Equal(AgentRunState.Cancelled, raised!.State);
}
```

- [ ] **Step 7: Run all three, confirm fail → pass**

```bash
dotnet test -- --filter-method "*TryRejectParkedPlanAsync*"
```

- [ ] **Step 8: Run the full gate**

```bash
dotnet test
```

- [ ] **Step 9: Commit**

```bash
git add src/Pia.Wpf/Services/Interfaces/IAgentRunService.cs src/Pia.Wpf/Services/AgentRunService.cs tests/Pia.Wpf.Tests/Services/AgentRunServiceTests.cs tests/Pia.Wpf.Tests/Services/AgentRunOrchestratorTests.cs tests/Pia.Wpf.Tests/Services/AgentRunClarificationResumeTests.cs tests/Pia.Wpf.Tests/Services/AgentRunResumeNoRePlanPremiseTests.cs tests/Pia.Wpf.Tests/Services/BackgroundAssistantTurnRunnerRunSpineTests.cs
git commit -m "Add TryRejectParkedPlanAsync: CAS a plan-approval park straight to Cancelled"
```

### Task 4.2: `AgentRunOrchestrator.PostPlanRejectedNoticeAsync`

**Files:**
- Modify: `src/Pia.Wpf/Services/AgentRunOrchestrator.cs` (new trailing constructor parameter + new public method)
- Modify: every existing positional `new AgentRunOrchestrator(...)` test construction that would otherwise be unaffected (none need editing — the new parameter is trailing and defaulted, per this class's own established convention; this step is a grep-and-confirm, not an edit)
- Modify: `src/Pia.Wpf/Resources/Strings/ViewStrings.resx` + `.de.resx` + `.fr.resx` (one new key)
- Test: `tests/Pia.Wpf.Tests/Services/AgentRunOrchestratorTests.cs`. No existing test anywhere covers `PostAndMirrorClarificationQuestionAsync`/`SafePostClarificationQuestionAsync` — this file's `Harness.BuildOrchestrator` (line 235-239) never passes `chats:` even though `Harness.Chats` (a real `AssistantChatService`) exists on the harness, and there is no reusable `ILocalizationService` fake anywhere in this file. Step 3 below extends the harness rather than assuming either already works.

- [ ] **Step 1: Add the loc key**

`ViewStrings.resx`:
```xml
<data name="Run_PlanRejected_ChatNote" xml:space="preserve"><value>The proposed plan was rejected.</value></data>
```

`ViewStrings.de.resx`:
```xml
<data name="Run_PlanRejected_ChatNote" xml:space="preserve"><value>Der vorgeschlagene Plan wurde abgelehnt.</value></data>
```

`ViewStrings.fr.resx`:
```xml
<data name="Run_PlanRejected_ChatNote" xml:space="preserve"><value>Le plan proposé a été rejeté.</value></data>
```

Regenerate `ViewStrings.Designer.cs` the same manual way as Task 3.1 Step 3, then re-run the parity test:

```bash
dotnet test -- --filter-method "*AllTranslations_MustBeComplete*"
```

- [ ] **Step 2: Add the trailing constructor parameter**

`AgentRunOrchestrator`'s constructor takes every optional dependency as TRAILING and DEFAULTED specifically so its dozen positional test constructions never break (see the class's own doc comments on `workspaces`/`childLauncher`/`chats`/`steering`, all phrased this way). Add `localization` the same way. Current constructor:

```csharp
    public AgentRunOrchestrator(
        IAgentRunService runService,
        IAgentPlanner planner,
        IAgentVerifier verifier,
        ILogger<AgentRunOrchestrator> logger,
        IRunWorkspaceService? workspaces = null,
        IHeadlessRunLauncher? childLauncher = null,
        IAssistantChatService? chats = null,
        IRunSteeringStore? steering = null)
    {
        _runService = runService;
        _planner = planner;
        _verifier = verifier;
        _logger = logger;
        _workspaces = workspaces;
        _childLauncher = childLauncher;
        _chats = chats;
        _steering = steering;
    }
```

Change to:

```csharp
    public AgentRunOrchestrator(
        IAgentRunService runService,
        IAgentPlanner planner,
        IAgentVerifier verifier,
        ILogger<AgentRunOrchestrator> logger,
        IRunWorkspaceService? workspaces = null,
        IHeadlessRunLauncher? childLauncher = null,
        IAssistantChatService? chats = null,
        IRunSteeringStore? steering = null,
        ILocalizationService? localization = null)
    {
        _runService = runService;
        _planner = planner;
        _verifier = verifier;
        _logger = logger;
        _workspaces = workspaces;
        _childLauncher = childLauncher;
        _chats = chats;
        _steering = steering;
        _localization = localization;
    }
```

Add the backing field beside the others near the top of the class:

```csharp
    private readonly ILocalizationService? _localization;
```

Add the doc comment above the constructor's `<param name="localization">` the same way every other trailing-and-defaulted parameter here is documented:

```csharp
    /// <param name="localization">TRAILING and DEFAULTED, like every dependency this loop has gained: null ⇒
    /// <see cref="PostPlanRejectedNoticeAsync"/> silently posts nothing, the same "optional dependency
    /// degrades to no-op" shape <see cref="_chats"/> already has.</param>
```

- [ ] **Step 3: Add `PostPlanRejectedNoticeAsync`**

Add this new PUBLIC method (called from OUTSIDE any `RunAsync` dispatch — Reject never re-enters the drain loop, so this fetches the run itself):

```csharp
    /// <summary>
    /// Posts a short "plan rejected" notice into the run's own chat, called by the Reject path AFTER
    /// <c>IAgentRunService.TryRejectParkedPlanAsync</c> has already settled the row to
    /// <see cref="AgentRunState.Cancelled"/>. Durable post only, no live-mirror: unlike a park inside
    /// <c>RunAsync</c>, Reject has no attached <see cref="IAgentTurnExecutor"/> to mirror into (the run's
    /// Live session already released itself back at park time) — an open chat window's own ChatsChanged
    /// pull picks the durable row up the same way it does for any other writer.
    /// </summary>
    public async Task PostPlanRejectedNoticeAsync(Guid runId, Persona persona, CancellationToken ct)
    {
        if (_localization is null)
            return;

        var run = await SafeGetRunAsync(runId, ct).ConfigureAwait(false);
        if (run is null)
            return;

        await SafePostClarificationQuestionAsync(
            run, persona, Guid.NewGuid(), _localization["Run_PlanRejected_ChatNote"]).ConfigureAwait(false);
    }
```

Confirm `SafeGetRunAsync` exists with this exact shape (it was cited earlier in this plan's research as `SafeGetRunAsync(Guid runId, CancellationToken ct)` around line 1532) before relying on it — grep to confirm its return type is `Task<AgentRun?>`.

- [ ] **Step 4: Extend the test `Harness` to pass `chats` and a fake `localization`**

`Harness.BuildOrchestrator` (`AgentRunOrchestratorTests.cs:235-239`) currently is:

```csharp
        public AgentRunOrchestrator BuildOrchestrator(
            IAgentPlanner planner, IAgentVerifier? verifier = null,
            IRunWorkspaceService? workspaces = null, IAgentRunService? runService = null) =>
            new(runService ?? Runs, planner, verifier ?? new FakeVerifier(),
                NullLogger<AgentRunOrchestrator>.Instance, workspaces);
```

Change to (new trailing-optional parameter, so every one of this file's dozens of existing call sites is unaffected):

```csharp
        public AgentRunOrchestrator BuildOrchestrator(
            IAgentPlanner planner, IAgentVerifier? verifier = null,
            IRunWorkspaceService? workspaces = null, IAgentRunService? runService = null,
            ILocalizationService? localization = null) =>
            new(runService ?? Runs, planner, verifier ?? new FakeVerifier(),
                NullLogger<AgentRunOrchestrator>.Instance, workspaces, chats: Chats, localization: localization);
```

`Chats` (the harness's real `AssistantChatService`) is now always passed — this is safe because every existing test either never calls a code path that reads `_chats` (so this is a no-op for them) or already relies on `Chats` being the same real backing store `NewRunAsync` uses.

- [ ] **Step 5: Write a failing test — the happy path**

Use `Substitute.For<ILocalizationService>()` with the same "echo the key" stub `RunProgressViewModelTests.cs` already uses (`_loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);`), so the assertion can check for the literal loc KEY without needing real resolved text:

```csharp
[Fact]
public async Task PostPlanRejectedNoticeAsync_PostsANoticeIntoTheRunsChat()
{
    var ct = TestContext.Current.CancellationToken;
    var h = new Harness();
    var run = await h.NewRunAsync("goal");
    var loc = Substitute.For<ILocalizationService>();
    loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
    var orchestrator = h.BuildOrchestrator(new StubPlanner(PlanResult.Fallback), localization: loc);

    await orchestrator.PostPlanRejectedNoticeAsync(run.Id, Persona(), ct);

    var chat = await h.Chats.GetAsync(run.ChatId, ct);
    Assert.Contains(chat!.Messages, m => m.Content == "Run_PlanRejected_ChatNote");
}
```

Adjust `new StubPlanner(...)`/`Persona()` to whatever this file's own helpers are actually named (grep the file — it has its own `Persona()`/`Provider()` helpers used throughout, and its own planner fake, possibly named differently from `AgentRunOrchestratorArmTests.cs`'s `StubPlanner`; a `PlanAsync`/`ReplanAsync` are never invoked by this test, so any minimal `IAgentPlanner` fake in this file works).

- [ ] **Step 6: Write a failing test for the null-localization no-op**

```csharp
[Fact]
public async Task PostPlanRejectedNoticeAsync_NoOps_WhenLocalizationIsNull()
{
    var ct = TestContext.Current.CancellationToken;
    var h = new Harness();
    var run = await h.NewRunAsync("goal");
    var orchestrator = h.BuildOrchestrator(new StubPlanner(PlanResult.Fallback)); // localization defaults null

    await orchestrator.PostPlanRejectedNoticeAsync(run.Id, Persona(), ct);

    var chat = await h.Chats.GetAsync(run.ChatId, ct);
    Assert.Empty(chat?.Messages ?? []);
}
```

- [ ] **Step 7: Run, confirm fail → pass**

```bash
dotnet test -- --filter-method "*PostPlanRejectedNoticeAsync*"
```

- [ ] **Step 8: Commit**

```bash
git add src/Pia.Wpf/Services/AgentRunOrchestrator.cs src/Pia.Wpf/Resources/Strings/ViewStrings.resx src/Pia.Wpf/Resources/Strings/ViewStrings.de.resx src/Pia.Wpf/Resources/Strings/ViewStrings.fr.resx src/Pia.Wpf/Resources/Strings/ViewStrings.Designer.cs tests/Pia.Wpf.Tests/Services/AgentRunOrchestratorTests.cs
git commit -m "Post a chat notice when a proposed plan is rejected"
```

### Task 4.3: `IAgentRunResumeService.RejectPlanAsync` + `HeadlessRunLauncher` implementation

**Files:**
- Modify: `src/Pia.Wpf/Services/Interfaces/IAgentRunResumeService.cs`
- Modify: `src/Pia.Wpf/Services/HeadlessRunLauncher.cs`
- Test: `tests/Pia.Wpf.Tests/Services/HeadlessRunLauncherTests.cs` — its shared `BuildLauncher(...)` helper (lines 127-224) registers a `ServiceCollection` for the launcher's per-run DI scope, but never registers `ILocalizationService`. Since `AgentRunOrchestrator` now takes it as a trailing-optional constructor parameter, an unregistered service resolves to the default `null` (the same "trailing-optional, so an unregistered store is silently absent" behavior this file's own comment already documents for `steering`, line 211-213) — so `PostPlanRejectedNoticeAsync`'s `if (_localization is null) return;` guard would fire and the happy-path test below would have nothing to assert. Step 0 registers a stub so the notice actually posts.

- [ ] **Step 1: Register a stub `ILocalizationService` in `BuildLauncher`**

In `HeadlessRunLauncherTests.cs`, inside `BuildLauncher(...)` (around line 191-217), add — beside the existing `services.AddSingleton<IAgentVerifier>(...)` line, using the same "echo the key" NSubstitute stub `RunProgressViewModelTests.cs` already uses elsewhere in this suite:

```csharp
        var loc = Substitute.For<ILocalizationService>();
        loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        services.AddSingleton(loc);
```

- [ ] **Step 2: Add the interface member**

```csharp
    /// <summary>
    /// Reject a plan-approval park: settles it straight to <c>Cancelled</c> via
    /// <c>IAgentRunService.TryRejectParkedPlanAsync</c> — no re-dispatch, unlike <see cref="ResumeAsync"/> and
    /// <see cref="DeclineAsync"/>, both of which re-launch the run. On the win, best-effort posts a short
    /// notice into the run's own chat. Returns <c>false</c> when the run is not parked on a plan-approval
    /// question (already resumed, already terminal, or parked on something else) or the CAS is lost.
    /// </summary>
    Task<bool> RejectPlanAsync(Guid runId, CancellationToken ct = default);
```

- [ ] **Step 2: Implement on `HeadlessRunLauncher`**

Add near `DeclineAsync` (which is the interface's other short forwarding method):

```csharp
    /// <summary>
    /// Reject a plan-approval park. Unlike <see cref="ResumeAsync"/>/<see cref="DeclineAsync"/>, this never
    /// re-enters the dispatch machinery — <c>TryRejectParkedPlanAsync</c> settles the row directly, and the
    /// chat notice is posted through a scoped <see cref="AgentRunOrchestrator"/>, the same DI-scope-resolution
    /// pattern <see cref="RunResumedDispatchAsync"/> already uses to get one.
    /// </summary>
    public async Task<bool> RejectPlanAsync(Guid runId, CancellationToken ct = default)
    {
        var rejected = await _agentRunService.TryRejectParkedPlanAsync(runId, ct).ConfigureAwait(false);
        if (!rejected)
            return false;

        try
        {
            var settings = await _settingsService.GetSettingsAsync().ConfigureAwait(false);
            // Reuses the same persona-resolution helper the launch/resume path already has (line 1064),
            // rather than re-deriving the mode fallback inline.
            var persona = await ResolveRunPersonaAsync(personaIdOverride: null, settings).ConfigureAwait(false);

            using var scope = _scopeFactory.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<AgentRunOrchestrator>();
            await orchestrator.PostPlanRejectedNoticeAsync(runId, persona, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Best-effort: the run IS rejected regardless (the CAS above already committed), a failed notice
            // is a missing chat line, not a wedged run.
            _logger.LogWarning(ex, "Failed to post the plan-rejected notice for run {RunId}", runId);
        }

        return true;
    }
```

Confirm `_settingsService`, `_personaService`, `_scopeFactory` are the exact field names already in this class (all four were confirmed present and used with these exact names inside `ResumeAsync`/`RunResumedDispatchAsync` in the research above) before relying on them verbatim.

Note on resilience: `SafePostClarificationQuestionAsync` (which `PostPlanRejectedNoticeAsync` calls) already wraps its own body in a try/catch that logs and swallows — by the time an exception could reach `RejectPlanAsync`'s own try/catch above, it would have to come from `_scopeFactory.CreateScope()` or `GetRequiredService<AgentRunOrchestrator>()` itself (a DI resolution fault), not from the chat write. This is a narrow, hard-to-cheaply-simulate edge case in `BuildLauncher`'s standard fixture (every dependency `AgentRunOrchestrator` needs is already registered there) — Step 5 below covers the two mechanism-verifiable behaviors (happy path, wrong-reason no-op) and leaves the DI-resolution-fault edge case as a manual code-review check rather than a fabricated test double, since forcing that specific fault would require a bespoke, non-standard DI setup whose only purpose is to prove a defensive `catch` block is reachable.

- [ ] **Step 3: Write a failing test — happy path**

Using this file's real fixtures (`_runs`, `ParkRunWithNoStepsAsync`, `BuildLauncher()`):

```csharp
[Fact]
public async Task RejectPlanAsync_CancelsTheRun_AndPostsANotice()
{
    var ct = TestContext.Current.CancellationToken;
    var (launcher, _) = BuildLauncher();
    var parked = await ParkRunWithNoStepsAsync(AgentRunOrchestrator.PlanApprovalReason);

    var result = await launcher.RejectPlanAsync(parked.Id, ct);

    Assert.True(result);
    var updated = await _runs.GetAsync(parked.Id, ct);
    Assert.Equal(AgentRunState.Cancelled, updated!.State);
    Assert.NotNull(updated.CompletedAt);
    var chat = await _chats.GetAsync(updated.ChatId, ct);
    Assert.Contains(chat!.Messages, m => m.Content == "Run_PlanRejected_ChatNote");
}
```

- [ ] **Step 4: Write a failing test — false when not parked on this reason**

```csharp
[Fact]
public async Task RejectPlanAsync_ReturnsFalse_WhenRunIsNotParkedOnPlanApproval()
{
    var ct = TestContext.Current.CancellationToken;
    var (launcher, _) = BuildLauncher();
    var parked = await ParkRunWithNoStepsAsync(AgentRunOrchestrator.NeedsInputReason); // a DIFFERENT reason

    var result = await launcher.RejectPlanAsync(parked.Id, ct);

    Assert.False(result);
    var updated = await _runs.GetAsync(parked.Id, ct);
    Assert.Equal(AgentRunState.WaitingForInput, updated!.State); // untouched
}
```

- [ ] **Step 5: Run, confirm fail → pass**

```bash
dotnet test -- --filter-method "*RejectPlanAsync*"
```

- [ ] **Step 6: Run the full gate**

```bash
dotnet test
```

- [ ] **Step 7: Commit**

```bash
git add src/Pia.Wpf/Services/Interfaces/IAgentRunResumeService.cs src/Pia.Wpf/Services/HeadlessRunLauncher.cs tests/Pia.Wpf.Tests/Services/HeadlessRunLauncherTests.cs
git commit -m "Add RejectPlanAsync: settle a plan-approval park to Cancelled with no re-dispatch"
```

---

## Chunk 5: RunProgressViewModel + RunProgressPanel UI

### Task 5.1: Loc keys for the button labels

**Files:**
- Modify: `src/Pia.Wpf/Resources/Strings/ViewStrings.resx` + `.de.resx` + `.fr.resx`

- [ ] **Step 1: Add `Run_Action_ApprovePlan` and `Run_Action_RejectPlan` to all three files**

`ViewStrings.resx` (near `Run_Action_Continue`/`Run_Action_Approve`/`Run_Action_Deny`):

```xml
<data name="Run_Action_ApprovePlan" xml:space="preserve"><value>Approve</value></data>
<data name="Run_Action_RejectPlan" xml:space="preserve"><value>Reject</value></data>
```

`Run_Action_ApprovePlan` is deliberately a NEW key, not a reuse of `Run_Action_Approve` — that key already exists with value "Allow" and is the Flow surface's tool-approval label (`FlowItemViewModel.cs:129`); reusing it here would either collide or silently repurpose an unrelated label.

`ViewStrings.de.resx`:
```xml
<data name="Run_Action_ApprovePlan" xml:space="preserve"><value>Genehmigen</value></data>
<data name="Run_Action_RejectPlan" xml:space="preserve"><value>Ablehnen</value></data>
```

`ViewStrings.fr.resx`:
```xml
<data name="Run_Action_ApprovePlan" xml:space="preserve"><value>Approuver</value></data>
<data name="Run_Action_RejectPlan" xml:space="preserve"><value>Rejeter</value></data>
```

No separate "plan-approval title" loc key is needed here: `RunProgressPanel.xaml`'s signal-band lead line already binds `CurrentActivity` (via `ComputeActivity` → `DescribePause`), and Chunk 3 Task 3.2 already added the `PlanApprovalReason` arm there, returning `Run_Activity_PlanApproval` — the exact same key Task 3.1 already localized in all three resx files. A second, competing lead-line key/property would be redundant dead weight (verified against the actual XAML/VM — see Task 5.2 Step 6 and Task 5.3 Step 3 below, which state this firmly rather than as an open question).

- [ ] **Step 2: Regenerate `Designer.cs` and run the parity test**

Same manual Visual-Studio-save step as Task 3.1 Step 3, then:

```bash
dotnet test -- --filter-method "*AllTranslations_MustBeComplete*"
```

- [ ] **Step 3: Commit**

```bash
git add src/Pia.Wpf/Resources/Strings/ViewStrings.resx src/Pia.Wpf/Resources/Strings/ViewStrings.de.resx src/Pia.Wpf/Resources/Strings/ViewStrings.fr.resx src/Pia.Wpf/Resources/Strings/ViewStrings.Designer.cs
git commit -m "Add Approve/Reject button and plan-approval title loc keys"
```

### Task 5.2: `RunProgressViewModel` — projection, commands, label swap, Region-D suppression

**Files:**
- Modify: `src/Pia.Wpf/ViewModels/RunProgressViewModel.cs`
- Test: `tests/Pia.Wpf.Tests/ViewModels/RunProgressViewModelTests.cs`

- [ ] **Step 1: Add the `[ObservableProperty]` field**

Beside `_isToolApprovalPause`/`_approvalToolName` (lines 137-146), add:

```csharp
    /// <summary>True while the run is parked asking the user to approve its plan before any step runs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRejectPlanButton))]
    [NotifyPropertyChangedFor(nameof(ShowNudgeBox))]
    [NotifyPropertyChangedFor(nameof(ContinueLabel))]
    [NotifyCanExecuteChangedFor(nameof(RejectPlanCommand))]
    private bool _isPlanApprovalPause;
```

- [ ] **Step 2: Project it in `Project(...)`**

Right after the existing tool-approval block (lines 915-922), add:

```csharp
        // Mirrors the tool-approval projection immediately above, for the sibling park reason.
        IsPlanApprovalPause = run.State == AgentRunState.WaitingForInput
            && RunPauseEnvelope.ReadReason(run) == AgentRunOrchestrator.PlanApprovalReason;
```

- [ ] **Step 3: Add `CanRejectPlan`/`ShowRejectPlanButton`**

Beside `CanDeclineTool`/`ShowDenyButton`:

```csharp
    public bool CanRejectPlan => IsPlanApprovalPause && !IsResuming;

    public bool ShowRejectPlanButton => IsPlanApprovalPause;
```

- [ ] **Step 4: Add `RejectPlan()`**

Mirroring `DeclineTool()` exactly, reusing the same `IsResuming` double-click guard (so Approve/Reject/Continue on this card cannot fire concurrently):

```csharp
    /// <summary>
    /// Rejects a plan-approval park: settles the run to Cancelled with no re-dispatch. Same double-click
    /// gate as <see cref="Continue"/>/<see cref="DeclineTool"/>; the CAS in the resume service is the hard
    /// guard.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRejectPlan))]
    private async Task RejectPlan()
    {
        IsResuming = true;
        try
        {
            await _resumeService.RejectPlanAsync(_runId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Run {RunId} plan-reject failed from panel", _runId);
        }
        finally
        {
            IsResuming = false;
        }
    }
```

Note `[NotifyCanExecuteChangedFor(nameof(RejectPlanCommand))]` also needs adding to the `_isResuming` field's attribute list (mirroring the existing `[NotifyCanExecuteChangedFor(nameof(DeclineToolCommand))]` already there at line 134) so a Reject-in-flight disables the button.

- [ ] **Step 5: Add `ContinueLabel` for the Approve/Continue label swap**

```csharp
    /// <summary>The Continue button's label: "Approve" on a plan-approval park (a new decision), "Continue"
    /// everywhere else (an ordinary resume). Run_Action_ApprovePlan is a DISTINCT key from the pre-existing
    /// Run_Action_Approve (that one is the Flow surface's tool-approval label, value "Allow") — reusing it
    /// here would collide.</summary>
    public string ContinueLabel => IsPlanApprovalPause
        ? _localization["Run_Action_ApprovePlan"]
        : _localization["Run_Action_Continue"];
```

Add `[NotifyPropertyChangedFor(nameof(ContinueLabel))]` to `IsPlanApprovalPause`'s attribute list (already added in Step 1).

- [ ] **Step 6: Add `ShowNudgeBox` to suppress Region D on this card**

```csharp
    /// <summary>Region D's visibility. Same state gate as <see cref="ShowContinueButton"/>, minus a
    /// plan-approval park: a steering note ("for the rest of THIS run") is nonsensical before any step has
    /// executed, and Continue() would otherwise ship whatever text sits there straight into
    /// <c>ResumeAsync(_runId, NudgeText)</c> on Approve — contradicting the plain-binary Approve/Reject
    /// decision this card is supposed to be.</summary>
    public bool ShowNudgeBox => ShowContinueButton && !IsPlanApprovalPause;
```

`ShowNudgeBox` also depends on `State` (transitively, via `ShowContinueButton`), not only on `IsPlanApprovalPause`. `_state`'s existing attribute list already carries `[NotifyPropertyChangedFor(nameof(ShowContinueButton))]` (around line 91) but nothing for `ShowNudgeBox` — CommunityToolkit.Mvvm's generated setter only raises `PropertyChanged` for names explicitly listed, it does not cascade through a property's getter body. Without this, an ORDINARY budget pause (`IsPlanApprovalPause` staying `false` throughout) would update `ShowContinueButton`'s binding but never raise `PropertyChanged("ShowNudgeBox")` — regressing the pre-existing steering-note box (Batch 08 D4) across ordinary pause/resume transitions, since Task 5.3 Step 3 rebinds Region D's `Visibility` straight to `ShowNudgeBox`. Add `[NotifyPropertyChangedFor(nameof(ShowNudgeBox))]` to `_state`'s attribute list, beside its existing `ShowContinueButton` entry.

- [ ] **Step 7: (dropped)**

An earlier draft of this plan proposed a `PlanApprovalTitle` property and a matching conditional XAML rebind in Task 5.3, hedging on whether the signal band's lead line already covers this. Verified against the actual source: `RunProgressViewModel.cs`'s `Project` sets `CurrentActivity = ComputeActivity(run)` (line 913), `ComputeActivity` (lines 1208-1227) routes `AgentRunState.WaitingForInput` through `DescribePause(run)`, and `RunProgressPanel.xaml:212-217` binds `CurrentActivity` to the signal band's lead-line `TextBlock`. Chunk 3 Task 3.2 already adds the `PlanApprovalReason` arm to that exact switch. So once Chunk 3 lands, parking for plan approval already renders the lead line with zero further changes — a `PlanApprovalTitle` property would be a second, competing text source bound to the same slot. Confirmed dead weight; not added. (This also means `Run_PlanApproval_Title` was correctly dropped from Task 5.1.)

- [ ] **Step 8: Write failing tests**

`Project(AgentRun, ...)` is `private` — not reachable directly from the test file. Every existing test in this file drives a projection by persisting a real pause via `_runs.PauseAsync(...)` and then calling `await vm.RefreshAsync()` (see `WaitingForInput_ProjectsWaitingState_ContinueEnabled`, lines 287-301), and every test is `async Task`, never synchronous `void`. Follow that exact pattern:

```csharp
[Fact]
public async Task WaitingForInput_ProjectsPlanApprovalPause_ApproveLabelAndNoNudgeBox()
{
    var run = await NewPlannedRunAsync();
    var vm = CreateVm(run.Id);

    await _runs.PauseAsync(run.Id, AgentRunOrchestrator.PlanApprovalReason, TestContext.Current.CancellationToken);
    await vm.RefreshAsync();

    Assert.True(vm.IsPlanApprovalPause);
    Assert.True(vm.ShowRejectPlanButton);
    Assert.False(vm.ShowNudgeBox);
    Assert.Equal("Run_Action_ApprovePlan", vm.ContinueLabel); // _loc echoes the key, per this file's setup (line 32)
    vm.Dispose();
}

[Fact]
public async Task WaitingForInput_OrdinaryToolApprovalPark_LeavesPlanApprovalPropertiesFalse()
{
    var run = await NewPlannedRunAsync();
    var vm = CreateVm(run.Id);

    await _runs.PauseAsync(run.Id, AgentRunOrchestrator.ToolApprovalReason, TestContext.Current.CancellationToken, approvalTool: "write_file");
    await vm.RefreshAsync();

    Assert.False(vm.IsPlanApprovalPause);
    Assert.False(vm.ShowRejectPlanButton);
    Assert.True(vm.ShowNudgeBox); // ordinary park keeps the nudge box
    Assert.Equal("Run_Action_Continue", vm.ContinueLabel);
    vm.Dispose();
}

[Fact]
public async Task RejectPlan_CallsResumeServiceRejectPlanAsync()
{
    var run = await NewPlannedRunAsync();
    var vm = CreateVm(run.Id);
    await _runs.PauseAsync(run.Id, AgentRunOrchestrator.PlanApprovalReason, TestContext.Current.CancellationToken);
    await vm.RefreshAsync();

    await vm.RejectPlanCommand.ExecuteAsync(null);

    await _resume.Received(1).RejectPlanAsync(run.Id, Arg.Any<CancellationToken>());
    vm.Dispose();
}
```

Match `CreateVm`/`NewPlannedRunAsync`/`_runs`/`_resume`/`_loc`'s exact existing names in this file (all confirmed present: `_resume` is `Substitute.For<IAgentRunResumeService>()` at line 23, `_loc` echoes the key per line 32) rather than the illustrative fakes an earlier draft of this plan used.

- [ ] **Step 9: Run, confirm fail → pass**

```bash
dotnet test -- --filter-method "*PlanApproval*"
```

- [ ] **Step 10: Commit**

```bash
git add src/Pia.Wpf/ViewModels/RunProgressViewModel.cs tests/Pia.Wpf.Tests/ViewModels/RunProgressViewModelTests.cs
git commit -m "Add plan-approval projection, Approve/Reject commands, and nudge-box suppression to RunProgressViewModel"
```

### Task 5.3: `RunProgressPanel.xaml` — the Reject button, label rebind, Region-D rebind

**Files:**
- Modify: `src/Pia.Wpf/Controls/Assistant/RunProgressPanel.xaml`

- [ ] **Step 1: Rebind the Continue button's `Content` to `ContinueLabel`**

Current (line 256):

```xml
<ui:Button Content="{loc:Str Run_Action_Continue}" Command="{Binding ContinueCommand}"
```

Change to:

```xml
<ui:Button Content="{Binding ContinueLabel}" Command="{Binding ContinueCommand}"
```

- [ ] **Step 2: Add the Reject button, beside the existing Deny button**

Current Deny button (lines 262-265):

```xml
<ui:Button Content="{loc:Str Run_Action_Deny}" Command="{Binding DeclineToolCommand}"
           Appearance="Secondary" Padding="13,6" Margin="0,0,6,0"
           FontSize="{StaticResource RunMetaSize}" FontWeight="SemiBold"
           Visibility="{Binding ShowDenyButton, Converter={StaticResource BooleanToVisibilityConverter}}" />
```

Add, immediately after it (the two are mutually exclusive by construction — `ShowDenyButton`/`ShowRejectPlanButton` can never both be true, since a run parks on exactly one reason):

```xml
<!-- Reject beside Approve, only on a plan-approval park. Settles the run to Cancelled with no
     re-dispatch — the redirect path is an ordinary chat message afterward, not an in-place edit. -->
<ui:Button Content="{loc:Str Run_Action_RejectPlan}" Command="{Binding RejectPlanCommand}"
           Appearance="Secondary" Padding="13,6" Margin="0,0,6,0"
           FontSize="{StaticResource RunMetaSize}" FontWeight="SemiBold"
           Visibility="{Binding ShowRejectPlanButton, Converter={StaticResource BooleanToVisibilityConverter}}" />
```

- [ ] **Step 3: Rebind Region D's visibility**

Current (line 323):

```xml
Visibility="{Binding ShowContinueButton, Converter={StaticResource BooleanToVisibilityConverter}}">
```

Change to:

```xml
Visibility="{Binding ShowNudgeBox, Converter={StaticResource BooleanToVisibilityConverter}}">
```

- [ ] **Step 4: Build and manually smoke-test**

```bash
dotnet build src/Pia.Wpf/Pia.Wpf.csproj
```

WPF/XAML bindings are not exercised by `dotnet test` — a typo in a binding path fails silently at runtime, not at build time. Run the app (`dotnet run --project src/Pia.Wpf/Pia.Wpf.csproj`), and USE `dotnet test`'s existing `RunProgressViewModelTests` coverage from Task 5.2 as the correctness check for the VM side; the XAML binding paths themselves need a manual check — this plan's Chunk 6 Task 6.4 includes the full manual smoke test once the composer guard is also wired up, so a full plan-approval flow can actually be triggered end-to-end.

- [ ] **Step 5: Commit**

```bash
git add src/Pia.Wpf/Controls/Assistant/RunProgressPanel.xaml
git commit -m "Add the plan-approval Reject button and rebind Continue's label / the nudge box"
```

---

## Chunk 6: Composer guard + final smoke test

### Task 6.1: `ChatSession.PlanApprovalParkActive`

**Files:**
- Modify: `src/Pia.Wpf/ViewModels/Models/ChatSession.cs`

- [ ] **Step 1: Add the stored property, mirroring `ForeignRunActive`'s exact shape**

Current `ForeignRunActive` (lines 168-188, confirmed above) is a plain stored bool with a private setter, a `Changed` event, and a no-op-if-unchanged setter. Add, right after it:

```csharp
    /// <summary>True while this chat's run is parked for plan approval — narrower than
    /// <see cref="ForeignRunActive"/>, which stays false for any park so "continue in chat" stays open.</summary>
    public bool PlanApprovalParkActive { get; private set; }

    /// <summary>Raised when <see cref="PlanApprovalParkActive"/> changes (marshaled to the UI thread by the manager).</summary>
    public event EventHandler<bool>? PlanApprovalParkActiveChanged;

    /// <summary>Sets <see cref="PlanApprovalParkActive"/> and notifies (no-op when unchanged).</summary>
    public void SetPlanApprovalParkActive(bool active)
    {
        if (PlanApprovalParkActive == active)
            return;
        PlanApprovalParkActive = active;
        PlanApprovalParkActiveChanged?.Invoke(this, active);
    }
```

- [ ] **Step 2: Build**

```bash
dotnet build src/Pia.Wpf/Pia.Wpf.csproj
```

No test here — this is a plain data holder with no logic; Task 6.2 tests the recompute logic that sets it.

- [ ] **Step 3: Commit**

```bash
git add src/Pia.Wpf/ViewModels/Models/ChatSession.cs
git commit -m "Add ChatSession.PlanApprovalParkActive"
```

### Task 6.2: `ChatSessionManager` — recompute at the three sites + `StartTurnAsync` backstop

**Files:**
- Modify: `src/Pia.Wpf/ViewModels/Models/ChatSessionManager.cs`
- Test: `tests/Pia.Wpf.Tests/ViewModels/ChatSessionManagerTests.cs` (note: NOT under a `Models` subfolder, despite the source file living in `ViewModels/Models/`)

- [ ] **Step 1: Recompute at `ActivateAsync` (line 551)**

Current:

```csharp
        session.SetForeignRunActive(_executingRuns.IsExecuting(chat.Id));
```

**No change at this site.** `ActivateAsync` does call `RestoreActiveRunAsync` right after this line (`ChatSessionManager.cs:551,570`), but via `.SafeFireAndForget(_logger)` — fire-and-forget, not awaited — so that ordering guarantees nothing about completion order by itself. The real reason no seed is needed here: a freshly constructed `ChatSession`'s `PlanApprovalParkActive` already defaults to `false`, which is the safe/composer-enabled value, and `RestoreActiveRunAsync`'s async backfill (Step 2 below) corrects it once its pool-thread lookup lands — the exact same eventual-consistency window `ForeignRunActive`'s own backfill already accepts (per `ChatSessionManager.cs:634-640`'s own comment). No explicit `SetPlanApprovalParkActive(false)` call needed at this site.

- [ ] **Step 2: Recompute at `RestoreActiveRunAsync`**

Current (lines 641-647, confirmed above):

```csharp
        session.SetForeignRunActive(
            _executingRuns.IsExecuting(chatId)
            || resumable.State is AgentRunState.Planning or AgentRunState.Running or AgentRunState.Verifying
                or AgentRunState.WaitingForChildren);
```

Add, right after it:

```csharp
        // Narrower than ForeignRunActive above: only a plan-approval park closes the composer's "type
        // instead of deciding" path (see ChatSession.PlanApprovalParkActive's doc comment).
        session.SetPlanApprovalParkActive(
            resumable.State == AgentRunState.WaitingForInput
            && RunPauseEnvelope.ReadReason(resumable) == AgentRunOrchestrator.PlanApprovalReason);
```

- [ ] **Step 3: Recompute at `OnAgentRunChanged`**

This handler (lines 237-328, confirmed above) reasons ONLY from `e.State` — it deliberately does no store read per event (a documented choice: "this handler reasons purely from `e.State`, no I/O"). `PlanApprovalParkActive` needs the pause REASON, which the event does not carry. Rather than adding an I/O read into this hot per-event handler (a departure from its established no-I/O discipline), take the cheaper, correct path: a `RunChanged(WaitingForInput)` event fires whenever ANY park happens (including plan-approval, via `SafePause` in `ParkForPlanApprovalAsync` from Chunk 2), and a `RunChanged(Running)`/`RunChanged(Cancelled)` event fires whenever it resolves (Approve/Reject/any resume). So:

- On `e.State == AgentRunState.WaitingForInput`: do a ONE-TIME read of the run's reason (an occasional pause-transition event, not a per-step hot path — unlike the `executing`-only recompute above it, which fires every step).
- On any OTHER `e.State`: the run is not parked for plan approval by definition — no read needed, `false` unconditionally.

Compute this ONCE, OUTSIDE the `foreach (var session in _allSessions)` loop — not per-session inside it. Two reasons: (1) the answer is the same for whichever session holds this run, so recomputing it once and reusing it avoids redundant reads; (2) `IAgentRunService` is a synchronous, lock-holding store (`AgentRunService.GetAsync` takes `lock (_gate)` for its whole body) — this file's OWN existing caution around it (`RestoreActiveRunAsync` wraps the equivalent call in `Task.Run(...)`, and `ChatSessionManager.cs:1017-1022`'s `ReadClarificationParkReasonAsync` does the same) is "a live headless run holding that lock must never stall the UI." Doing this fetch inside the `foreach` would put an `await` mid-loop, reopening exactly the cross-event interleaving hazard hoisting avoids. Add, right before the `foreach (var session in _allSessions)` loop:

```csharp
                var isPlanApprovalPark = e.State == AgentRunState.WaitingForInput
                    && await Task.Run(async () =>
                    {
                        var run = await _agentRunService.GetAsync(e.RunId).ConfigureAwait(false);
                        return run is not null && RunPauseEnvelope.ReadReason(run) == AgentRunOrchestrator.PlanApprovalReason;
                    }).ConfigureAwait(false);
```

Then, inside the `foreach` loop, right after the existing `session.SetForeignRunActive(foreign);` line:

```csharp
                    if (holdsThisRun)
                        session.SetPlanApprovalParkActive(isPlanApprovalPark);
```

This requires the enclosing `_syncContext.Post(_ => { ... }, null)` lambda to become `async` (`_syncContext.Post(async _ => { ... }, null)`) so the two `await`s above compile — the method's existing `try`/`catch` already spans the whole body, so this is exception-safe as written; there is no ordering hazard today, but that safety rests on `GetAsync`'s current synchronous-under-lock implementation, which this change does not alter.

- [ ] **Step 4: Add the `StartTurnAsync` backstop guard — a LIVE read, per the spec, not the cached flag**

The design spec is explicit that this backstop should use "the same two-part read `IsPlanApprovalPause` uses" specifically because a live re-read composes correctly with Reject without depending on the cached flag's async propagation delay (`ChatSession.PlanApprovalParkActive` is updated by `OnAgentRunChanged`, which is itself async and could theoretically lag a fast Approve→immediately-type-again sequence). The cached flag is the right tool for the three cheap, synchronous composer-level `CanExecute`/early-return checks (Task 6.3) — an async DB read there would be the wrong shape for a `CanExecute` predicate — but `StartTurnAsync` is already `async` and already does a live read for the SAME kind of check one line later (`TryAnswerParkedRunAsync` → `ReadClarificationParkReasonAsync`, `ChatSessionManager.cs:1017-1031`, which today only recognizes `NeedsGoalReason`/`NeedsInputReason`). Add a sibling live check rather than relying on the cached flag here.

Current (line 667):

```csharp
        if (await TryAnswerParkedRunAsync(session, userText, attachment, regenerationInstruction))
            return;
```

Change to:

```csharp
        // Backstop (defense in depth, same shape TryAnswerParkedRunAsync's check is below) — a REFUSAL, not
        // an answer: typing over a pending plan must never be read as approving/rejecting it. Live read, not
        // the cached PlanApprovalParkActive flag, matching TryAnswerParkedRunAsync's own live check.
        if (session.ActiveRunId is { } activeRunId && await IsPlanApprovalParkedAsync(activeRunId))
        {
            _logger.LogInformation(
                "Chat {ChatId}: refusing a new turn while a plan-approval park is active", session.Id);
            return;
        }

        if (await TryAnswerParkedRunAsync(session, userText, attachment, regenerationInstruction))
            return;
```

Add a small sibling helper beside `ReadClarificationParkReasonAsync`, following its exact shape (`ChatSessionManager.cs:1016-1034`):

```csharp
    /// <summary>Live check for the StartTurnAsync backstop: is this run currently parked specifically for
    /// plan approval? A sibling of <see cref="ReadClarificationParkReasonAsync"/>, not a widened version of
    /// it — that method's return type is "the recognized clarification reason, or null", and plan-approval
    /// is a different park with different resume semantics (no answer text applies to it at all).</summary>
    private async Task<bool> IsPlanApprovalParkedAsync(Guid runId)
    {
        AgentRun? run;
        try
        {
            run = await Task.Run(() => _agentRunService.GetAsync(runId));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read run {RunId} while checking for a plan-approval park", runId);
            return false;
        }

        return run is { State: AgentRunState.WaitingForInput }
            && RunPauseEnvelope.ReadReason(run) == AgentRunOrchestrator.PlanApprovalReason;
    }
```

This composes correctly with Reject for the same reason `ReadClarificationParkReasonAsync` already does: it re-reads the run's *current* state on every call rather than caching, so once `RejectPlanAsync`'s CAS lands, the very next `StartTurnAsync` call sees `Cancelled` and proceeds normally.

- [ ] **Step 5: Write a failing test for the `StartTurnAsync` backstop**

This file already has an `AttachParkedRun(session, reason, state)` helper (line 1624 — "writes a real pause envelope rather than stubbing a 'parked' bool, since nothing in production reads such a bool") and a directly analogous existing test, `StartTurnAsync_RunParkedAtBudget_StartsAnOrdinaryTurn_AndNeverResumes` (line 1734), which proves an unanswerable park (`"step-cap"`) lets an ordinary turn start — asserting non-vacuity via `_personas.Received(1).ResolveActiveAsync(...)`. The new plan-approval case is the mirror image: it must NOT let that turn start. Use the same non-vacuity technique, inverted:

```csharp
[Fact]
public async Task StartTurnAsync_RunParkedForPlanApproval_RefusesTheTurn_WithoutEverResolvingSetup()
{
    var sut = CreateResumingSut();
    var session = sut.GetOrCreateActiveForNewChat();
    AttachParkedRun(session, AgentRunOrchestrator.PlanApprovalReason);

    await sut.StartTurnAsync(session, "meanwhile, what is the weather", null);

    // Mechanism-specific, not just a message count: if the turn had proceeded (even via
    // TryAnswerParkedRunAsync's unrelated no-op path), persona/provider resolution would have run.
    await _personas.DidNotReceive().ResolveActiveAsync(Arg.Any<WindowMode>(), Arg.Any<UserOperatingMode>());
    await _resumeService.DidNotReceive().ResumeAsync(
        Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    // Only the park's own envelope exists — no user/assistant messages were added by this call.
    Assert.Empty(session.Messages);
}
```

- [ ] **Step 6: Write a failing test for `RestoreActiveRunAsync`'s recompute**

Mirroring the existing `RestoreActiveRunAsync_ParkedRun_DoesNotMarkItForeign` `[Theory]` (line 322) and its `Run(chatId, state, createdAt)` helper (line 246) — extend a local `AgentRun` construction to carry the plan-approval `ExtraJson` (`Run(...)` itself has no `ExtraJson` parameter, so build the run inline for this reason, matching the shape `AttachParkedRun` already uses at line 1633-1640):

```csharp
[Fact]
public async Task RestoreActiveRunAsync_SetsPlanApprovalParkActive_ForAPlanApprovalPark()
{
    var chatId = Guid.NewGuid();
    var parked = new AgentRun
    {
        Id = Guid.NewGuid(), ChatId = chatId, RunShape = RunShape.Planned,
        State = AgentRunState.WaitingForInput, CreatedAt = DateTime.UtcNow.AddMinutes(-1),
        ExtraJson = PauseEnvelope(AgentRunOrchestrator.PlanApprovalReason),
    };
    _chatService.GetAsync(chatId, Arg.Any<CancellationToken>()).Returns(StoredChat(chatId));
    _runService.GetByChatAsync(chatId, Arg.Any<CancellationToken>()).Returns(new List<AgentRun> { parked });

    var sut = CreateSut();
    var session = await sut.ActivateAsync(chatId);
    session!.SetActiveRun(null);

    await sut.RestoreActiveRunAsync(session);

    Assert.True(session.PlanApprovalParkActive);
    Assert.False(session.ForeignRunActive); // still the parked "continue in chat" shape for THIS flag
}

[Fact]
public async Task RestoreActiveRunAsync_LeavesPlanApprovalParkActiveFalse_ForAnOrdinaryPark()
{
    var chatId = Guid.NewGuid();
    var parked = Run(chatId, AgentRunState.WaitingForInput, DateTime.UtcNow.AddMinutes(-1)); // no ExtraJson at all
    _chatService.GetAsync(chatId, Arg.Any<CancellationToken>()).Returns(StoredChat(chatId));
    _runService.GetByChatAsync(chatId, Arg.Any<CancellationToken>()).Returns(new List<AgentRun> { parked });

    var sut = CreateSut();
    var session = await sut.ActivateAsync(chatId);
    session!.SetActiveRun(null);

    await sut.RestoreActiveRunAsync(session);

    Assert.False(session.PlanApprovalParkActive);
}
```

- [ ] **Step 7: Write a failing test for the `OnAgentRunChanged` recompute — set on park, clear on resolve**

Mirroring `RunChanged_ToPaused_ClearsTheForeignFlag` (line 342)'s exact poll-with-timeout shape, since `AgentRunService` raises `RunChanged` from a pool thread and the manager marshals the flip via `_syncContext.Post`:

```csharp
[Fact]
public async Task RunChanged_ToWaitingForInput_SetsPlanApprovalParkActive_ThenClearsItOnCancelled()
{
    var chatId = Guid.NewGuid();
    var running = Run(chatId, AgentRunState.Running, DateTime.UtcNow.AddMinutes(-1));
    _chatService.GetAsync(chatId, Arg.Any<CancellationToken>()).Returns(StoredChat(chatId));
    _runService.GetByChatAsync(chatId, Arg.Any<CancellationToken>()).Returns(new List<AgentRun> { running });

    var sut = CreateSut();
    var session = await sut.ActivateAsync(chatId);
    session!.SetActiveRun(null);
    await sut.RestoreActiveRunAsync(session);
    Assert.False(session.PlanApprovalParkActive);

    // The live read this event triggers (Task 6.2 Step 3) needs GetAsync(running.Id) to answer with the
    // now-parked row — stub it the same way AttachParkedRun does.
    _runService.GetAsync(running.Id, Arg.Any<CancellationToken>()).Returns(new AgentRun
    {
        Id = running.Id, ChatId = chatId, RunShape = RunShape.Planned, State = AgentRunState.WaitingForInput,
        ExtraJson = PauseEnvelope(AgentRunOrchestrator.PlanApprovalReason),
    });
    _runService.RunChanged += Raise.EventWith(new AgentRunChangedEventArgs(running.Id, AgentRunState.WaitingForInput));

    for (var i = 0; i < 200 && !session.PlanApprovalParkActive; i++)
        await Task.Delay(10, TestContext.Current.CancellationToken);
    Assert.True(session.PlanApprovalParkActive);

    // Reject lands: Cancelled.
    _runService.RunChanged += Raise.EventWith(new AgentRunChangedEventArgs(running.Id, AgentRunState.Cancelled));

    for (var i = 0; i < 200 && session.PlanApprovalParkActive; i++)
        await Task.Delay(10, TestContext.Current.CancellationToken);
    Assert.False(session.PlanApprovalParkActive);
}
```

- [ ] **Step 8: Run, confirm fail → pass**

```bash
dotnet test -- --filter-method "*PlanApprovalParkActive*"
```

- [ ] **Step 9: Run the full gate**

```bash
dotnet test
```

- [ ] **Step 10: Commit**

```bash
git add src/Pia.Wpf/ViewModels/Models/ChatSessionManager.cs tests/Pia.Wpf.Tests/ViewModels/ChatSessionManagerTests.cs
git commit -m "Track PlanApprovalParkActive and refuse a new turn in StartTurnAsync while it holds"
```

### Task 6.3: `AssistantViewModel` guards + composer hint

**Files:**
- Modify: `src/Pia.Wpf/ViewModels/AssistantViewModel.cs`
- Modify: `src/Pia.Wpf/Views/AssistantView.xaml`
- Modify: `src/Pia.Wpf/Resources/Strings/ViewStrings.resx` + `.de.resx` + `.fr.resx` (one new key)
- Test: `tests/Pia.Wpf.Tests/ViewModels/AssistantViewModelLeverTests.cs` — there is no single `AssistantViewModelTests.cs`; tests are split by concern across several files, and this one already covers `ForeignRunActive`/`SendMessageCommand`/the session-attach wiring with the exact fixtures this task needs (`CreateSut()`, `SessionWithTranscript(...)`, `Activate(...)`).

- [ ] **Step 1: Add the composer-hint loc key**

`ViewStrings.resx`:
```xml
<data name="Assistant_PlanApprovalActive_Hint" xml:space="preserve"><value>Approve or reject the proposed plan before sending another message.</value></data>
```

`ViewStrings.de.resx`:
```xml
<data name="Assistant_PlanApprovalActive_Hint" xml:space="preserve"><value>Genehmigen oder lehnen Sie den vorgeschlagenen Plan ab, bevor Sie eine weitere Nachricht senden.</value></data>
```

`ViewStrings.fr.resx`:
```xml
<data name="Assistant_PlanApprovalActive_Hint" xml:space="preserve"><value>Approuvez ou rejetez le plan proposé avant d'envoyer un autre message.</value></data>
```

Regenerate `Designer.cs` (manual VS step, as in prior tasks) and run the parity test:

```bash
dotnet test -- --filter-method "*AllTranslations_MustBeComplete*"
```

- [ ] **Step 2: Add a fully-wired `PlanApprovalParkActive` `[ObservableProperty]`, mirroring `ForeignRunActive` exactly**

`ForeignRunActive` (`AssistantViewModel.cs:98-99`) is a full `[ObservableProperty] private bool _foreignRunActive;` kept in sync at FOUR sites, not a computed passthrough — a bare `_chatSessionManager.ActiveSession?.PlanApprovalParkActive ?? false` getter would be `private`/non-notifying and the XAML binding in Step 1 above would silently render nothing bound to it (the exact class of bug this plan has flagged elsewhere). Add the property and all four wiring points:

```csharp
    /// <summary>True while this chat's active run is parked for plan approval — narrower than
    /// <see cref="ForeignRunActive"/>. Also drives the composer hint line.</summary>
    [ObservableProperty]
    private bool _planApprovalParkActive;
```

In `AttachToActiveSession` (`AssistantViewModel.cs:408-438`), add the unsubscribe (beside line 417):

```csharp
            prev.PlanApprovalParkActiveChanged -= OnPlanApprovalParkActiveChanged;
```

the subscribe (beside line 426):

```csharp
        session.PlanApprovalParkActiveChanged += OnPlanApprovalParkActiveChanged;
```

and the late-attach synchronous read (beside line 428):

```csharp
        PlanApprovalParkActive = session.PlanApprovalParkActive;
```

Add the UI-thread-marshaling handler, mirroring `OnForeignRunActiveChanged` (`AssistantViewModel.cs:447-448`):

```csharp
    private void OnPlanApprovalParkActiveChanged(object? sender, bool active) =>
        _uiDispatcher.Post(() => PlanApprovalParkActive = active);
```

- [ ] **Step 2b: Add `PlanApprovalParkActive` to the manual `OnPropertyChanged` allowlist**

`SendMessageCommand`/`RunInBackgroundCommand` are hand-constructed (no `[NotifyCanExecuteChangedFor]`), so their `CanExecute` re-evaluation is driven entirely by the manual dispatcher at `AssistantViewModel.cs:712-719`. Without this step, `CanExecuteSendMessage()`'s new `&& !PlanApprovalParkActive` term (Step 3 below) is logically correct but WPF never re-queries it at the moment a park begins or resolves — the Send button could stay stuck in a stale enabled/disabled state. Current:

```csharp
        if (e.PropertyName is nameof(InputText) or nameof(IsStreaming) or nameof(PendingAttachment)
            or nameof(ForeignRunActive))
        {
            SendMessageCommand.NotifyCanExecuteChanged();
            RunInBackgroundCommand.NotifyCanExecuteChanged();
        }
```

Change to:

```csharp
        if (e.PropertyName is nameof(InputText) or nameof(IsStreaming) or nameof(PendingAttachment)
            or nameof(ForeignRunActive) or nameof(PlanApprovalParkActive))
        {
            SendMessageCommand.NotifyCanExecuteChanged();
            RunInBackgroundCommand.NotifyCanExecuteChanged();
        }
```

- [ ] **Step 3: Guard `CanExecuteSendMessage`**

Current (lines 852-854):

```csharp
    private bool CanExecuteSendMessage() =>
        !IsStreaming && !ForeignRunActive
        && (!string.IsNullOrWhiteSpace(InputText) || PendingAttachment is not null);
```

Change to:

```csharp
    private bool CanExecuteSendMessage() =>
        !IsStreaming && !ForeignRunActive && !PlanApprovalParkActive
        && (!string.IsNullOrWhiteSpace(InputText) || PendingAttachment is not null);
```

(`CanExecuteRunInBackground` composes on this directly per the earlier confirmed research, so it is automatically covered — no separate edit.)

- [ ] **Step 4: Guard `RegenerateCore`**

Current (line 1140):

```csharp
        if (message is null || IsStreaming || ForeignRunActive) return;
```

Change to:

```csharp
        if (message is null || IsStreaming || ForeignRunActive || PlanApprovalParkActive) return;
```

This must run BEFORE the message-truncation logic later in the same method (lines 1149-1156) — confirm the edit lands on this early-return line, not after it.

- [ ] **Step 5: Guard `SwitchToAgent`**

Current (line 1512):

```csharp
        if (IsStreaming || ForeignRunActive)
            return;
```

Change to:

```csharp
        if (IsStreaming || ForeignRunActive || PlanApprovalParkActive)
            return;
```

- [ ] **Step 6: Add the composer hint `TextBlock` in `AssistantView.xaml`**

Find the existing `Assistant_BackgroundRunActive_Hint` `TextBlock` (`AssistantView.xaml:569-574`, confirmed above):

```xml
<TextBlock Text="{loc:Str Assistant_BackgroundRunActive_Hint}"
           Margin="2,6,0,0"
           FontSize="12"
           TextWrapping="Wrap"
           Foreground="{DynamicResource TextSubtleBrush}"
           Visibility="{Binding ForeignRunActive, Converter={StaticResource BooleanToVisibilityConverter}}" />
```

Add a SIBLING `TextBlock` right after it — a separate control, not a shared one, since the two hints' text and visibility condition are both different:

```xml
<TextBlock Text="{loc:Str Assistant_PlanApprovalActive_Hint}"
           Margin="2,6,0,0"
           FontSize="12"
           TextWrapping="Wrap"
           Foreground="{DynamicResource TextSubtleBrush}"
           Visibility="{Binding PlanApprovalParkActive, Converter={StaticResource BooleanToVisibilityConverter}}" />
```

Confirm `PlanApprovalParkActive` is a public bindable property on `AssistantViewModel` (Step 2 above) before wiring this binding — a private property is invisible to XAML and this binding would silently render nothing, the exact class of bug this plan has been careful to avoid throughout.

- [ ] **Step 7: Write failing tests for the three guards**

`AssistantViewModelLeverTests.cs` already has the exact fixtures needed — `CreateSut()`, a settable `vm.ForeignRunActive`/`vm.PlanApprovalParkActive` (both real `[ObservableProperty]`s per Step 2), and `SessionWithTranscript(foreignRunActive:)`/`Activate(session)` for the attach-wiring tests. Add a `planApprovalParkActive` parameter to `SessionWithTranscript` (mirroring its existing `foreignRunActive` parameter) so both flags can be tested independently:

```csharp
    private static ChatSession SessionWithTranscript(bool foreignRunActive = false, bool planApprovalParkActive = false)
    {
        var session = new ChatSession(/* ...unchanged... */);
        session.Messages.Add(/* ...unchanged... */);
        if (foreignRunActive) session.SetForeignRunActive(true);
        if (planApprovalParkActive) session.SetPlanApprovalParkActive(true);
        return session;
    }
```

Then, mirroring `CanSend_IsFalse_WhileAForeignRunIsExecuting`/`CanSend_ReEnables_WhenTheForeignRunStops`/`AttachingASessionWithAForeignRun_SeedsTheFlagOntoTheViewModel` exactly:

```csharp
[Fact]
public void CanSend_IsFalse_WhilePlanApprovalParkIsActive()
{
    var vm = CreateSut();
    vm.InputText = "hello";
    Assert.True(vm.SendMessageCommand.CanExecute(null));

    vm.PlanApprovalParkActive = true;

    Assert.False(vm.SendMessageCommand.CanExecute(null));
}

[Fact]
public void CanSend_ReEnables_WhenThePlanApprovalParkResolves()
{
    var vm = CreateSut();
    vm.InputText = "hello";
    vm.PlanApprovalParkActive = true;
    Assert.False(vm.SendMessageCommand.CanExecute(null));

    vm.PlanApprovalParkActive = false;

    Assert.True(vm.SendMessageCommand.CanExecute(null));
}

[Fact]
public void AttachingASessionParkedForPlanApproval_SeedsTheFlagOntoTheViewModel()
{
    var vm = CreateSut();
    vm.InputText = "hello";
    Assert.True(vm.SendMessageCommand.CanExecute(null));

    Activate(SessionWithTranscript(planApprovalParkActive: true));

    Assert.True(vm.PlanApprovalParkActive);
    Assert.False(vm.SendMessageCommand.CanExecute(null));
}

[Fact]
public void Dispose_StopsReactingToPlanApprovalParkActiveChanged()
{
    var vm = CreateSut();
    var session = SessionWithTranscript();
    Activate(session);
    Assert.False(vm.PlanApprovalParkActive);

    vm.Dispose();
    session.SetPlanApprovalParkActive(true); // false -> true, so this really does raise

    Assert.False(vm.PlanApprovalParkActive);
}
```

`RegenerateCore`/`SwitchToAgent`'s guards (Steps 4-5) have no `CanExecute` predicate — they no-op inline instead (per this plan's earlier research into those methods). Test them the same way, driving a real `AssistantMessage`/`AgentModeSuggestion` through the command and asserting nothing happened:

```csharp
[Fact]
public async Task RegenerateMessageCommand_DoesNothing_WhilePlanApprovalParkActive()
{
    var vm = CreateSut();
    var userMsg = new AssistantMessage(Microsoft.Extensions.AI.ChatRole.User, "goal");
    var assistantMsg = new AssistantMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "answer");
    vm.Messages.Add(userMsg);
    vm.Messages.Add(assistantMsg);
    vm.PlanApprovalParkActive = true;

    await vm.RegenerateMessageCommand.ExecuteAsync(assistantMsg);

    Assert.Equal(2, vm.Messages.Count); // nothing truncated
}

[Fact]
public async Task SwitchToAgentCommand_DoesNothing_WhilePlanApprovalParkActive()
{
    var vm = CreateSut();
    Assert.False(vm.AgentModeEnabled); // confirm the starting state before relying on it below
    vm.PlanApprovalParkActive = true;

    await vm.SwitchToAgentCommand.ExecuteAsync(new AgentModeSuggestion { Goal = "do the thing" });

    // SwitchToAgent's very next line after the guard is "AgentModeEnabled = true" — it staying false
    // proves the method returned at the guard rather than proceeding.
    Assert.False(vm.AgentModeEnabled);
}
```

Match `CreateSut()`'s exact construction against this file's existing usage (it's used unchanged throughout) — no new setup is needed for these two tests beyond what `CreateSut()` already provides.

- [ ] **Step 8: Run, confirm fail → pass**

```bash
dotnet test -- --filter-method "*PlanApprovalParkActive*"
```

- [ ] **Step 9: Run the full gate**

```bash
dotnet test
```

Expected: `failed: 0`.

- [ ] **Step 10: Commit**

```bash
git add src/Pia.Wpf/ViewModels/AssistantViewModel.cs src/Pia.Wpf/Views/AssistantView.xaml src/Pia.Wpf/Resources/Strings/ViewStrings.resx src/Pia.Wpf/Resources/Strings/ViewStrings.de.resx src/Pia.Wpf/Resources/Strings/ViewStrings.fr.resx src/Pia.Wpf/Resources/Strings/ViewStrings.Designer.cs tests/Pia.Wpf.Tests/ViewModels/AssistantViewModelLeverTests.cs
git commit -m "Block Send/Regenerate/SwitchToAgent while a plan-approval park is active"
```

### Task 6.4: Zero-warning check + manual end-to-end smoke test

**Files:** none (verification only)

- [ ] **Step 1: Rebuild Debug and check for zero warnings**

Per `CLAUDE.md`'s Zero-Warning Policy — a rebuild, not an incremental build (an incremental build skips re-emitting warnings from projects it did not recompile):

```bash
dotnet build -t:Rebuild -v:n
```

Read the `N Warning(s)` line off MSBuild's summary (not a grep count — at `-v:n` every warning prints twice, inline and in the summary). Expected: `0 Warning(s)`, `0 Error(s)`.

- [ ] **Step 2: Rebuild Release and check for zero warnings**

```bash
dotnet build -t:Rebuild -c Release -v:n
```

Expected: `0 Warning(s)`, `0 Error(s)`.

- [ ] **Step 3: Run the full test gate one final time**

```bash
dotnet test
```

Expected: `failed: 0`.

- [ ] **Step 4: Manual smoke test — the golden path**

Run the app:

```bash
dotnet run --project src/Pia.Wpf/Pia.Wpf.csproj
```

1. Open the Assistant view, flip the Chat/Agent lever to Agent.
2. Send a goal specific enough to produce 3+ steps (e.g. "research topic X, write a summary file, and create a todo for follow-up" — three distinct actions).
3. Confirm the run-progress panel shows the plan skeleton, then the Approve/Reject card with the plan's step titles, and that the panel's Continue button now reads "Approve".
4. Confirm the composer hint appears and Send/Regenerate/the Agent-mode suggestion chip (if one is showing) are all inert while the card is up.
5. Click **Reject**. Confirm: the run panel clears/updates to show the run as no longer active, a "plan rejected" note appears in the chat transcript, and the composer's Send button and hint both return to normal.
6. Send a new message in the same chat (Agent mode still on) with a goal that again produces 3+ steps. Confirm the card appears again.
7. Click **Approve**. Confirm the run drains its steps normally (no second approval pause appears once steps start executing, even if a step fails and the run replans).
8. Repeat step 2 with a goal that produces only 1-2 steps. Confirm the run executes immediately with NO approval pause.
9. Switch the lever to Chat mode (or use a no-tools persona) and confirm an ordinary chat turn is entirely unaffected.
10. If a background/scheduled run can be triggered in this environment (per the `/schedule` skill or equivalent), trigger one with a goal that would produce 3+ steps and confirm it executes straight through with no approval pause and no Flow notification asking for plan approval.

- [ ] **Step 5: Record the smoke-test result**

This step has no code artifact — note the outcome (pass/fail per numbered item above) back to whoever requested this plan; do not mark the branch done until every item above has been walked through once.

---

## Final: clear the branch for merge

Per `CLAUDE.md`'s Git Workflow section: "Before treating a feature branch as done, clear the Zero-Warning Policy above" — already done in Task 6.4 Steps 1-2. No further steps beyond what Chunk 6 already covers; this plan does not include a merge/PR step, since that is a decision for whoever is running it, not something to automate.
