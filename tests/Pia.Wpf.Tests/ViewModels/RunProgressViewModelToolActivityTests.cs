using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Tests.TestInfrastructure;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// A headless step writes its reply only when it ends, so the panel's tool line is the only thing that moves
/// while it runs.
/// </summary>
public sealed class RunProgressViewModelToolActivityTests
{
    private readonly Guid _runId = Guid.NewGuid();
    private readonly Guid _stepId = Guid.NewGuid();
    private readonly IAgentRunService _runs = Substitute.For<IAgentRunService>();
    private readonly IAgentTimelineService _timeline = Substitute.For<IAgentTimelineService>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly IAgentRunResumeService _resume = Substitute.For<IAgentRunResumeService>();

    public RunProgressViewModelToolActivityTests()
    {
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        _loc.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => (string)ci[0]);
        _timeline.GetForRunAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentTimelineEvent>());
        Run(AgentRunState.Running);
    }

    /// <summary>The reported 1 min 36 s of nothing: the step is running and has called no tool yet.</summary>
    [Fact]
    public async Task BeforeTheFirstToolCall_TheLineSaysTheModelIsStillWorking()
    {
        var vm = await LoadedVm();

        Assert.True(vm.HasToolActivity);
        Assert.Equal("Run_ToolActivity_Waiting", vm.ToolActivity);
    }

    [Fact]
    public async Task AfterAToolCall_TheLineNamesTheLatestTool()
    {
        _timeline.GetForRunAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<AgentTimelineEvent>
        {
            Row(1, "web_search"),
            Row(2, "read_file"),
        });

        var vm = await LoadedVm();

        Assert.Equal("Run_ToolActivity_AfterTool", vm.ToolActivity);
    }

    /// <summary>A row belonging to another step is not this step's progress.</summary>
    [Fact]
    public async Task AnotherStepsCalls_DoNotFeedThisStepsLine()
    {
        _timeline.GetForRunAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentTimelineEvent> { Row(1, "web_search", Guid.NewGuid()) });

        var vm = await LoadedVm();

        Assert.Equal("Run_ToolActivity_Waiting", vm.ToolActivity);
    }

    [Fact]
    public async Task ASettledRun_HasNoToolLine()
    {
        Run(AgentRunState.Completed, AgentStepStatus.Done);

        var vm = await LoadedVm();

        Assert.False(vm.HasToolActivity);
        Assert.Null(vm.ToolActivity);
    }

    /// <summary>The band opens itself exactly once — a user who closed it mid-run stays closed.</summary>
    [Fact]
    public async Task TheActivityBandOpensOnceWhenTheRunStartsExecuting()
    {
        var vm = await LoadedVm();
        Assert.True(vm.IsTimelineExpanded);

        vm.IsTimelineExpanded = false;
        Run(AgentRunState.Running);
        await vm.RefreshAsync();

        Assert.False(vm.IsTimelineExpanded);
    }

    /// <summary>
    /// A step advance leaves the run on Running, and step N+1 writes no timeline row until its first call
    /// RETURNS — so nothing but the projection itself can clear step N's tally off the line.
    /// </summary>
    [Fact]
    public async Task AStepAdvance_ClearsThePreviousStepsTally()
    {
        var second = Guid.NewGuid();
        _timeline.GetForRunAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentTimelineEvent> { Row(1, "web_search") });
        var vm = await LoadedVm();
        Assert.Equal("Run_ToolActivity_AfterTool", vm.ToolActivity);

        // Step 1 settles and step 2 starts. No timeline event rides along: the trace is already primed, so
        // nothing re-reads it, which is exactly the window the stale line lived in.
        _runs.GetAsync(_runId, Arg.Any<CancellationToken>()).Returns(new AgentRun
        {
            Id = _runId,
            State = AgentRunState.Running,
            Plan =
            [
                new AgentStep { Id = _stepId, Ordinal = 1, Title = "Research", Status = AgentStepStatus.Done },
                new AgentStep { Id = second, Ordinal = 2, Title = "Compare", Status = AgentStepStatus.Running },
            ],
        });
        await vm.RefreshAsync();

        Assert.Equal("Run_ToolActivity_Waiting", vm.ToolActivity);
    }
    private void Run(AgentRunState state, AgentStepStatus stepStatus = AgentStepStatus.Running) =>
        _runs.GetAsync(_runId, Arg.Any<CancellationToken>()).Returns(new AgentRun
        {
            Id = _runId,
            State = state,
            Plan = [new AgentStep { Id = _stepId, Ordinal = 1, Title = "Research", Status = stepStatus }],
        });

    private async Task<RunProgressViewModel> LoadedVm()
    {
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());
        var vm = new RunProgressViewModel(_runs, _runId, _loc, _resume, NullLogger.Instance, _timeline);
        await vm.RefreshAsync();
        if (vm.TimelineLoadTask is { } load) await load;
        return vm;
    }

    private AgentTimelineEvent Row(long seq, string toolName, Guid? stepId = null) => new(
        Id: Guid.NewGuid(),
        RunId: _runId,
        StepId: stepId ?? _stepId,
        Seq: seq,
        Kind: AgentTimelineEventKind.ToolCall,
        Surface: ToolGateSurface.Unattended,
        Decision: ToolGateDecision.AutoApprovedPolicy,
        Outcome: AgentTimelineOutcome.Ok,
        ToolName: toolName,
        ToolClass: ToolClass.Files,
        PluginId: null,
        ArgsChars: 12,
        ResultChars: 20,
        DurationMs: 5,
        CreatedAt: DateTime.UtcNow,
        ToolCallId: null, Round: 1, StepOrdinal: null, RequestedAt: null, DecidedAt: null);
}
