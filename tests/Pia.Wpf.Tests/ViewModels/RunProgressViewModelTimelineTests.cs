using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// Batch 03's render surface: the tool-activity trace on the run panel. Read-only, re-read on EACH expand,
/// and deliberately outside live projection — a run emits up to ~500 events and none of them may drive a
/// re-projection.
/// </summary>
public sealed class RunProgressViewModelTimelineTests
{
    private readonly Guid _runId = Guid.NewGuid();
    private readonly IAgentRunService _runs = Substitute.For<IAgentRunService>();
    private readonly IAgentTimelineService _timeline = Substitute.For<IAgentTimelineService>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly IAgentRunResumeService _resume = Substitute.For<IAgentRunResumeService>();

    public RunProgressViewModelTimelineTests()
    {
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]); // echo the key so the label is assertable
        _loc.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => (string)ci[0]);
        _runs.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((AgentRun?)null);
        _timeline.GetForRunAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentTimelineEvent>());
    }

    [Fact]
    public async Task TimelineIsReReadOnEveryExpand()
    {
        var vm = CreateVm();
        await _timeline.DidNotReceive().GetForRunAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());

        // Awaited through the VM's own seam: the read now hops off the caller's thread, so asserting straight
        // after the property set would race it.
        vm.IsTimelineExpanded = true;
        await vm.TimelineLoadTask!;
        await _timeline.Received(1).GetForRunAsync(_runId, Arg.Any<CancellationToken>());
        Assert.True(vm.HasNoTimeline);

        // A trace read BEFORE the run's first gated call must not pin "nothing was recorded" for the session.
        // This is the load-once latch's defect, mechanized: with the latch the second expand reads nothing and
        // the panel keeps rendering the empty line over a run that has since recorded two decisions.
        _timeline.GetForRunAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<AgentTimelineEvent>
        {
            Row(1, AgentTimelineEventKind.ToolCall, ToolGateDecision.ApprovedOnce),
            Row(2, AgentTimelineEventKind.ToolCall, ToolGateDecision.AutoApprovedPolicy),
        });

        vm.IsTimelineExpanded = false;
        vm.IsTimelineExpanded = true;
        await vm.TimelineLoadTask!;

        await _timeline.Received(2).GetForRunAsync(_runId, Arg.Any<CancellationToken>());
        Assert.Collection(vm.Timeline,
            first => Assert.Equal("Run_Timeline_Decision_Approved", first.DecisionLabel),
            second => Assert.Equal("Run_Timeline_Decision_AutoApproved", second.DecisionLabel));
        Assert.False(vm.HasNoTimeline);
    }

    [Fact]
    public async Task AFailedReadSaysSo_AndIsNeverRenderedAsAnEmptyTrace()
    {
        _timeline.GetForRunAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("the store is gone"));

        var vm = CreateVm();
        await vm.LoadTimelineAsync();

        // "No tool decisions were recorded" is a POSITIVE claim about the run and this read proved nothing of
        // the sort. Same standard the CardCancelled decision is held to on the write side.
        Assert.True(vm.HasTimelineReadError);
        Assert.False(vm.HasNoTimeline);
        Assert.Empty(vm.Timeline);

        // And it is not sticky: a read that later succeeds clears the error line.
        _timeline.GetForRunAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<AgentTimelineEvent>
        {
            Row(1, AgentTimelineEventKind.ToolCall, ToolGateDecision.ApprovedOnce),
        });

        await vm.LoadTimelineAsync();

        Assert.False(vm.HasTimelineReadError);
        Assert.Single(vm.Timeline);
    }

    [Fact]
    public async Task TimelineIsNotLoadedByRunChanged()
    {
        // Control for the fact above (which proves the same path DOES read): the trace takes no part in live
        // projection, which is what keeps ~500 emits per run off it.
        var vm = CreateVm();

        for (var i = 0; i < 5; i++)
            _runs.RunChanged += Raise.EventWith(new AgentRunChangedEventArgs(_runId, AgentRunState.Running, null));

        await _timeline.DidNotReceive().GetForRunAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        _ = vm;
    }

    [Fact]
    public async Task ATruncatedTraceSetsTheNote_AndIsNotRenderedAsARow()
    {
        _timeline.GetForRunAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<AgentTimelineEvent>
        {
            Row(1, AgentTimelineEventKind.ToolCall, ToolGateDecision.ApprovedOnce),
            Row(2, AgentTimelineEventKind.TraceTruncated, ToolGateDecision.Unknown),
        });

        var vm = CreateVm();
        await vm.LoadTimelineAsync();

        Assert.True(vm.IsTimelineTruncated);
        Assert.NotNull(vm.TimelineNote);
        // The marker is a statement about the trace, so it is a note and NOT one of the rows.
        var row = Assert.Single(vm.Timeline);
        Assert.Equal("write_file", row.ToolName);
        Assert.False(vm.HasNoTimeline);
    }

    [Fact]
    public async Task AFailedOutcomeCarriesTheLocalizedSuffix()
    {
        _timeline.GetForRunAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<AgentTimelineEvent>
        {
            Row(1, AgentTimelineEventKind.ToolCall, ToolGateDecision.ApprovedOnce, AgentTimelineOutcome.Error),
            Row(2, AgentTimelineEventKind.ToolCall, ToolGateDecision.ApprovedOnce),
        });

        var vm = CreateVm();
        await vm.LoadTimelineAsync();

        Assert.Equal("Run_Timeline_Outcome_Failed", vm.Timeline[0].OutcomeSuffix);
        Assert.Null(vm.Timeline[1].OutcomeSuffix);
    }

    [Fact]
    public async Task ARowIsAttributedToItsStep_WhileThatStepIsStillInThePlan()
    {
        // StepLabel and OutcomeSuffix are both RENDERED now (the row template binds five columns, not three),
        // so the projection that fills them is load-bearing rather than decorative.
        var stepId = Guid.NewGuid();
        _runs.GetAsync(_runId, Arg.Any<CancellationToken>()).Returns(new AgentRun
        {
            Id = _runId,
            State = AgentRunState.Running,
            Plan = [new AgentStep { Id = stepId, Ordinal = 0, Title = "Read the notes", Status = AgentStepStatus.Running }],
        });
        _timeline.GetForRunAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<AgentTimelineEvent>
        {
            Row(1, AgentTimelineEventKind.ToolCall, ToolGateDecision.ApprovedOnce, stepId: stepId),
            Row(2, AgentTimelineEventKind.ToolCall, ToolGateDecision.ApprovedOnce),
        });

        var vm = CreateVm();
        await vm.LoadTimelineAsync();

        Assert.Equal("Run_Timeline_Step", vm.Timeline[0].StepLabel); // the stubbed Format echoes the key
        // A replanned-away step (here: a row with no step at all) stays unattributed rather than guessing.
        Assert.Null(vm.Timeline[1].StepLabel);
    }

    [Theory]
    [MemberData(nameof(EveryDecision))]
    public void EveryDecisionOrdinalMapsToALabel(ToolGateDecision decision)
    {
        // Driven off Enum.GetValues, not a literal range, so a 13th member cannot be missed the way the 11th
        // was when this batch's spec was written.
        Assert.False(string.IsNullOrWhiteSpace(RunProgressViewModel.DecisionLabelKey(decision)));
    }

    /// <summary>
    /// hermes #16. <c>EveryDecisionOrdinalMapsToALabel</c> above cannot see this: it asserts only that a label
    /// is non-empty, so a decision with no arm passes it on the <c>Run_Timeline_Decision_Unknown</c>
    /// fall-through. A run that stopped to ask a person is the ONE row that user is expected to answer, so
    /// "unknown" — or "denied", which is the neighbouring wrong answer — is worse than most defaults.
    /// <para>Neutralize: delete the <c>ParkedForApproval</c> arm from <c>DecisionLabelKey</c> → red.</para>
    /// </summary>
    [Fact]
    public void AParkedForApprovalRow_IsLabelledAsAwaiting_NotAsUnknownAndNotAsDenied()
    {
        var key = RunProgressViewModel.DecisionLabelKey(ToolGateDecision.ParkedForApproval);

        Assert.Equal("Run_Timeline_Decision_AwaitingApproval", key);
        Assert.NotEqual("Run_Timeline_Decision_Unknown", key);
        Assert.NotEqual(RunProgressViewModel.DecisionLabelKey(ToolGateDecision.DeniedNotGranted), key);
    }

    public static TheoryData<ToolGateDecision> EveryDecision()
    {
        var data = new TheoryData<ToolGateDecision>();
        foreach (var d in Enum.GetValues<ToolGateDecision>())
            data.Add(d);
        // …plus an ordinal no build knows: the append-only render guarantee is that it labels, never throws.
        data.Add((ToolGateDecision)99);
        return data;
    }

    [Fact]
    public void OutOfRangeAndUnknownOrdinalsRenderAsUnknown()
    {
        Assert.Equal("Run_Timeline_Decision_Unknown", RunProgressViewModel.DecisionLabelKey(ToolGateDecision.Unknown));
        Assert.Equal("Run_Timeline_Decision_Unknown", RunProgressViewModel.DecisionLabelKey((ToolGateDecision)99));
    }

    [Fact]
    public void TimelineRowsCarryNoPathAndNoPayload()
    {
        // A later FilePath / Arguments / ResultText property fails HERE, which is the point: the store holds
        // none of those, so the row must not invent a place to put them.
        var actual = typeof(TimelineRowViewModel)
            .GetProperties()
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[] { "DecisionLabel", "OutcomeSuffix", "StepLabel", "TimeLabel", "ToolName" },
            actual);
    }

    [Fact]
    public async Task ANullTimelineServiceRendersAsEmpty()
    {
        // The trailing-optional ctor argument: production passes one, RunProgressViewModelTests does not, and
        // neither path may throw.
        var vm = new RunProgressViewModel(_runs, _runId, _loc, _resume, NullLogger.Instance);
        await vm.LoadTimelineAsync();

        Assert.True(vm.HasNoTimeline);
        Assert.Empty(vm.Timeline);
    }

    private RunProgressViewModel CreateVm()
    {
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());
        return new RunProgressViewModel(_runs, _runId, _loc, _resume, NullLogger.Instance, _timeline);
    }

    private AgentTimelineEvent Row(
        long seq, AgentTimelineEventKind kind, ToolGateDecision decision,
        AgentTimelineOutcome outcome = AgentTimelineOutcome.Ok, Guid? stepId = null) => new(
        Id: Guid.NewGuid(),
        RunId: _runId,
        StepId: stepId,
        Seq: seq,
        Kind: kind,
        Surface: ToolGateSurface.Interactive,
        Decision: decision,
        Outcome: outcome,
        ToolName: kind == AgentTimelineEventKind.TraceTruncated ? string.Empty : "write_file",
        ToolClass: ToolClass.Files,
        PluginId: null,
        ArgsChars: 12,
        ResultChars: 20,
        DurationMs: 5,
        CreatedAt: DateTime.UtcNow);

    /// <summary>Runs Post callbacks inline so the projection is observable synchronously.</summary>
    private sealed class InlineSyncContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
        public override void Send(SendOrPostCallback d, object? state) => d(state);
    }
}
