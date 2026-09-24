using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Tests.TestInfrastructure;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// An approval spans two rows — the park and the replay that answers it — so an unmerged trace reports two
/// approvals as four decisions, half of them "not executed" for calls that did run.
/// </summary>
public sealed class RunProgressViewModelApprovalPairingTests
{
    private readonly Guid _runId = Guid.NewGuid();
    private readonly Guid _stepId = Guid.NewGuid();
    private readonly IAgentRunService _runs = Substitute.For<IAgentRunService>();
    private readonly IAgentTimelineService _timeline = Substitute.For<IAgentTimelineService>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly IAgentRunResumeService _resume = Substitute.For<IAgentRunResumeService>();

    public RunProgressViewModelApprovalPairingTests()
    {
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        _loc.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => (string)ci[0]);
        _runs.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((AgentRun?)null);
        _timeline.GetForRunAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentTimelineEvent>());
    }

    /// <summary>The reported case: two prompts, both accepted, four rows.</summary>
    [Fact]
    public async Task TwoAnsweredParks_RenderAsTwoApprovedRows_NotFour()
    {
        Rows(
            Row(1, ToolGateDecision.ParkedForApproval, AgentTimelineOutcome.NotExecuted, "write_file"),
            Row(2, ToolGateDecision.GrantedByName, AgentTimelineOutcome.Ok, "write_file"),
            Row(3, ToolGateDecision.ParkedForApproval, AgentTimelineOutcome.NotExecuted, "edit_file"),
            Row(4, ToolGateDecision.GrantedByName, AgentTimelineOutcome.Ok, "edit_file"));

        var vm = await LoadedVm();

        Assert.Equal(2, vm.Timeline.Count);
        Assert.All(vm.Timeline, r => Assert.Equal("Run_Timeline_Decision_Approved", r.DecisionLabel));
    }

    /// <summary>The pill is what the user actually read off the collapsed band.</summary>
    [Fact]
    public async Task TheSummaryCountsOneDecisionPerApproval()
    {
        Rows(
            Row(1, ToolGateDecision.ParkedForApproval, AgentTimelineOutcome.NotExecuted, "write_file"),
            Row(2, ToolGateDecision.GrantedByName, AgentTimelineOutcome.Ok, "write_file"));

        var vm = await LoadedVm();

        var pill = Assert.Single(vm.DecisionPills);
        Assert.Equal("Run_Timeline_Pill_Approved", pill.Text);
    }

    /// <summary>A scheduled job's configured envelope resolves to the very same decision with nobody asked,
    /// so an unpaired grant must not claim a person approved it.</summary>
    [Fact]
    public async Task AGrantWithNoParkBeforeIt_StaysAutoApproved()
    {
        Rows(Row(1, ToolGateDecision.GrantedByName, AgentTimelineOutcome.Ok, "write_file"));

        var vm = await LoadedVm();

        var row = Assert.Single(vm.Timeline);
        Assert.Equal("Run_Timeline_Decision_AutoApproved", row.DecisionLabel);
    }

    /// <summary>A park row is folded away only once something answers it; until then it is the pending
    /// question and the trace must keep showing it.</summary>
    [Fact]
    public async Task AnUnansweredPark_IsStillRendered()
    {
        Rows(Row(1, ToolGateDecision.ParkedForApproval, AgentTimelineOutcome.NotExecuted, "write_file"));

        var vm = await LoadedVm();

        var row = Assert.Single(vm.Timeline);
        Assert.Equal("Run_Timeline_Decision_NotExecuted", row.DecisionLabel);
    }

    /// <summary>The delete-four-files case: a second parked call of the same tool deliberately writes no
    /// second park row, so every replay of that tool is still the same person's answer.</summary>
    [Fact]
    public async Task OneParkAnswersEveryReplayOfThatToolInTheStep()
    {
        Rows(
            Row(1, ToolGateDecision.ParkedForApproval, AgentTimelineOutcome.NotExecuted, "write_file"),
            Row(2, ToolGateDecision.GrantedByName, AgentTimelineOutcome.Ok, "write_file"),
            Row(3, ToolGateDecision.GrantedByName, AgentTimelineOutcome.Ok, "write_file"));

        var vm = await LoadedVm();

        Assert.Equal(2, vm.Timeline.Count);
        Assert.All(vm.Timeline, r => Assert.Equal("Run_Timeline_Decision_Approved", r.DecisionLabel));
    }

    /// <summary>Pairing is per step: a grant in a later step answers nothing a previous step parked on.</summary>
    [Fact]
    public async Task AGrantInAnotherStep_DoesNotAnswerThisStepsPark()
    {
        var otherStep = Guid.NewGuid();
        Rows(
            Row(1, ToolGateDecision.ParkedForApproval, AgentTimelineOutcome.NotExecuted, "write_file"),
            Row(2, ToolGateDecision.GrantedByName, AgentTimelineOutcome.Ok, "write_file", otherStep));

        var vm = await LoadedVm();

        Assert.Equal(2, vm.Timeline.Count);
        Assert.Contains(vm.Timeline, r => r.DecisionLabel == "Run_Timeline_Decision_NotExecuted");
        Assert.Contains(vm.Timeline, r => r.DecisionLabel == "Run_Timeline_Decision_AutoApproved");
    }

    private void Rows(params AgentTimelineEvent[] rows) =>
        _timeline.GetForRunAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(rows.ToList());

    private async Task<RunProgressViewModel> LoadedVm()
    {
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());
        var vm = new RunProgressViewModel(_runs, _runId, _loc, _resume, NullLogger.Instance, _timeline);
        vm.IsTimelineExpanded = true;
        await vm.TimelineLoadTask!;
        return vm;
    }

    private AgentTimelineEvent Row(
        long seq, ToolGateDecision decision, AgentTimelineOutcome outcome, string toolName,
        Guid? stepId = null) => new(
        Id: Guid.NewGuid(),
        RunId: _runId,
        StepId: stepId ?? _stepId,
        Seq: seq,
        Kind: AgentTimelineEventKind.ToolCall,
        Surface: ToolGateSurface.Unattended,
        Decision: decision,
        Outcome: outcome,
        ToolName: toolName,
        ToolClass: ToolClass.Files,
        PluginId: null,
        ArgsChars: 12,
        ResultChars: 20,
        DurationMs: 5,
        CreatedAt: DateTime.UtcNow,
        ToolCallId: null, Round: 1, StepOrdinal: null, RequestedAt: DateTime.UtcNow, DecidedAt: null);
}
