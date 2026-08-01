using System.Reflection;
using System.Windows.Input;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Tests;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// Batch 08 8a — the run panel's own steering surface: the Pause request (D1) and the Continue widening
/// (D1 item 8) it enables. <see cref="RunProgressViewModel.State"/> is a plain settable
/// <c>[ObservableProperty]</c>, so the state-only theories below drive it directly rather than replaying a
/// whole run through <see cref="RunProgressViewModel.RefreshAsync"/> — <c>CanPause</c>/<c>CanContinue</c> are
/// pure projections of it and nothing else.
/// </summary>
public sealed class RunProgressViewModelSteeringTests
{
    private readonly Guid _runId = Guid.NewGuid();
    private readonly IAgentRunService _runs = Substitute.For<IAgentRunService>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly IAgentRunResumeService _resume = Substitute.For<IAgentRunResumeService>();

    public RunProgressViewModelSteeringTests()
    {
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]); // echo the key so a string is assertable
        _runs.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((AgentRun?)null);
    }

    private RunProgressViewModel CreateVm(IAgentRunSteeringService? steering = null)
    {
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());
        return new RunProgressViewModel(_runs, _runId, _loc, _resume, NullLogger.Instance, steering: steering);
    }

    /// <summary>
    /// The explicit set (D7), pinned member by member over all eight <see cref="RunProgressState"/> members:
    /// <c>Running</c> covers real <c>Running</c> AND <c>Verifying</c> (which folds into it in
    /// <c>MapState</c> — there is no separate <see cref="RunProgressState"/> member for it),
    /// <c>WaitingForChildren</c> is D6's cascade, <c>Planning</c> is excluded per §1 D1 item 8 (a resume skips
    /// planning entirely), and every other state is a terminal or already-parked one.
    /// Neutralize: replace the pattern with a range (e.g. <c>State &lt; Failed</c>) — this theory's
    /// <c>WaitingForChildren</c> row goes red the same way D7's own guard does.
    /// </summary>
    [Theory]
    [InlineData(RunProgressState.Planning, false)]
    [InlineData(RunProgressState.Running, true)]
    [InlineData(RunProgressState.Completed, false)]
    [InlineData(RunProgressState.TruncatedCompleted, false)]
    [InlineData(RunProgressState.Failed, false)]
    [InlineData(RunProgressState.WaitingForInput, false)]
    [InlineData(RunProgressState.Paused, false)]
    [InlineData(RunProgressState.WaitingForChildren, true)]
    public void CanPause_IsTrueOnlyForRunningAndWaitingForChildren(RunProgressState state, bool expected)
    {
        var vm = CreateVm(Substitute.For<IAgentRunSteeringService>());
        vm.State = state;

        Assert.Equal(expected, vm.CanPause);
        vm.Dispose();
    }

    /// <summary>A row-count pin for the theory above, exactly like <c>RunProgressConvertersTests</c>'s own
    /// spinner-coverage fact: an appended state would otherwise go unasserted rather than red.</summary>
    [Fact]
    public void CanPauseTheoryCoversEveryState()
        => Assert.Equal(8, Enum.GetValues<RunProgressState>().Length);

    /// <summary>
    /// The trailing-optional null-means-identical property (§5.4): a build with no steering service injected
    /// renders exactly the pre-Batch-08 panel — <c>CanPause</c> stays false even for the one state that would
    /// otherwise grant it.
    /// </summary>
    [Fact]
    public void CanPause_IsFalseWhenNoSteeringServiceWasInjected()
    {
        var vm = CreateVm(steering: null);
        vm.State = RunProgressState.Running;

        Assert.False(vm.CanPause);
        vm.Dispose();
    }

    /// <summary>
    /// D1 item 8's widening: <c>Paused</c> joins <c>WaitingForInput</c> on the identical Continue command.
    /// <c>WaitingForChildren</c> stays false — <c>RunProgressViewModelChildrenTests.cs</c>'s own
    /// <c>WaitingForChildren_ProjectsItsOwnStateAndDoesNotOfferContinue</c> pins this from the real-run side;
    /// this row keeps the two in agreement rather than leaving the false half implicit here.
    /// </summary>
    [Theory]
    [InlineData(RunProgressState.Planning, false)]
    [InlineData(RunProgressState.Running, false)]
    [InlineData(RunProgressState.Completed, false)]
    [InlineData(RunProgressState.TruncatedCompleted, false)]
    [InlineData(RunProgressState.Failed, false)]
    [InlineData(RunProgressState.WaitingForInput, true)]
    [InlineData(RunProgressState.Paused, true)]
    [InlineData(RunProgressState.WaitingForChildren, false)]
    public void CanContinue_IsTrueForWaitingForInputAndPaused(RunProgressState state, bool expected)
    {
        var vm = CreateVm();
        vm.State = state;

        Assert.Equal(expected, vm.CanContinue);
        vm.Dispose();
    }

    /// <summary>
    /// Privacy (hazard 1). <see cref="RunProgressViewModel.Pause"/> logs nothing at all on the happy path —
    /// this drives the FAILURE arm, the only one that logs, and asserts the resulting line carries the run id
    /// and NEVER the run's Goal or the in-flight step's Title, both bound onto this VM (the step title drives
    /// <c>CurrentActivity</c>) at the moment the failure is logged.
    /// </summary>
    [Fact]
    public async Task Pause_InvokesTheSteeringService_AndLogsTheRunIdOnly()
    {
        const string secretGoal = "SECRET GOAL 8f3e2a";
        const string secretTitle = "SECRET STEP TITLE 51c2d9";
        var step = new AgentStep
        {
            Id = Guid.NewGuid(), Ordinal = 0, Title = secretTitle, Status = AgentStepStatus.Running,
        };
        var run = new AgentRun { Id = _runId, State = AgentRunState.Running, Goal = secretGoal, Plan = [step] };
        _runs.GetAsync(_runId, Arg.Any<CancellationToken>()).Returns(run);

        var steering = Substitute.For<IAgentRunSteeringService>();
        steering.PauseAsync(_runId, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<bool>(new InvalidOperationException("boom")));

        var logger = new CapturingLogger<RunProgressViewModel>();
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());
        var vm = new RunProgressViewModel(_runs, _runId, _loc, _resume, logger, steering: steering);
        await vm.RefreshAsync(); // projects State = Running, CurrentActivity = secretTitle

        await vm.PauseCommand.ExecuteAsync(null);

        await steering.Received(1).PauseAsync(_runId, Arg.Any<CancellationToken>());
        var entries = logger.Entries;
        Assert.NotEmpty(entries); // non-vacuity: the forced failure really produced a log line
        Assert.All(entries, e => Assert.DoesNotContain(secretGoal, e.Message));
        Assert.All(entries, e => Assert.DoesNotContain(secretTitle, e.Message));
        Assert.Contains(entries, e => e.Message.Contains(_runId.ToString()));
        vm.Dispose();
    }

    /// <summary>
    /// D4's wiring, from the VM side: <see cref="RunProgressViewModel.NudgeText"/> rides the SAME resume call
    /// the budget Continue already makes, and is cleared ONLY when <c>ResumeAsync</c> returns <c>true</c> —
    /// this call actually started the dispatch (a CAS win). Stubbed <c>true</c> deliberately: an unconfigured
    /// substitute returns <c>false</c> by default, which would make this fact pass whether or not the clear
    /// were gated at all — see the sibling fact below for the <c>false</c> half, which is the one that
    /// actually discriminates the gate.
    /// </summary>
    [Fact]
    public async Task Continue_CarriesTheNudgeTextAndClearsIt()
    {
        var run = new AgentRun { Id = _runId, State = AgentRunState.Paused };
        _runs.GetAsync(_runId, Arg.Any<CancellationToken>()).Returns(run);
        _resume.ResumeAsync(_runId, Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(true);

        var vm = CreateVm();
        await vm.RefreshAsync();
        vm.NudgeText = "keep it under 200 words";

        await vm.ContinueCommand.ExecuteAsync(null);

        await _resume.Received(1).ResumeAsync(_runId, "keep it under 200 words", Arg.Any<CancellationToken>());
        Assert.Null(vm.NudgeText);
        vm.Dispose();
    }

    /// <summary>
    /// The other half, and the one that actually proves the gate: <c>ResumeAsync</c> returning <c>false</c> —
    /// a lost CAS, or a run someone else already claimed — is NOT an exception, so the <c>catch</c> never
    /// fires, and a resume that never started must not destroy the note before the retry that follows.
    /// </summary>
    [Fact]
    public async Task Continue_WhenResumeDidNotStart_LeavesTheNudgeTextIntact()
    {
        var run = new AgentRun { Id = _runId, State = AgentRunState.Paused };
        _runs.GetAsync(_runId, Arg.Any<CancellationToken>()).Returns(run);
        _resume.ResumeAsync(_runId, Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(false);

        var vm = CreateVm();
        await vm.RefreshAsync();
        vm.NudgeText = "keep it under 200 words";

        await vm.ContinueCommand.ExecuteAsync(null);

        await _resume.Received(1).ResumeAsync(_runId, "keep it under 200 words", Arg.Any<CancellationToken>());
        Assert.Equal("keep it under 200 words", vm.NudgeText);
        vm.Dispose();
    }

    /// <summary>Runs Post callbacks inline so the projection is observable synchronously in tests — the same
    /// shape every other RunProgressViewModel test file declares for itself.</summary>
    private sealed class InlineSyncContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
        public override void Send(SendOrPostCallback d, object? state) => d(state);
    }
}

/// <summary>
/// Batch 08 8b — the row-level plan-mutation verbs (D3). Substitute-based, like the class above: G6 already
/// proved the SERVICE's own validation exhaustively (16 cases against real SQLite), so these facts stay
/// scoped to what only the VM is responsible for — building the right <see cref="PlanStepEdit"/> list per
/// verb, repainting rows IN PLACE rather than re-minting them (W12), and refusing every verb while the run is
/// live. <c>_runs.GetAsync</c> is stubbed to return a fixed <see cref="AgentRun"/>/<see cref="AgentStep"/>
/// graph that a fact mutates in place immediately before invoking a command — standing in for "what the real
/// service would have persisted" without a database, since the VM's mutation handler (private; exercised only
/// through the commands) always re-reads via <c>RefreshAsync</c> after the call.
/// </summary>
public sealed class RunProgressViewModelPlanMutationTests
{
    private readonly Guid _runId = Guid.NewGuid();
    private readonly IAgentRunService _runs = Substitute.For<IAgentRunService>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly IAgentRunResumeService _resume = Substitute.For<IAgentRunResumeService>();

    public RunProgressViewModelPlanMutationTests()
    {
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]); // echo the key so a rejection note is assertable
    }

    /// <param name="steering">Trailing-optional, so every call site below stays <c>CreateVm()</c>. Only the
    /// <c>CanExecuteChanged</c> fact supplies one: <c>CanPause</c> is null-guarded on this service, so without
    /// it <c>PauseCommand</c>'s answer is false in every state while <c>_state</c> still notifies it — and that
    /// fact's set equality would then fail for a reason that is not the defect.</param>
    private RunProgressViewModel CreateVm(IAgentRunSteeringService? steering = null)
    {
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());
        return new RunProgressViewModel(_runs, _runId, _loc, _resume, NullLogger.Instance, steering: steering);
    }

    private sealed class InlineSyncContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
        public override void Send(SendOrPostCallback d, object? state) => d(state);
    }

    /// <summary>
    /// Each of the five mutating verbs calls <c>ApplyPlanMutationAsync</c> EXACTLY once per invocation — never
    /// zero (the verb silently no-opping) and never twice (a double-submit). <c>ClearReceivedCalls</c> between
    /// verbs is what makes "once" a per-verb claim rather than a cumulative count.
    /// </summary>
    [Fact]
    public async Task EveryVerb_RoundTripsThroughApplyPlanMutationAsync_Once()
    {
        var step1 = new AgentStep { Id = Guid.NewGuid(), Ordinal = 0, Title = "s1", Status = AgentStepStatus.Pending };
        var step2 = new AgentStep { Id = Guid.NewGuid(), Ordinal = 1, Title = "s2", Status = AgentStepStatus.Pending };
        var run = new AgentRun { Id = _runId, State = AgentRunState.Paused, Plan = [step1, step2] };
        _runs.GetAsync(_runId, Arg.Any<CancellationToken>()).Returns(run);
        _runs.ApplyPlanMutationAsync(_runId, Arg.Any<IReadOnlyList<PlanStepEdit>>(), Arg.Any<CancellationToken>())
            .Returns(new PlanMutationResult(PlanMutationOutcome.Applied, 2));

        var vm = CreateVm();
        await vm.RefreshAsync();
        var row1 = vm.Steps.Single(r => r.StepId == step1.Id);
        var row2 = vm.Steps.Single(r => r.StepId == step2.Id);

        async Task AssertCalledOnce(Func<Task> act)
        {
            await act();
            await _runs.Received(1).ApplyPlanMutationAsync(
                _runId, Arg.Any<IReadOnlyList<PlanStepEdit>>(), Arg.Any<CancellationToken>());
            _runs.ClearReceivedCalls();
        }

        vm.EditStepCommand.Execute(row1); // SaveStepEdit refuses a row whose editor was never opened
        row1.EditTitle = "edited";
        row1.EditIntent = null;
        await AssertCalledOnce(() => vm.SaveStepEditCommand.ExecuteAsync(row1));
        await AssertCalledOnce(() => vm.InsertStepBelowCommand.ExecuteAsync(row1));
        await AssertCalledOnce(() => vm.MoveStepUpCommand.ExecuteAsync(row2));
        await AssertCalledOnce(() => vm.MoveStepDownCommand.ExecuteAsync(row1));
        await AssertCalledOnce(() => vm.SkipStepCommand.ExecuteAsync(row2));

        vm.Dispose();
    }

    /// <summary>A rejection sets the localized note and leaves the rows exactly as they were — the
    /// re-projection reads back the SAME persisted plan, so nothing repaints.</summary>
    [Fact]
    public async Task AFailedMutation_ShowsALocalizedNote_AndDoesNotChangeTheRows()
    {
        var step = new AgentStep { Id = Guid.NewGuid(), Ordinal = 0, Title = "s1", Status = AgentStepStatus.Pending };
        var run = new AgentRun { Id = _runId, State = AgentRunState.Paused, Plan = [step] };
        _runs.GetAsync(_runId, Arg.Any<CancellationToken>()).Returns(run);
        _runs.ApplyPlanMutationAsync(_runId, Arg.Any<IReadOnlyList<PlanStepEdit>>(), Arg.Any<CancellationToken>())
            .Returns(new PlanMutationResult(PlanMutationOutcome.TitleRequired, 1));

        var vm = CreateVm();
        await vm.RefreshAsync();
        var row = vm.Steps.Single();

        await vm.SkipStepCommand.ExecuteAsync(row);

        Assert.Equal("Run_Plan_Error_TitleRequired", vm.PlanMutationNote); // the fake echoes the loc key
        Assert.Single(vm.Steps);
        Assert.Equal("s1", vm.Steps.Single().Title);
        Assert.Equal(AgentStepStatus.Pending, vm.Steps.Single().Status); // the skip did NOT land
        vm.Dispose();
    }

    /// <summary>
    /// A rejection note must not survive past the pause it was about — the <c>PublishNote</c> precedent
    /// guards itself the same way. Once the run leaves <c>Paused</c> (resumed, here), the next projection
    /// clears it, so a run that ran to completion does not keep showing a stale plan-mutation complaint.
    /// </summary>
    [Fact]
    public async Task PlanMutationNote_ClearsOnceTheRunLeavesPaused()
    {
        var step = new AgentStep { Id = Guid.NewGuid(), Ordinal = 0, Title = "s1", Status = AgentStepStatus.Pending };
        var run = new AgentRun { Id = _runId, State = AgentRunState.Paused, Plan = [step] };
        _runs.GetAsync(_runId, Arg.Any<CancellationToken>()).Returns(run);
        _runs.ApplyPlanMutationAsync(_runId, Arg.Any<IReadOnlyList<PlanStepEdit>>(), Arg.Any<CancellationToken>())
            .Returns(new PlanMutationResult(PlanMutationOutcome.TitleRequired, 1));

        var vm = CreateVm();
        await vm.RefreshAsync();
        await vm.SkipStepCommand.ExecuteAsync(vm.Steps.Single());
        Assert.NotNull(vm.PlanMutationNote); // non-vacuity: the rejection really set it

        run.State = AgentRunState.Running; // the run resumed and moved on
        await vm.RefreshAsync();

        Assert.Null(vm.PlanMutationNote);
        vm.Dispose();
    }

    /// <summary>
    /// W12, the RED-before-the-fix fact: an edit preserves the step's Id, and the row must repaint IN PLACE —
    /// never be re-minted — or the panel would lose whatever local UI state (this test doesn't set any, but
    /// <see cref="StepRowViewModel"/>'s own identity is otherwise meaningless to a caller holding a reference).
    /// RED before <c>StepRowViewModel.Title</c> became an <c>[ObservableProperty]</c>: <c>SyncSteps</c>'s
    /// else-branch could not assign it, so <c>vm.Steps.Single().Title</c> stayed "old title" forever.
    /// </summary>
    [Fact]
    public async Task EditingAStepTitle_RepaintsTheRow_WithoutReMintingItsId()
    {
        var stepId = Guid.NewGuid();
        var step = new AgentStep { Id = stepId, Ordinal = 0, Title = "old title", Status = AgentStepStatus.Pending };
        var run = new AgentRun { Id = _runId, State = AgentRunState.Paused, Plan = [step] };
        _runs.GetAsync(_runId, Arg.Any<CancellationToken>()).Returns(run);
        _runs.ApplyPlanMutationAsync(_runId, Arg.Any<IReadOnlyList<PlanStepEdit>>(), Arg.Any<CancellationToken>())
            .Returns(new PlanMutationResult(PlanMutationOutcome.Applied, 1));

        var vm = CreateVm();
        await vm.RefreshAsync();
        var rowBefore = vm.Steps.Single();
        vm.EditStepCommand.Execute(rowBefore); // SaveStepEdit refuses a row whose editor was never opened
        rowBefore.EditTitle = "new title";
        rowBefore.EditIntent = null;

        // Stand-in for "what the real service just persisted" — the same AgentStep instance run.Plan holds,
        // mutated in place so the re-projection RefreshAsync triggers next reads the NEW title back.
        step.Title = "new title";

        await vm.SaveStepEditCommand.ExecuteAsync(rowBefore);

        var rowAfter = vm.Steps.Single();
        Assert.Same(rowBefore, rowAfter);          // never re-minted
        Assert.Equal(stepId, rowAfter.StepId);     // the Id survived the edit
        Assert.Equal("new title", rowAfter.Title);
        Assert.False(rowAfter.IsEditing);           // the editor closed either way
        vm.Dispose();
    }

    /// <summary>
    /// W12, the other RED-before-the-fix fact: a reorder that preserves every step's Id must actually MOVE the
    /// existing rows, never rebuild the collection. RED before <c>SyncSteps</c> gained its index-reconciling
    /// pass: the insert/update loop only ever INSERTS a brand-new row at its plan index, so two rows whose Ids
    /// both already existed would repaint in place and the collection would keep its OLD visual order forever.
    /// </summary>
    [Fact]
    public async Task ReorderingSteps_MovesTheExistingRows()
    {
        var s1 = new AgentStep { Id = Guid.NewGuid(), Ordinal = 0, Title = "s1", Status = AgentStepStatus.Pending };
        var s2 = new AgentStep { Id = Guid.NewGuid(), Ordinal = 1, Title = "s2", Status = AgentStepStatus.Pending };
        var run = new AgentRun { Id = _runId, State = AgentRunState.Paused, Plan = [s1, s2] };
        _runs.GetAsync(_runId, Arg.Any<CancellationToken>()).Returns(run);
        _runs.ApplyPlanMutationAsync(_runId, Arg.Any<IReadOnlyList<PlanStepEdit>>(), Arg.Any<CancellationToken>())
            .Returns(new PlanMutationResult(PlanMutationOutcome.Applied, 2));

        var vm = CreateVm();
        await vm.RefreshAsync();
        var row1 = vm.Steps[0];
        var row2 = vm.Steps[1];
        Assert.Equal(s1.Id, row1.StepId);
        Assert.Equal(s2.Id, row2.StepId);

        // Stand-in for the persisted reorder a real MoveStepDown would have produced.
        run.Plan = [s2, s1];

        await vm.MoveStepDownCommand.ExecuteAsync(row1);

        Assert.Same(row2, vm.Steps[0]); // the SAME instances, just moved — never re-minted
        Assert.Same(row1, vm.Steps[1]);
        Assert.Equal(s2.Id, vm.Steps[0].StepId);
        Assert.Equal(s1.Id, vm.Steps[1].StepId);
        vm.Dispose();
    }

    /// <summary>
    /// All five mutating verbs refuse to execute while the run is live — asserted through
    /// <c>ICommand.CanExecute</c> directly, never through the row's own <c>IsMutable</c> (which is
    /// Status-based, not run-state-based, and defaults such that a value-only check would not discriminate —
    /// hazard 4/8). <c>CancelStepEditCommand</c> is the deliberate exception: dismissing an already-open editor
    /// must work even if the run stopped being pausable mid-edit.
    /// </summary>
    [Fact]
    public void RowCommands_AreDisabledWhileTheRunIsLive()
    {
        var vm = CreateVm();
        var row = new StepRowViewModel { StepId = Guid.NewGuid(), Title = "s1", Status = AgentStepStatus.Pending };

        vm.State = RunProgressState.Running;
        Assert.False(vm.EditStepCommand.CanExecute(row));
        Assert.False(vm.SaveStepEditCommand.CanExecute(row));
        Assert.False(vm.InsertStepBelowCommand.CanExecute(row));
        Assert.False(vm.MoveStepUpCommand.CanExecute(row));
        Assert.False(vm.MoveStepDownCommand.CanExecute(row));
        Assert.False(vm.SkipStepCommand.CanExecute(row));
        Assert.True(vm.CancelStepEditCommand.CanExecute(row));

        vm.State = RunProgressState.Paused;
        Assert.True(vm.EditStepCommand.CanExecute(row));
        Assert.True(vm.SaveStepEditCommand.CanExecute(row));
        Assert.True(vm.InsertStepBelowCommand.CanExecute(row));
        Assert.True(vm.MoveStepUpCommand.CanExecute(row));
        Assert.True(vm.MoveStepDownCommand.CanExecute(row));
        Assert.True(vm.SkipStepCommand.CanExecute(row));

        vm.Dispose();
    }

    /// <summary>
    /// <b>Batch 08 F4.</b> Every command whose <c>CanExecute</c> ANSWER changes when the run pauses must also
    /// RAISE <c>CanExecuteChanged</c> — set equality, both directions, over whatever commands the VM happens to
    /// expose. <c>EditStepCommand</c> was the one that did not: it was missing from <c>_state</c>'s
    /// <c>[NotifyCanExecuteChangedFor]</c> block, and CommunityToolkit's <c>RelayCommand</c> has no
    /// <c>CommandManager</c> integration, so <c>ButtonBase</c> keeps the <c>_canExecute</c> it cached when the
    /// row was realized. A row that existed while the run was live therefore showed "Edit step" greyed out for
    /// the panel's whole life, while the four verbs beside it lit up — and only re-minting the VM recovered it.
    /// <para>
    /// Why the shipped facts missed it, and why this one is shaped by REFLECTION rather than by six named
    /// subscriptions: <see cref="RowCommands_AreDisabledWhileTheRunIsLive"/> calls <c>CanExecute</c> directly,
    /// which recomputes on every call and can never observe a missing notification, and the row-level fact sets
    /// <c>Paused</c> BEFORE loading the row so its buttons hook already-enabled. A named list would have to be
    /// extended by hand for the seventh verb — exactly the omission this is guarding. The set is discovered, so
    /// a new command is covered the moment it exists.
    /// </para>
    /// </summary>
    [Fact]
    public void EveryCommandWhoseCanExecuteChangesOnPause_AlsoRaisesCanExecuteChanged()
    {
        var vm = CreateVm(Substitute.For<IAgentRunSteeringService>());
        var row = new StepRowViewModel { StepId = Guid.NewGuid(), Title = "s1", Status = AgentStepStatus.Pending };

        var commands = typeof(RunProgressViewModel)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => typeof(ICommand).IsAssignableFrom(p.PropertyType))
            .ToDictionary(p => p.Name, p => (ICommand)p.GetValue(vm)!, StringComparer.Ordinal);

        // Non-vacuity floor: the ten commands the panel ships today. A discovery that found none — a renamed
        // generator suffix, a changed base type — would otherwise make every assertion below trivially true.
        Assert.True(commands.Count >= 10, $"only {commands.Count} commands were discovered: {string.Join(",", commands.Keys)}");

        vm.State = RunProgressState.Running;
        // The row argument is ignored by the parameterless commands and consumed by the per-row ones, so one
        // call shape covers both kinds.
        var before = commands.ToDictionary(kv => kv.Key, kv => kv.Value.CanExecute(row), StringComparer.Ordinal);

        var notified = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, command) in commands)
            command.CanExecuteChanged += (_, _) => notified.Add(name);

        vm.State = RunProgressState.Paused;

        var changed = commands.Where(kv => kv.Value.CanExecute(row) != before[kv.Key]).Select(kv => kv.Key);
        var changedText = string.Join(",", changed.OrderBy(n => n, StringComparer.Ordinal));
        var notifiedText = string.Join(",", notified.OrderBy(n => n, StringComparer.Ordinal));

        Assert.NotEqual(string.Empty, changedText); // the state flip really did move some answers
        Assert.Equal(changedText, notifiedText);

        vm.Dispose();
    }

    /// <summary>
    /// <b>Batch 08 F6: a REFUSED pause must give the button back and say so.</b> <c>PauseAsync</c>'s
    /// <c>bool</c> used to be discarded, and <see cref="RunProgressViewModel.IsPausing"/> is deliberately not
    /// cleared in a <c>finally</c> (an accepted pause takes time to land, and clearing early re-enables the
    /// button over a request that is still coming). Together those made a refusal indistinguishable from a slow
    /// pause: the button read "Pausing…" and stayed disabled for the VM's whole life, with no note and no
    /// retry. Refusal is reachable four ways — the run is not pausable, nothing in this process is dispatching
    /// it, the read faulted, or the service threw — and after Batch 08 F10 a fifth: the dispatch is already
    /// terminating from a Stop.
    /// </summary>
    [Theory]
    [InlineData(false)]  // the service refused
    [InlineData(true)]   // …or threw, which is the same thing to the user
    public async Task ARefusedPause_ClearsIsPausing_AndExplainsItself(bool throws)
    {
        var run = new AgentRun { Id = _runId, State = AgentRunState.Running };
        _runs.GetAsync(_runId, Arg.Any<CancellationToken>()).Returns(run);

        var steering = Substitute.For<IAgentRunSteeringService>();
        steering.PauseAsync(_runId, Arg.Any<CancellationToken>())
            .Returns(throws ? Task.FromException<bool>(new InvalidOperationException("boom")) : Task.FromResult(false));

        var vm = CreateVm(steering);
        await vm.RefreshAsync();
        Assert.True(vm.CanPause);       // non-vacuity: the button was live before the click
        Assert.Null(vm.PauseNote);

        await vm.PauseCommand.ExecuteAsync(null);

        Assert.False(vm.IsPausing);                                  // the button is usable again
        Assert.True(vm.CanPause);                                    // …and really re-enabled, not merely un-flagged
        Assert.Equal("Run_Pause_Error_Refused", vm.PauseNote);       // the fake echoes the loc key
        vm.Dispose();
    }

    /// <summary>Batch 08 F6's other half, and the one that keeps the fix from being "always clear": an
    /// ACCEPTED pause leaves <c>IsPausing</c> true and says nothing, because the request is on its way and the
    /// row's own move out of the pausable states is what retires the affordance.</summary>
    [Fact]
    public async Task AnAcceptedPause_KeepsTheButtonInFlight_AndAddsNoNote()
    {
        var run = new AgentRun { Id = _runId, State = AgentRunState.Running };
        _runs.GetAsync(_runId, Arg.Any<CancellationToken>()).Returns(run);

        var steering = Substitute.For<IAgentRunSteeringService>();
        steering.PauseAsync(_runId, Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));

        var vm = CreateVm(steering);
        await vm.RefreshAsync();
        await vm.PauseCommand.ExecuteAsync(null);

        Assert.True(vm.IsPausing);
        Assert.False(vm.CanPause);
        Assert.Null(vm.PauseNote);

        // …and the note/flag pair is retired together once the run leaves the pausable states.
        run.State = AgentRunState.Paused;
        await vm.RefreshAsync();
        Assert.False(vm.IsPausing);
        Assert.Null(vm.PauseNote);
        vm.Dispose();
    }

    /// <summary>
    /// <b>Batch 08 F12: "Pause the run to change its plan." is shown only while there is a Pause button to
    /// press.</b> The note used to be the INVERSE of <c>CanMutatePlan</c>, i.e. true in every state except
    /// <c>Paused</c> — so a run parked at its budget showed the instruction next to a Continue button and no
    /// Pause button (impossible to follow), and a run that completed an hour ago carried it forever. The impl
    /// spec §13 8b states the condition as "whenever the run is LIVE".
    /// <para>
    /// Asserted as an identity against <c>CanPause</c> over EVERY state rather than as a hand-written table, so
    /// the two can never drift and a ninth state is covered the moment it exists.
    /// </para>
    /// </summary>
    [Fact]
    public void ShowPauseFirstNote_IsExactlyCanPause_InEveryState()
    {
        var vm = CreateVm(Substitute.For<IAgentRunSteeringService>());

        foreach (var state in Enum.GetValues<RunProgressState>())
        {
            vm.State = state;
            Assert.Equal(vm.CanPause, vm.ShowPauseFirstNote);
        }

        // The two states the defect was actually about: the note is GONE where it used to nag.
        vm.State = RunProgressState.WaitingForInput;
        Assert.False(vm.ShowPauseFirstNote);
        vm.State = RunProgressState.Completed;
        Assert.False(vm.ShowPauseFirstNote);
        // …and still present where it is actionable (non-vacuity: this is not a property that is always false).
        vm.State = RunProgressState.Running;
        Assert.True(vm.ShowPauseFirstNote);

        // It also follows IsPausing, which is half of CanPause — a pause already in flight has nothing left to
        // instruct. That needs its own notification on _isPausing, which is the easy half to forget.
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);
        vm.IsPausing = true;
        Assert.False(vm.ShowPauseFirstNote);
        Assert.Contains(nameof(RunProgressViewModel.ShowPauseFirstNote), raised);
        vm.Dispose();
    }

    /// <summary>
    /// <b>Batch 08 F13: the <c>NotPaused</c> rejection is no longer wiped by its own refresh.</b>
    /// <c>ApplyStepEditsAsync</c> set <see cref="RunProgressViewModel.PlanMutationNote"/> and THEN awaited
    /// <c>RefreshAsync</c>, and <c>Project</c> clears that note on any state but <c>Paused</c> — which is the
    /// definition of the <c>NotPaused</c> outcome. So the one rejection that means "your edit did not happen
    /// and the run has moved on" was set and instantly erased: the row-button group vanished with
    /// <c>CanMutatePlan</c> and nothing said why. The other five outcomes are returned only after the service's
    /// own <c>Paused</c> gate, which is why the fix is a one-line reorder and not a freshness flag.
    /// </summary>
    [Fact]
    public async Task ANotPausedRejection_SurvivesItsOwnRefresh()
    {
        var step = new AgentStep { Id = Guid.NewGuid(), Ordinal = 0, Title = "s1", Status = AgentStepStatus.Pending };
        var run = new AgentRun { Id = _runId, State = AgentRunState.Paused, Plan = [step] };
        _runs.GetAsync(_runId, Arg.Any<CancellationToken>()).Returns(run);

        var vm = CreateVm();
        await vm.RefreshAsync();
        var row = vm.Steps.Single();

        // The race the outcome models: something else (the Flow card's "Continue run", a second window) resumed
        // the run between the click and the write, so the mutation is refused AND the refresh reads Running.
        _runs.ApplyPlanMutationAsync(_runId, Arg.Any<IReadOnlyList<PlanStepEdit>>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                run.State = AgentRunState.Running;
                return new PlanMutationResult(PlanMutationOutcome.NotPaused, 1);
            });

        await vm.SkipStepCommand.ExecuteAsync(row);

        Assert.Equal(RunProgressState.Running, vm.State);                    // the refresh really did re-project
        Assert.False(vm.CanMutatePlan);                                      // …and the button group really is gone
        Assert.Equal("Run_Plan_Error_NotPaused", vm.PlanMutationNote);       // so the note is the ONLY feedback left
        vm.Dispose();
    }
}
