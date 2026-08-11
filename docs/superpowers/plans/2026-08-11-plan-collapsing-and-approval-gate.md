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

If `tests/Pia.Wpf.Tests/Services/AgentPlannerTests.cs` does not exist, create it following this repo's existing test-fake conventions for `AgentPlanner` (grep `tests/Pia.Wpf.Tests/Services/*.cs` for an existing fake `IAiClientService`/`ISettingsService` construction pattern to reuse — several files listed in the interface-implementer survey above construct `AgentPlanner` directly). Add:

```csharp
[Fact]
public void BuildPlanMessages_IncludesGroupByFileRule()
{
    // Use reflection to invoke the private static BuildPlanMessages the same way existing
    // AgentPlanner tests already do (grep the file for `GetMethod("BuildPlanMessages"` — if a
    // reflection helper already exists in this test class, reuse it instead of writing a new one).
    var method = typeof(AgentPlanner).GetMethod("BuildPlanMessages",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    var messages = (List<ChatMessage>)method.Invoke(null,
        new object?[] { "goal", TestPersonas.Default, false, null, Array.Empty<Persona>(), null })!;

    var systemText = messages[0].Text;
    Assert.Contains("Group by logical change, not by file", systemText);
}
```

Adjust the invocation's parameter list to match `BuildPlanMessages`'s actual signature exactly (`goal, persona, firm, analysis, roster, grounding` per `AgentPlanner.cs:774-776`) and swap `TestPersonas.Default` for whatever persona-construction helper the existing test file already uses (grep for `new Persona` in the same test directory).

- [ ] **Step 5: Run the test, confirm it fails before the edit / passes after**

```bash
dotnet test --filter "FullyQualifiedName~AgentPlannerTests.BuildPlanMessages_IncludesGroupByFileRule"
```

Run this once BEFORE Step 2's edit (expect FAIL — text not present) and once AFTER (expect PASS). If the MTP runner rejects the `--filter` form used here, fall back to the full gate (`dotnet test`) to confirm — do not invent an unverified filter flag.

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

- [ ] **Step 3: Write a failing test, mirroring Task 1.1's**

```csharp
[Fact]
public void BuildReplanMessages_IncludesGroupByFileRule()
{
    var method = typeof(AgentPlanner).GetMethod("BuildReplanMessages",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    var ctx = new RunContext("goal", RunProfile.Interactive);
    var messages = (List<ChatMessage>)method.Invoke(null,
        new object?[] { ctx, null, TestPersonas.Default, false, Array.Empty<Persona>() })!;

    Assert.Contains("Group by logical change, not by file", messages[0].Text);
}
```

Adjust the parameter list to `BuildReplanMessages`'s actual signature (`ctx, failure, persona, firm, roster` per `AgentPlanner.cs:816-818`) and confirm `RunContext`'s actual constructor before using it — grep `class RunContext` if the two-argument form shown here does not match.

- [ ] **Step 4: Run, confirm fail → pass**

```bash
dotnet test --filter "FullyQualifiedName~AgentPlannerTests.BuildReplanMessages_IncludesGroupByFileRule"
```

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

- [ ] **Step 5: Write a test asserting the default**

Add to `tests/Pia.Wpf.Tests/Services/AgentRunOrchestratorArmTests.cs` (or the nearest existing test file with a minimal `IAgentTurnExecutor` fake that does not override `SupportsPlanApproval`):

```csharp
[Fact]
public void SupportsPlanApproval_DefaultsFalseForAnExecutorThatDoesNotOverrideIt()
{
    IAgentTurnExecutor executor = new StubExecutor(); // or the file's existing minimal fake
    Assert.False(executor.SupportsPlanApproval);
}
```

If `StubExecutor` (or the chosen fake) already overrides members in a way that would need touching, instead add a tiny local fake in the same test file rather than editing a fake shared by other tests.

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
dotnet test --filter "FullyQualifiedName~SupportsPlanApproval"
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

- [ ] **Step 3: Write a failing test for the gate firing**

Find or add a test in `AgentRunOrchestratorArmTests.cs` (this file already tests "arm"/branching behavior per its name) using its existing `StubExecutor` fake, extended to override `SupportsPlanApproval => true`, and an `IAgentPlanner` fake/mock that returns a 3-step plan. Pattern to follow (adapt names to the file's actual existing fakes/mocks — grep the file for how it constructs `AgentRunOrchestrator` and stubs `_planner.PlanAsync` before writing this):

```csharp
[Fact]
public async Task RunAsync_ParksForApproval_WhenFirstPlanHasThreeOrMoreSteps_AndExecutorSupportsIt()
{
    var steps = new[]
    {
        new PlanStepArg("Step 1", "Do the first thing"),
        new PlanStepArg("Step 2", "Do the second thing"),
        new PlanStepArg("Step 3", "Do the third thing"),
    };
    // ... construct AgentRunOrchestrator with a fake IAgentPlanner whose PlanAsync returns
    // new PlanResult(BuildStepsFromArgs(steps), false, usage: null) and a StubExecutor with
    // SupportsPlanApproval => true ...

    await orchestrator.RunAsync(run, executor, persona, provider, RunProfile.Interactive, CancellationToken.None);

    var updated = await runService.GetAsync(run.Id);
    Assert.Equal(AgentRunState.WaitingForInput, updated!.State);
    Assert.Equal(AgentRunOrchestrator.PlanApprovalReason, RunPauseEnvelope.ReadReason(updated));
}
```

- [ ] **Step 4: Write a failing test for the gate NOT firing below threshold**

```csharp
[Fact]
public async Task RunAsync_DoesNotParkForApproval_WhenFirstPlanHasFewerThanThreeSteps()
{
    // Same setup, but the fake planner returns a 2-step plan.
    await orchestrator.RunAsync(run, executor, persona, provider, RunProfile.Interactive, CancellationToken.None);

    var updated = await runService.GetAsync(run.Id);
    Assert.NotEqual(AgentRunOrchestrator.PlanApprovalReason, RunPauseEnvelope.ReadReason(updated!));
}
```

- [ ] **Step 5: Write a failing test for the gate NOT firing when the executor doesn't support it**

```csharp
[Fact]
public async Task RunAsync_DoesNotParkForApproval_WhenExecutorDoesNotSupportPlanApproval()
{
    // Same 3-step plan, but StubExecutor.SupportsPlanApproval => false (the default — a headless-shaped fake).
    await orchestrator.RunAsync(run, executor, persona, provider, RunProfile.Interactive, CancellationToken.None);

    var updated = await runService.GetAsync(run.Id);
    Assert.NotEqual(AgentRunOrchestrator.PlanApprovalReason, RunPauseEnvelope.ReadReason(updated!));
}
```

- [ ] **Step 6: Write a failing test for the gate NOT re-firing on a replan-after-failure**

This is the most important regression test — it locks in "first plan only". Construct a run whose first plan has 3 steps (so it WOULD gate — but the executor drains automatically past a park only if you call `RunAsync` with `resume: true` after simulating an Approve, OR more directly: give the executor `SupportsPlanApproval => false` for this specific test so the run drains straight through, make step 1 fail, and assert the resulting REPLAN (which `TryReplanAfterFailureAsync` produces, still ≥3 steps) does NOT re-park):

```csharp
[Fact]
public async Task RunAsync_DoesNotReparkForApproval_OnAReplanAfterStepFailure()
{
    // Executor: SupportsPlanApproval => false for THIS test (isolates the assertion to the replan path,
    // not the first-plan gate re-testing itself) — or, if the test harness makes it easy, SupportsPlanApproval
    // => true and drive the run through Approve first, then fail step 1, then assert the replan doesn't
    // re-park. Prefer whichever shape reuses more of this file's existing replan-test scaffolding
    // (grep for an existing "...ReplansAfterStepFailure" test and mirror its executor/planner fakes).
    // The fake IAgentPlanner's ReplanAsync should return a fresh 3-step plan.

    await orchestrator.RunAsync(run, executor, persona, provider, RunProfile.Interactive, CancellationToken.None);

    var updated = await runService.GetAsync(run.Id);
    Assert.NotEqual(AgentRunOrchestrator.PlanApprovalReason, RunPauseEnvelope.ReadReason(updated!));
}
```

- [ ] **Step 7: Run all five new tests, confirm each fails before the Step 2 wiring and passes after**

```bash
dotnet test --filter "FullyQualifiedName~RunAsync_ParksForApproval|FullyQualifiedName~RunAsync_DoesNotPark|FullyQualifiedName~RunAsync_DoesNotRepark"
```

- [ ] **Step 8: Run the full gate**

```bash
dotnet test
```

Expected: `failed: 0`.

- [ ] **Step 9: Commit**

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

Per this project's convention, `ViewStrings.Designer.cs` is generated by the `PublicResXFileCodeGenerator` Visual Studio single-file generator, NOT by any MSBuild target — `dotnet build`/`dotnet test` will silently NOT regenerate it. Open `ViewStrings.resx` in Visual Studio and save it (this re-invokes the custom tool), or right-click it in Solution Explorer → "Run Custom Tool". Confirm afterward that `ViewStrings.Designer.cs` now contains `Run_Activity_PlanApproval`/`Flow_Run_PlanApproval` properties (grep the generated file to confirm before moving on — do NOT hand-add these properties yourself; that is exactly the drift the project's memory of this file warns against).

- [ ] **Step 4: Run the localization parity test**

```bash
dotnet test --filter "FullyQualifiedName~LocalizationTests.AllTranslations_MustBeComplete"
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

- [ ] **Step 2: Write a failing test**

```csharp
[Fact]
public void DescribePause_ReturnsPlanApprovalWording_ForThePlanApprovalReason()
{
    var run = /* build a fake AgentRun, State=WaitingForInput, ExtraJson={"paused":true,"reason":"plan-approval"} —
                 mirror whatever helper this test file already uses to build a run with a given pause reason,
                 e.g. grep for an existing "ToolApprovalReason" test in this file and copy its run-construction
                 shape */;

    var vm = /* construct RunProgressViewModel the way the rest of this test file already does */;
    vm.Project(run); // or however this file already drives a projection

    Assert.Equal("Waiting for you to approve the plan", vm.SubLine); // or wherever DescribePause's result surfaces — confirm via the existing ToolApprovalReason test's assertion shape
}
```

- [ ] **Step 3: Run, confirm fail → pass**

```bash
dotnet test --filter "FullyQualifiedName~DescribePause_ReturnsPlanApprovalWording"
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

- [ ] **Step 3: Write a failing test for `PausedBodyKey`**

```csharp
[Fact]
public void PausedBodyKey_ReturnsPlanApprovalKey_ForThePlanApprovalReason()
{
    Assert.Equal("Flow_Run_PlanApproval", AgentRunNotificationSurface.PausedBodyKey(AgentRunOrchestrator.PlanApprovalReason));
}
```

- [ ] **Step 4: Write a failing test for the Flow routing (the more important one)**

Find this file's existing test that asserts `NeedsGoalReason` produces an `OpenParkedRunAction` (grep the test file for `OpenParkedRunAction`) and mirror it:

```csharp
[Fact]
public async Task PublishParkedRun_UsesOpenParkedRunAction_ForPlanApprovalReason()
{
    // Build a run parked WaitingForInput with reason=plan-approval, publish it through the surface the
    // same way the existing NeedsGoalReason test does, and assert the published FlowItemDraft.Action is an
    // OpenParkedRunAction, NOT a ContinueRunAction.
    var published = /* ... */;
    Assert.IsType<OpenParkedRunAction>(published.Action);
}
```

- [ ] **Step 5: Run, confirm fail → pass**

```bash
dotnet test --filter "FullyQualifiedName~PlanApproval"
```

- [ ] **Step 6: Commit**

```bash
git add src/Pia.Wpf/Services/AgentRunNotificationSurface.cs tests/Pia.Wpf.Tests/Services/AgentRunNotificationSurfaceTests.cs
git commit -m "Route plan-approval Flow cards to chat instead of a bare one-click resume"
```

### Task 3.4: `HeadlessRunLauncher.InterruptedReasonFor` allowlist join

**Files:**
- Modify: `src/Pia.Wpf/Services/HeadlessRunLauncher.cs:151-156`
- Test: grep `tests/Pia.Wpf.Tests/Services/` for existing `HeadlessRunLauncher` tests covering `InterruptedReasonFor` (it's `private static`, so any existing test reaches it indirectly — via a simulated pre-dispatch resume failure, or via `InternalsVisibleTo` + reflection; check which the file already does before writing a new test)

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

- [ ] **Step 2: Write a failing test**

If the existing test file reaches `InterruptedReasonFor` via reflection (check `AgentRunResumeNoRePlanPremiseTests.cs` — its name suggests it already tests reason-preservation-on-resume behavior), add:

```csharp
[Fact]
public void InterruptedReasonFor_PreservesThePlanApprovalReason()
{
    var method = typeof(HeadlessRunLauncher).GetMethod("InterruptedReasonFor",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    var result = (string)method.Invoke(null, new object?[] { AgentRunOrchestrator.PlanApprovalReason })!;
    Assert.Equal(AgentRunOrchestrator.PlanApprovalReason, result);
}
```

If the existing tests instead drive this through a full simulated pre-dispatch failure (end-to-end, not reflection), mirror THAT shape instead — check the existing `NeedsGoalReason`/`NeedsInputReason` coverage in this area before choosing.

- [ ] **Step 3: Run, confirm fail → pass**

```bash
dotnet test --filter "FullyQualifiedName~InterruptedReasonFor_PreservesThePlanApprovalReason"
```

- [ ] **Step 4: Run the full gate**

```bash
dotnet test
```

- [ ] **Step 5: Commit**

```bash
git add src/Pia.Wpf/Services/HeadlessRunLauncher.cs tests/Pia.Wpf.Tests/Services/AgentRunResumeNoRePlanPremiseTests.cs
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

- [ ] **Step 3: Write a failing test — the happy path**

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

- [ ] **Step 4: Write a failing test — wrong reason must not match**

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

- [ ] **Step 5: Write a failing test — `RunChanged` fires only on the win**

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

- [ ] **Step 6: Run all three, confirm fail → pass**

```bash
dotnet test --filter "FullyQualifiedName~TryRejectParkedPlanAsync"
```

- [ ] **Step 7: Run the full gate**

```bash
dotnet test
```

- [ ] **Step 8: Commit**

```bash
git add src/Pia.Wpf/Services/Interfaces/IAgentRunService.cs src/Pia.Wpf/Services/AgentRunService.cs tests/Pia.Wpf.Tests/Services/AgentRunServiceTests.cs
git commit -m "Add TryRejectParkedPlanAsync: CAS a plan-approval park straight to Cancelled"
```

### Task 4.2: `AgentRunOrchestrator.PostPlanRejectedNoticeAsync`

**Files:**
- Modify: `src/Pia.Wpf/Services/AgentRunOrchestrator.cs` (new trailing constructor parameter + new public method)
- Modify: every existing positional `new AgentRunOrchestrator(...)` test construction that would otherwise be unaffected (none need editing — the new parameter is trailing and defaulted, per this class's own established convention; this step is a grep-and-confirm, not an edit)
- Modify: `src/Pia.Wpf/Resources/Strings/ViewStrings.resx` + `.de.resx` + `.fr.resx` (one new key)
- Test: `tests/Pia.Wpf.Tests/Services/AgentRunOrchestratorTests.cs` (or wherever `PostAndMirrorClarificationQuestionAsync`'s behavior is already covered — reuse that scaffolding)

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
dotnet test --filter "FullyQualifiedName~LocalizationTests.AllTranslations_MustBeComplete"
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

- [ ] **Step 4: Write a failing test**

```csharp
[Fact]
public async Task PostPlanRejectedNoticeAsync_PostsANoticeIntoTheRunsChat()
{
    // Construct AgentRunOrchestrator with a fake IAssistantChatService (_chats) and a fake ILocalizationService
    // (_localization) whose indexer returns a fixed string for "Run_PlanRejected_ChatNote" — reuse whatever
    // fakes this test file already has for testing PostAndMirrorClarificationQuestionAsync/SafePostClarificationQuestionAsync.
    var run = await runService.CreateAsync(/* ... */);

    await orchestrator.PostPlanRejectedNoticeAsync(run.Id, TestPersonas.Default, CancellationToken.None);

    var chat = await chatService.GetAsync(run.ChatId, CancellationToken.None);
    Assert.Contains(chat!.Messages, m => m.Content == "expected fixed string from the fake localization service");
}
```

- [ ] **Step 5: Write a failing test for the null-localization no-op**

```csharp
[Fact]
public async Task PostPlanRejectedNoticeAsync_NoOps_WhenLocalizationIsNull()
{
    // Construct AgentRunOrchestrator WITHOUT a localization service (the default null).
    var run = await runService.CreateAsync(/* ... */);

    await orchestrator.PostPlanRejectedNoticeAsync(run.Id, TestPersonas.Default, CancellationToken.None);

    var chat = await chatService.GetAsync(run.ChatId, CancellationToken.None);
    Assert.Empty(chat?.Messages ?? []);
}
```

- [ ] **Step 6: Run, confirm fail → pass**

```bash
dotnet test --filter "FullyQualifiedName~PostPlanRejectedNoticeAsync"
```

- [ ] **Step 7: Commit**

```bash
git add src/Pia.Wpf/Services/AgentRunOrchestrator.cs src/Pia.Wpf/Resources/Strings/ViewStrings.resx src/Pia.Wpf/Resources/Strings/ViewStrings.de.resx src/Pia.Wpf/Resources/Strings/ViewStrings.fr.resx src/Pia.Wpf/Resources/Strings/ViewStrings.Designer.cs tests/Pia.Wpf.Tests/Services/AgentRunOrchestratorTests.cs
git commit -m "Post a chat notice when a proposed plan is rejected"
```

### Task 4.3: `IAgentRunResumeService.RejectPlanAsync` + `HeadlessRunLauncher` implementation

**Files:**
- Modify: `src/Pia.Wpf/Services/Interfaces/IAgentRunResumeService.cs`
- Modify: `src/Pia.Wpf/Services/HeadlessRunLauncher.cs`
- Test: `tests/Pia.Wpf.Tests/Services/HeadlessRunLauncherTests.cs` (grep to confirm exact name)

- [ ] **Step 1: Add the interface member**

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
            var persona = await _personaService.ResolveActiveAsync(
                WindowMode.Assistant, settings.UserOperatingMode ?? UserOperatingMode.Personal).ConfigureAwait(false);

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

- [ ] **Step 3: Write a failing test — happy path**

```csharp
[Fact]
public async Task RejectPlanAsync_CancelsTheRun_AndPostsANotice()
{
    var run = await agentRunService.CreateAsync(/* ... */);
    await agentRunService.PauseAsync(run.Id, AgentRunOrchestrator.PlanApprovalReason);

    var result = await launcher.RejectPlanAsync(run.Id);

    Assert.True(result);
    var updated = await agentRunService.GetAsync(run.Id);
    Assert.Equal(AgentRunState.Cancelled, updated!.State);
    // Assert the notice landed — via whatever fake IAssistantChatService this test's DI scope resolves.
}
```

- [ ] **Step 4: Write a failing test — false when not parked on this reason**

```csharp
[Fact]
public async Task RejectPlanAsync_ReturnsFalse_WhenRunIsNotParkedOnPlanApproval()
{
    var run = await agentRunService.CreateAsync(/* ... */); // never parked
    var result = await launcher.RejectPlanAsync(run.Id);
    Assert.False(result);
}
```

- [ ] **Step 5: Write a failing test — notice failure does not flip the CAS result**

```csharp
[Fact]
public async Task RejectPlanAsync_StillReturnsTrue_WhenTheNoticeFailsToPost()
{
    // Fake the scoped AgentRunOrchestrator's PostPlanRejectedNoticeAsync to throw. The CAS already
    // committed before this call, so the method must still return true.
    var run = await agentRunService.CreateAsync(/* ... */);
    await agentRunService.PauseAsync(run.Id, AgentRunOrchestrator.PlanApprovalReason);

    var result = await launcher.RejectPlanAsync(run.Id);

    Assert.True(result);
    var updated = await agentRunService.GetAsync(run.Id);
    Assert.Equal(AgentRunState.Cancelled, updated!.State);
}
```

- [ ] **Step 6: Run, confirm fail → pass**

```bash
dotnet test --filter "FullyQualifiedName~RejectPlanAsync"
```

- [ ] **Step 7: Run the full gate**

```bash
dotnet test
```

- [ ] **Step 8: Commit**

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

- [ ] **Step 2: Add a plan-approval lead line for the signal band**

`ViewStrings.resx`:
```xml
<data name="Run_PlanApproval_Title" xml:space="preserve"><value>Review this plan before it runs</value></data>
```

`ViewStrings.de.resx`:
```xml
<data name="Run_PlanApproval_Title" xml:space="preserve"><value>Diesen Plan vor der Ausführung prüfen</value></data>
```

`ViewStrings.fr.resx`:
```xml
<data name="Run_PlanApproval_Title" xml:space="preserve"><value>Vérifiez ce plan avant son exécution</value></data>
```

- [ ] **Step 3: Regenerate `Designer.cs` and run the parity test**

Same manual Visual-Studio-save step as Task 3.1 Step 3, then:

```bash
dotnet test --filter "FullyQualifiedName~LocalizationTests.AllTranslations_MustBeComplete"
```

- [ ] **Step 4: Commit**

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

- [ ] **Step 7: Add a plan-approval title line property for the signal band**

```csharp
    /// <summary>The signal band's lead line while parked for plan approval; null everywhere else so the
    /// existing lead-line binding is unaffected on every other state.</summary>
    public string? PlanApprovalTitle => IsPlanApprovalPause ? _localization["Run_PlanApproval_Title"] : null;
```

(Confirm the exact existing lead-line property name in this file before wiring the XAML in Task 5.3 — grep for whatever binds to the signal band's headline text today, since this plan does not have its exact name from the research above; it may already be `ComposeSubLine`/`SubLine`-driven rather than needing a brand-new property. If `SubLine`/`ComposeSubLine` already covers this via `DescribePause`'s new arm from Task 3.2, DROP this step and Task 5.3's corresponding binding — do not introduce a redundant second lead-line property. Verify against the actual XAML before deciding.)

- [ ] **Step 8: Write failing tests**

```csharp
[Fact]
public void Project_SetsIsPlanApprovalPause_ForAPlanApprovalPark()
{
    var run = /* WaitingForInput, reason=plan-approval */;
    vm.Project(run);
    Assert.True(vm.IsPlanApprovalPause);
    Assert.True(vm.ShowRejectPlanButton);
    Assert.False(vm.ShowNudgeBox);
    Assert.Equal("Approve", vm.ContinueLabel); // or whatever the fake ILocalizationService returns for Run_Action_ApprovePlan
}

[Fact]
public void Project_LeavesPlanApprovalPropertiesFalse_ForAnOrdinaryToolApprovalPark()
{
    var run = /* WaitingForInput, reason=tool-approval */;
    vm.Project(run);
    Assert.False(vm.IsPlanApprovalPause);
    Assert.False(vm.ShowRejectPlanButton);
    Assert.True(vm.ShowNudgeBox); // ordinary park keeps the nudge box
}

[Fact]
public async Task RejectPlan_CallsResumeServiceRejectPlanAsync()
{
    // fake _resumeService captures the call
    await vm.RejectPlanCommand.ExecuteAsync(null);
    Assert.True(fakeResumeService.RejectPlanAsyncCalled);
}
```

- [ ] **Step 9: Run, confirm fail → pass**

```bash
dotnet test --filter "FullyQualifiedName~PlanApproval"
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

- [ ] **Step 4: Wire the plan-approval title line — CONDITIONAL on Task 5.2 Step 7's decision**

If Task 5.2 kept `PlanApprovalTitle` as a real property (because the existing lead line does NOT already route through `DescribePause`), find the signal band's headline `TextBlock` (search this file for whatever binds the lead/sub line today — the panel's own comments in the research above call this "Region A") and add a `DataTrigger`-style override so `PlanApprovalTitle` wins when non-null, following whatever pattern the existing lead-line binding already uses for its own state-dependent text. If Task 5.2 dropped `PlanApprovalTitle` (because `DescribePause`'s new arm already covers it via the existing `SubLine`/activity-line binding), skip this step entirely — no XAML change needed beyond Steps 1-3.

- [ ] **Step 5: Build and manually smoke-test**

```bash
dotnet build src/Pia.Wpf/Pia.Wpf.csproj
```

WPF/XAML bindings are not exercised by `dotnet test` — a typo in a binding path fails silently at runtime, not at build time. Run the app (`dotnet run --project src/Pia.Wpf/Pia.Wpf.csproj`), and USE `dotnet test`'s existing `RunProgressViewModelTests` coverage from Task 5.2 as the correctness check for the VM side; the XAML binding paths themselves need a manual check — this plan's Chunk 6 Task 6.4 includes the full manual smoke test once the composer guard is also wired up, so a full plan-approval flow can actually be triggered end-to-end.

- [ ] **Step 6: Commit**

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
    /// <summary>
    /// True while this chat's active run is parked WaitingForInput specifically for plan approval
    /// (<c>AgentRunOrchestrator.PlanApprovalReason</c>) — narrower than <see cref="ForeignRunActive"/>, which
    /// deliberately stays false for ANY WaitingForInput park so the "continue in chat" path stays open. This
    /// one exists to close exactly one path: a plan sitting on screen for Approve/Reject must not be
    /// bypassed by starting an unrelated second turn (Send, Regenerate, or an Agent-mode suggestion chip) —
    /// see ChatSessionManager's three recompute sites and its StartTurnAsync backstop guard.
    /// </summary>
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
- Test: `tests/Pia.Wpf.Tests/ViewModels/Models/ChatSessionManagerTests.cs` (grep to confirm exact name/location)

- [ ] **Step 1: Recompute at `ActivateAsync` (line 551)**

Current:

```csharp
        session.SetForeignRunActive(_executingRuns.IsExecuting(chat.Id));
```

This site seeds `ForeignRunActive` before a resumable run is even looked up (`RestoreActiveRunAsync` runs after and is the real source of park-reason knowledge for a re-attached run) — so `PlanApprovalParkActive` has nothing to compute here yet. **No change at this site**; it is seeded by `RestoreActiveRunAsync` (Step 2) instead, the same way `ForeignRunActive`'s OWN plan-approval-aware value ultimately comes from there too. Confirm this by reading `ActivateAsync`'s full body before skipping — if it calls `RestoreActiveRunAsync` itself right after this line, the ordering already guarantees the correct final value; if it does not, `PlanApprovalParkActive` needs an explicit `session.SetPlanApprovalParkActive(false)` seed here for symmetry. Check and note which is true in a code comment at the call site.

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

- On `e.State == AgentRunState.WaitingForInput` for the session holding this run: do a ONE-TIME read of the run's reason (an occasional pause-transition event, not a per-step hot path — unlike the `executing`-only recompute above it, which fires every step) via `_agentRunService.GetAsync`, then `SetPlanApprovalParkActive` accordingly.
- On any OTHER `e.State` for the session holding this run: `SetPlanApprovalParkActive(false)` unconditionally (a run that is Running/Cancelled/Completed/etc. is not parked for plan approval by definition).

Add, inside the existing `foreach (var session in _allSessions)` loop, right after the existing `session.SetForeignRunActive(foreign);` line:

```csharp
                    if (holdsThisRun)
                    {
                        if (e.State == AgentRunState.WaitingForInput)
                        {
                            // One read per pause transition (not per step) — cheap, and the only way this
                            // handler can tell a plan-approval park apart from any other WaitingForInput one;
                            // e.State alone (see the no-I/O discipline above this block) cannot.
                            var run = await _agentRunService.GetAsync(e.RunId).ConfigureAwait(false);
                            session.SetPlanApprovalParkActive(
                                run is not null && RunPauseEnvelope.ReadReason(run) == AgentRunOrchestrator.PlanApprovalReason);
                        }
                        else
                        {
                            session.SetPlanApprovalParkActive(false);
                        }
                    }
```

This requires the enclosing `_syncContext.Post(_ => { ... }, null)` lambda to become `async` (`_syncContext.Post(async _ => { ... }, null)`) so the `await` compiles — confirm this does not change the method's fire-and-forget semantics in a way the rest of the handler relies on (it already catches all exceptions in its own `try`/`catch`, so an async void-shaped continuation is consistent with what is already there).

- [ ] **Step 4: Add the `StartTurnAsync` backstop guard**

Current (line 667):

```csharp
        if (await TryAnswerParkedRunAsync(session, userText, attachment, regenerationInstruction))
            return;
```

Change to:

```csharp
        // Backstop for any caller that reaches StartTurnAsync WITHOUT going through one of the three
        // composer-level pre-checks added in AssistantViewModel (CanExecuteSendMessage, RegenerateCore,
        // SwitchToAgent) — defense in depth, same shape TryAnswerParkedRunAsync's own check is. A REFUSAL,
        // not an "answer the park" branch: typing over a plan sitting on screen for Approve/Reject must
        // never be read as approving or rejecting it.
        if (session.PlanApprovalParkActive)
        {
            _logger.LogInformation(
                "Chat {ChatId}: refusing a new turn while a plan-approval park is active", session.Id);
            return;
        }

        if (await TryAnswerParkedRunAsync(session, userText, attachment, regenerationInstruction))
            return;
```

This re-reads `session.PlanApprovalParkActive` (the live, session-level flag from Task 6.1/Step 2-3 above) rather than doing its own DB read — so it composes correctly with Reject: once `RejectPlanAsync`'s CAS lands and raises `RunChanged(Cancelled)`, Step 3's handler clears the flag, and the very next `StartTurnAsync` call proceeds normally.

- [ ] **Step 5: Write a failing test for the `StartTurnAsync` backstop**

```csharp
[Fact]
public async Task StartTurnAsync_RefusesANewTurn_WhilePlanApprovalParkIsActive()
{
    var session = /* build a session, set session.SetPlanApprovalParkActive(true) directly for this unit test */;
    var messageCountBefore = session.Messages.Count;

    await manager.StartTurnAsync(session, "some new message", attachment: null);

    Assert.Equal(messageCountBefore, session.Messages.Count); // nothing was added — refused, not answered
}
```

- [ ] **Step 6: Write a failing test for `RestoreActiveRunAsync`'s recompute**

```csharp
[Fact]
public async Task RestoreActiveRunAsync_SetsPlanApprovalParkActive_ForAPlanApprovalPark()
{
    var run = /* create + pause with AgentRunOrchestrator.PlanApprovalReason */;
    var session = /* new, unattached session for the same chat */;

    await manager.RestoreActiveRunAsync(session);

    Assert.True(session.PlanApprovalParkActive);
}

[Fact]
public async Task RestoreActiveRunAsync_LeavesPlanApprovalParkActiveFalse_ForAnOrdinaryPark()
{
    var run = /* create + pause with AgentRunOrchestrator.NeedsInputReason */;
    var session = /* new, unattached session for the same chat */;

    await manager.RestoreActiveRunAsync(session);

    Assert.False(session.PlanApprovalParkActive);
}
```

- [ ] **Step 7: Write a failing test for the `OnAgentRunChanged` recompute — set on park, clear on resolve**

```csharp
[Fact]
public async Task OnAgentRunChanged_SetsThenClearsPlanApprovalParkActive_AcrossAParkAndAResolve()
{
    var session = /* attached to a run via session.SetActiveRun(runId) */;

    RaiseRunChanged(runId, AgentRunState.WaitingForInput); // with the fake IAgentRunService returning a
                                                            // plan-approval-parked run for GetAsync(runId)
    await WaitForUiThreadWorkToDrainAsync(); // however this test file already synchronizes with _syncContext.Post

    Assert.True(session.PlanApprovalParkActive);

    RaiseRunChanged(runId, AgentRunState.Cancelled); // Reject landed
    await WaitForUiThreadWorkToDrainAsync();

    Assert.False(session.PlanApprovalParkActive);
}
```

Adapt `RaiseRunChanged`/`WaitForUiThreadWorkToDrainAsync` to whatever this test file's existing `OnAgentRunChanged` coverage (if any — grep for `ForeignRunActive` tests in this file) already uses to drive and observe the `_syncContext.Post` continuation.

- [ ] **Step 8: Run, confirm fail → pass**

```bash
dotnet test --filter "FullyQualifiedName~PlanApprovalParkActive"
```

- [ ] **Step 9: Run the full gate**

```bash
dotnet test
```

- [ ] **Step 10: Commit**

```bash
git add src/Pia.Wpf/ViewModels/Models/ChatSessionManager.cs tests/Pia.Wpf.Tests/ViewModels/Models/ChatSessionManagerTests.cs
git commit -m "Track PlanApprovalParkActive and refuse a new turn in StartTurnAsync while it holds"
```

### Task 6.3: `AssistantViewModel` guards + composer hint

**Files:**
- Modify: `src/Pia.Wpf/ViewModels/AssistantViewModel.cs`
- Modify: `src/Pia.Wpf/Views/AssistantView.xaml`
- Modify: `src/Pia.Wpf/Resources/Strings/ViewStrings.resx` + `.de.resx` + `.fr.resx` (one new key)
- Test: `tests/Pia.Wpf.Tests/ViewModels/AssistantViewModelTests.cs`

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
dotnet test --filter "FullyQualifiedName~LocalizationTests.AllTranslations_MustBeComplete"
```

- [ ] **Step 2: Add a `PlanApprovalParkActive` passthrough property on `AssistantViewModel`**

The composer hint and the three guards below all need to read the ACTIVE session's flag. Find how this ViewModel already exposes `ForeignRunActive` (grep `ForeignRunActive` in `AssistantViewModel.cs` — it is referenced directly in `CanExecuteSendMessage`/`RegenerateCore`/`SwitchToAgent`, so it is likely a direct passthrough property or a raised/observed change off the active session). Add a property with the identical shape:

```csharp
    private bool PlanApprovalParkActive => _chatSessionManager.ActiveSession?.PlanApprovalParkActive ?? false;
```

Adjust this to match EXACTLY how `ForeignRunActive` itself is exposed on this ViewModel (it may be a bound `[ObservableProperty]` synced via an event subscription rather than a plain computed getter — check before assuming a bare getter is sufficient; if `ForeignRunActive` is an observable property kept in sync via `ForeignRunActiveChanged`, `PlanApprovalParkActive` needs the same subscription wiring off `PlanApprovalParkActiveChanged`, added wherever the existing subscription is set up).

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

```csharp
[Fact]
public void CanExecuteSendMessage_ReturnsFalse_WhilePlanApprovalParkActive()
{
    // set up an active session with PlanApprovalParkActive = true, non-empty InputText
    Assert.False(vm.SendMessageCommand.CanExecute(null));
}

[Fact]
public async Task RegenerateMessageCommand_DoesNothing_WhilePlanApprovalParkActive()
{
    // active session PlanApprovalParkActive = true; a regenerate-eligible message present
    var countBefore = vm.Messages.Count;
    await vm.RegenerateMessageCommand.ExecuteAsync(someMessage);
    Assert.Equal(countBefore, vm.Messages.Count); // nothing truncated
}

[Fact]
public async Task SwitchToAgentCommand_DoesNothing_WhilePlanApprovalParkActive()
{
    // active session PlanApprovalParkActive = true
    await vm.SwitchToAgentCommand.ExecuteAsync(new AgentModeSuggestion { Goal = "do the thing" });
    // Assert _chatSessionManager.StartTurnAsync was never called (via whatever fake this test file uses).
}
```

- [ ] **Step 8: Run, confirm fail → pass**

```bash
dotnet test --filter "FullyQualifiedName~PlanApprovalParkActive"
```

- [ ] **Step 9: Run the full gate**

```bash
dotnet test
```

Expected: `failed: 0`.

- [ ] **Step 10: Commit**

```bash
git add src/Pia.Wpf/ViewModels/AssistantViewModel.cs src/Pia.Wpf/Views/AssistantView.xaml src/Pia.Wpf/Resources/Strings/ViewStrings.resx src/Pia.Wpf/Resources/Strings/ViewStrings.de.resx src/Pia.Wpf/Resources/Strings/ViewStrings.fr.resx src/Pia.Wpf/Resources/Strings/ViewStrings.Designer.cs tests/Pia.Wpf.Tests/ViewModels/AssistantViewModelTests.cs
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
