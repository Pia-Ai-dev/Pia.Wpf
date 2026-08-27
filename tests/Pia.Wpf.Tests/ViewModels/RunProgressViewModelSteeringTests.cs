using System.Reflection;
using System.Windows.Input;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Tests;
using Pia.ViewModels;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.ViewModels;

public sealed class RunProgressViewModelSteeringTests
{
    private readonly Guid _runId = Guid.NewGuid();
    private readonly IAgentRunService _runs = Substitute.For<IAgentRunService>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly IAgentRunResumeService _resume = Substitute.For<IAgentRunResumeService>();

    public RunProgressViewModelSteeringTests()
    {
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        _runs.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((AgentRun?)null);
    }

    private RunProgressViewModel CreateVm(IAgentRunSteeringService? steering = null)
    {
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());
        return new RunProgressViewModel(_runs, _runId, _loc, _resume, NullLogger.Instance, steering: steering);
    }

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

    // Without this, a newly added state would go unasserted by the theory above rather than red.
    [Fact]
    public void CanPauseTheoryCoversEveryState()
        => Assert.Equal(8, Enum.GetValues<RunProgressState>().Length);

    [Fact]
    public void CanPause_IsFalseWhenNoSteeringServiceWasInjected()
    {
        var vm = CreateVm(steering: null);
        vm.State = RunProgressState.Running;

        Assert.False(vm.CanPause);
        vm.Dispose();
    }

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

    [Fact]
    public async Task ToolApprovalPark_OffersDeny_NamingTheParkedTool()
    {
        _runs.GetAsync(_runId, Arg.Any<CancellationToken>()).Returns(new AgentRun
        {
            Id = _runId,
            ChatId = Guid.NewGuid(),
            RunShape = RunShape.Planned,
            State = AgentRunState.WaitingForInput,
            ExtraJson = """{"paused":true,"reason":"tool-approval","tool":"git_commit"}""",
        });
        var vm = CreateVm();

        await vm.RefreshAsync();

        Assert.True(vm.IsToolApprovalPause);
        Assert.Equal("git_commit", vm.ApprovalToolName);
        Assert.True(vm.DeclineToolCommand.CanExecute(null));

        await vm.DeclineToolCommand.ExecuteAsync(null);

        await _resume.Received(1).DeclineAsync(_runId, Arg.Any<CancellationToken>());
        await _resume.DidNotReceive().ResumeAsync(Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<bool>());
        vm.Dispose();
    }

    /// <summary>Continue on an approval park IS the grant, so the panel has to say what it is about to allow —
    /// naming delete_file without the paths is the blind consent the gate used to refuse to ask for.</summary>
    [Fact]
    public async Task ToolApprovalPark_NamesWhatTheCallWouldActOn()
    {
        _loc.Format(Arg.Any<string>(), Arg.Any<object[]>())
            .Returns(ci => (string)ci[0] + ":" + string.Join("|", (object[])ci[1]));
        _runs.GetAsync(_runId, Arg.Any<CancellationToken>()).Returns(new AgentRun
        {
            Id = _runId,
            ChatId = Guid.NewGuid(),
            RunShape = RunShape.Planned,
            State = AgentRunState.WaitingForInput,
            ExtraJson = """{"paused":true,"reason":"tool-approval","tool":"delete_file","args":"path=fragments/0001.md, path=fragments/0004.md"}""",
        });
        var vm = CreateVm();

        await vm.RefreshAsync();

        Assert.True(vm.HasApprovalTarget);
        Assert.Equal("path=fragments/0001.md, path=fragments/0004.md", vm.ApprovalToolArguments);
        Assert.Contains("fragments/0004.md", vm.ApprovalTargetLine!);
        vm.Dispose();
    }

    /// <summary>Every envelope written before the args member existed, and every parked call that carried no
    /// string arguments: the line collapses rather than rendering an empty "Affects".</summary>
    [Fact]
    public async Task ToolApprovalPark_WithoutArguments_ShowsNoTargetLine()
    {
        _runs.GetAsync(_runId, Arg.Any<CancellationToken>()).Returns(new AgentRun
        {
            Id = _runId,
            ChatId = Guid.NewGuid(),
            RunShape = RunShape.Planned,
            State = AgentRunState.WaitingForInput,
            ExtraJson = """{"paused":true,"reason":"tool-approval","tool":"git_commit"}""",
        });
        var vm = CreateVm();

        await vm.RefreshAsync();

        Assert.True(vm.IsToolApprovalPause);
        Assert.False(vm.HasApprovalTarget);
        Assert.Null(vm.ApprovalTargetLine);
        vm.Dispose();
    }

    [Fact]
    public async Task BudgetPark_OffersNoDeny()
    {
        _runs.GetAsync(_runId, Arg.Any<CancellationToken>()).Returns(new AgentRun
        {
            Id = _runId,
            ChatId = Guid.NewGuid(),
            RunShape = RunShape.Planned,
            State = AgentRunState.WaitingForInput,
            ExtraJson = """{"paused":true,"reason":"step-cap"}""",
        });
        var vm = CreateVm();

        await vm.RefreshAsync();

        Assert.False(vm.IsToolApprovalPause);
        Assert.Null(vm.ApprovalToolName);
        Assert.False(vm.CanDeclineTool);
        Assert.False(vm.DeclineToolCommand.CanExecute(null));
        Assert.True(vm.CanContinue);
        vm.Dispose();
    }

    // Pause logs nothing on the happy path, so the failure arm is forced to get a line to inspect.
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
        await vm.RefreshAsync();

        await vm.PauseCommand.ExecuteAsync(null);

        await steering.Received(1).PauseAsync(_runId, Arg.Any<CancellationToken>());
        var entries = logger.Entries;
        Assert.NotEmpty(entries); // the Assert.All calls below pass vacuously on an empty list
        Assert.All(entries, e => Assert.DoesNotContain(secretGoal, e.Message));
        Assert.All(entries, e => Assert.DoesNotContain(secretTitle, e.Message));
        Assert.Contains(entries, e => e.Message.Contains(_runId.ToString()));
        vm.Dispose();
    }

    // ResumeAsync is stubbed true on purpose: an unstubbed substitute returns false, so this would pass either way.
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
}

public sealed class RunProgressViewModelPlanMutationTests
{
    private readonly Guid _runId = Guid.NewGuid();
    private readonly IAgentRunService _runs = Substitute.For<IAgentRunService>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly IAgentRunResumeService _resume = Substitute.For<IAgentRunResumeService>();

    public RunProgressViewModelPlanMutationTests()
    {
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
    }

    private RunProgressViewModel CreateVm(IAgentRunSteeringService? steering = null)
    {
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());
        return new RunProgressViewModel(_runs, _runId, _loc, _resume, NullLogger.Instance, steering: steering);
    }

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
        Assert.Equal(AgentStepStatus.Pending, vm.Steps.Single().Status);
        vm.Dispose();
    }

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
        Assert.NotNull(vm.PlanMutationNote); // keeps the Assert.Null below from passing vacuously

        run.State = AgentRunState.Running;
        await vm.RefreshAsync();

        Assert.Null(vm.PlanMutationNote);
        vm.Dispose();
    }

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

        // Stand-in for the persisted write: mutated in place so the next refresh reads the new title back.
        step.Title = "new title";

        await vm.SaveStepEditCommand.ExecuteAsync(rowBefore);

        var rowAfter = vm.Steps.Single();
        Assert.Same(rowBefore, rowAfter);
        Assert.Equal(stepId, rowAfter.StepId);
        Assert.Equal("new title", rowAfter.Title);
        Assert.False(rowAfter.IsEditing);
        vm.Dispose();
    }

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

        Assert.Same(row2, vm.Steps[0]);
        Assert.Same(row1, vm.Steps[1]);
        Assert.Equal(s2.Id, vm.Steps[0].StepId);
        Assert.Equal(s1.Id, vm.Steps[1].StepId);
        vm.Dispose();
    }

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

    // Commands are discovered by reflection, so a newly added one is covered without extending a hand-written list.
    [Fact]
    public void EveryCommandWhoseCanExecuteChangesOnPause_AlsoRaisesCanExecuteChanged()
    {
        var vm = CreateVm(Substitute.For<IAgentRunSteeringService>());
        var row = new StepRowViewModel { StepId = Guid.NewGuid(), Title = "s1", Status = AgentStepStatus.Pending };

        var commands = typeof(RunProgressViewModel)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => typeof(ICommand).IsAssignableFrom(p.PropertyType))
            .ToDictionary(p => p.Name, p => (ICommand)p.GetValue(vm)!, StringComparer.Ordinal);

        // A discovery that found nothing would make every assertion below trivially true.
        Assert.True(commands.Count >= 10, $"only {commands.Count} commands were discovered: {string.Join(",", commands.Keys)}");

        vm.State = RunProgressState.Running;
        // The row argument is ignored by the parameterless commands, so one call shape covers both kinds.
        var before = commands.ToDictionary(kv => kv.Key, kv => kv.Value.CanExecute(row), StringComparer.Ordinal);

        var notified = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, command) in commands)
            command.CanExecuteChanged += (_, _) => notified.Add(name);

        vm.State = RunProgressState.Paused;

        var changed = commands.Where(kv => kv.Value.CanExecute(row) != before[kv.Key]).Select(kv => kv.Key);
        var changedText = string.Join(",", changed.OrderBy(n => n, StringComparer.Ordinal));
        var notifiedText = string.Join(",", notified.OrderBy(n => n, StringComparer.Ordinal));

        Assert.NotEqual(string.Empty, changedText); // otherwise the set equality below is empty vs empty
        Assert.Equal(changedText, notifiedText);

        vm.Dispose();
    }

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
        Assert.True(vm.CanPause);
        Assert.Null(vm.PauseNote);

        await vm.PauseCommand.ExecuteAsync(null);

        Assert.False(vm.IsPausing);
        Assert.True(vm.CanPause);
        Assert.Equal("Run_Pause_Error_Refused", vm.PauseNote);       // the fake echoes the loc key
        vm.Dispose();
    }

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

        run.State = AgentRunState.Paused;
        await vm.RefreshAsync();
        Assert.False(vm.IsPausing);
        Assert.Null(vm.PauseNote);
        vm.Dispose();
    }

    [Fact]
    public void ShowPauseFirstNote_IsExactlyCanPause_InEveryState()
    {
        var vm = CreateVm(Substitute.For<IAgentRunSteeringService>());

        foreach (var state in Enum.GetValues<RunProgressState>())
        {
            vm.State = state;
            Assert.Equal(vm.CanPause, vm.ShowPauseFirstNote);
        }

        vm.State = RunProgressState.WaitingForInput;
        Assert.False(vm.ShowPauseFirstNote);
        vm.State = RunProgressState.Completed;
        Assert.False(vm.ShowPauseFirstNote);
        // Non-vacuity: the property is not simply always false.
        vm.State = RunProgressState.Running;
        Assert.True(vm.ShowPauseFirstNote);

        // The note also follows IsPausing, which needs its own change notification.
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);
        vm.IsPausing = true;
        Assert.False(vm.ShowPauseFirstNote);
        Assert.Contains(nameof(RunProgressViewModel.ShowPauseFirstNote), raised);
        vm.Dispose();
    }

    [Fact]
    public async Task ANotPausedRejection_SurvivesItsOwnRefresh()
    {
        var step = new AgentStep { Id = Guid.NewGuid(), Ordinal = 0, Title = "s1", Status = AgentStepStatus.Pending };
        var run = new AgentRun { Id = _runId, State = AgentRunState.Paused, Plan = [step] };
        _runs.GetAsync(_runId, Arg.Any<CancellationToken>()).Returns(run);

        var vm = CreateVm();
        await vm.RefreshAsync();
        var row = vm.Steps.Single();

        // Models the race the outcome exists for: something else resumed the run between the click and the write.
        _runs.ApplyPlanMutationAsync(_runId, Arg.Any<IReadOnlyList<PlanStepEdit>>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                run.State = AgentRunState.Running;
                return new PlanMutationResult(PlanMutationOutcome.NotPaused, 1);
            });

        await vm.SkipStepCommand.ExecuteAsync(row);

        Assert.Equal(RunProgressState.Running, vm.State);
        Assert.False(vm.CanMutatePlan);
        Assert.Equal("Run_Plan_Error_NotPaused", vm.PlanMutationNote);
        vm.Dispose();
    }

    // IsPausing has no timeout by design; a watchdog that quietly re-enables the button must red this first.
    [Fact]
    public async Task AnAcceptedPauseThatNeverLands_StaysInFlightWithoutATimeout_AndTheParkIsStillTheWayOut()
    {
        var run = new AgentRun
        {
            Id = _runId,
            State = AgentRunState.Running,
            Plan = [new AgentStep { Id = Guid.NewGuid(), Ordinal = 0, Title = "s0", Status = AgentStepStatus.Running }],
        };
        _runs.GetAsync(_runId, Arg.Any<CancellationToken>()).Returns(run);

        var steering = Substitute.For<IAgentRunSteeringService>();
        steering.PauseAsync(_runId, Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));

        var vm = CreateVm(steering);
        await vm.RefreshAsync();
        Assert.True(vm.CanPause);
        Assert.Equal("Run_Action_Pause", vm.PauseLabel);

        await vm.PauseCommand.ExecuteAsync(null);

        Assert.True(vm.IsPausing);
        Assert.Equal("Run_Action_Pausing", vm.PauseLabel);
        Assert.False(vm.PauseCommand.CanExecute(null));

        // The plan grows each iteration so the step count proves the refresh really re-projected.
        for (var i = 1; i <= 3; i++)
        {
            run.Plan =
            [
                .. run.Plan,
                new AgentStep { Id = Guid.NewGuid(), Ordinal = i, Title = $"s{i}", Status = AgentStepStatus.Pending },
            ];
            await vm.RefreshAsync();

            Assert.Equal(i + 1, vm.Steps.Count);
            Assert.True(vm.IsPausing);
            Assert.Equal("Run_Action_Pausing", vm.PauseLabel);
            Assert.False(vm.CanPause);
            Assert.Null(vm.PauseNote);
        }

        run.State = AgentRunState.WaitingForInput;
        await vm.RefreshAsync();

        Assert.False(vm.IsPausing);
        Assert.Equal("Run_Action_Pause", vm.PauseLabel);
        Assert.True(vm.CanContinue);

        _resume.ResumeAsync(_runId, Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(true);
        await vm.ContinueCommand.ExecuteAsync(null);
        await _resume.Received(1).ResumeAsync(_runId, Arg.Any<string?>(), Arg.Any<CancellationToken>());
        vm.Dispose();
    }
}
