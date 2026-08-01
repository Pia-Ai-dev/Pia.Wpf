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
    /// the budget Continue already makes, and is cleared once the dispatch has been started — a nudge that
    /// failed to reach this resume must not silently ride the next one.
    /// </summary>
    [Fact]
    public async Task Continue_CarriesTheNudgeTextAndClearsIt()
    {
        var run = new AgentRun { Id = _runId, State = AgentRunState.Paused };
        _runs.GetAsync(_runId, Arg.Any<CancellationToken>()).Returns(run);

        var vm = CreateVm();
        await vm.RefreshAsync();
        vm.NudgeText = "keep it under 200 words";

        await vm.ContinueCommand.ExecuteAsync(null);

        await _resume.Received(1).ResumeAsync(_runId, "keep it under 200 words", Arg.Any<CancellationToken>());
        Assert.Null(vm.NudgeText);
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
