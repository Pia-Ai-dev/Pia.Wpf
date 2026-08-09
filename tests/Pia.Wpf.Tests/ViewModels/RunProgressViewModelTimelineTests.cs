using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>The trace stays outside live projection: a run emits up to ~500 events and none may drive a re-projection.</summary>
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

        // The read hops off the caller's thread, so asserting straight after the property set would race it.
        vm.IsTimelineExpanded = true;
        await vm.TimelineLoadTask!;
        await _timeline.Received(1).GetForRunAsync(_runId, Arg.Any<CancellationToken>());
        Assert.True(vm.HasNoTimeline);

        // A read before the run's first gated call must not pin "nothing was recorded" for the session.
        _timeline.GetForRunAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<AgentTimelineEvent>
        {
            Row(1, AgentTimelineEventKind.ToolCall, ToolGateDecision.ApprovedOnce),
            Row(2, AgentTimelineEventKind.ToolCall, ToolGateDecision.AutoApprovedPolicy),
        });

        vm.IsTimelineExpanded = false;
        vm.IsTimelineExpanded = true;
        await vm.TimelineLoadTask!;

        await _timeline.Received(2).GetForRunAsync(_runId, Arg.Any<CancellationToken>());
        // Both rows are routine, so the second block reads newest-first — hence seq 2 above seq 1.
        Assert.Collection(vm.Timeline,
            first => Assert.Equal("Run_Timeline_Decision_AutoApproved", first.DecisionLabel),
            second => Assert.Equal("Run_Timeline_Decision_Approved", second.DecisionLabel));
        Assert.False(vm.HasNoTimeline);
    }

    /// <summary>A parked or refused call five hundred rows deep in a chronological list is a call nobody sees.</summary>
    [Fact]
    public async Task TheTraceSortsExceptionsFirst_ThenTheRestNewestFirst()
    {
        _timeline.GetForRunAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<AgentTimelineEvent>
        {
            Row(1, AgentTimelineEventKind.ToolCall, ToolGateDecision.AutoApprovedPolicy),
            Row(2, AgentTimelineEventKind.ToolCall, ToolGateDecision.ParkedForApproval),
            Row(3, AgentTimelineEventKind.ToolCall, ToolGateDecision.AutoApprovedPolicy),
            Row(4, AgentTimelineEventKind.ToolCall, ToolGateDecision.DeniedDestructiveFloor),
        });

        var vm = CreateVm();
        await vm.LoadTimelineAsync();

        Assert.Collection(vm.Timeline,
            // Exceptions, newest first: seq 4 above seq 2.
            first =>
            {
                Assert.Equal("Run_Timeline_Decision_Blocked", first.DecisionLabel);
                Assert.Equal(RunDecisionSeverity.Refused, first.Severity);
                Assert.False(first.ShowGroupSeparator);
            },
            second =>
            {
                Assert.Equal("Run_Timeline_Decision_AwaitingApproval", second.DecisionLabel);
                Assert.Equal(RunDecisionSeverity.Awaiting, second.Severity);
                Assert.False(second.ShowGroupSeparator);
            },
            // …then the rule on the first routine row, and that block newest first (seq 3, seq 1).
            third =>
            {
                Assert.Equal("Run_Timeline_Decision_AutoApproved", third.DecisionLabel);
                Assert.Equal(RunDecisionSeverity.Routine, third.Severity);
                Assert.True(third.ShowGroupSeparator);
            },
            fourth =>
            {
                Assert.Equal("Run_Timeline_Decision_AutoApproved", fourth.DecisionLabel);
                Assert.False(fourth.ShowGroupSeparator);
            });

        // The badge is the first exception category: awaiting outranks refused, being the one still answerable.
        Assert.Collection(vm.DecisionPills,
            first => Assert.Equal("Run_Timeline_Pill_AwaitingApproval", first.Text),
            second => Assert.Equal("Run_Timeline_Pill_Blocked", second.Text),
            third => Assert.Equal("Run_Timeline_Pill_AutoApproved", third.Text));
        Assert.Equal("Run_Timeline_Pill_AwaitingApproval", vm.TimelineExceptionBadge);
        Assert.Equal(RunDecisionSeverity.Awaiting, vm.TimelineExceptionSeverity);
    }

    /// <summary>The reload is driven by the timeline's own append stream, not by run projections.</summary>
    [Fact]
    public async Task ATimelineEventOnALiveRun_TriggersAReload()
    {
        _runs.GetAsync(_runId, Arg.Any<CancellationToken>()).Returns(new AgentRun
        {
            Id = _runId,
            State = AgentRunState.Running,
            Plan = [],
        });
        var watcher = new RunTimelineWatcher();
        var vm = CreateVm(watcher);
        await vm.RefreshAsync();
        await vm.TimelineLoadTask!; // the live run's priming read
        await _timeline.Received(1).GetForRunAsync(_runId, Arg.Any<CancellationToken>());

        watcher.OnTimelineEvent(Row(1, AgentTimelineEventKind.ToolCall, ToolGateDecision.ApprovedOnce));
        await vm.TimelineLoadTask!;

        await _timeline.Received(2).GetForRunAsync(_runId, Arg.Any<CancellationToken>());
        vm.Dispose();
    }

    /// <summary>The watcher is process-wide, so another run's events must not read this run's trace.</summary>
    [Fact]
    public async Task ForeignAndSettledRuns_DoNotReloadTheTrace()
    {
        _runs.GetAsync(_runId, Arg.Any<CancellationToken>()).Returns(new AgentRun
        {
            Id = _runId,
            State = AgentRunState.Running,
            Plan = [],
        });
        var watcher = new RunTimelineWatcher();
        var vm = CreateVm(watcher);
        await vm.RefreshAsync();
        await vm.TimelineLoadTask!;

        watcher.OnTimelineEvent(Row(1, AgentTimelineEventKind.ToolCall, ToolGateDecision.ApprovedOnce)
            with { RunId = Guid.NewGuid() });
        await Task.Yield();
        await _timeline.Received(1).GetForRunAsync(_runId, Arg.Any<CancellationToken>());

        // The terminal projection latches the trace with one last read; a settled trace cannot change again.
        _runs.GetAsync(_runId, Arg.Any<CancellationToken>()).Returns(new AgentRun
        {
            Id = _runId,
            State = AgentRunState.Completed,
            Plan = [],
        });
        await vm.RefreshAsync();
        await _timeline.Received(2).GetForRunAsync(_runId, Arg.Any<CancellationToken>());

        watcher.OnTimelineEvent(Row(2, AgentTimelineEventKind.ToolCall, ToolGateDecision.ApprovedOnce));
        await Task.Yield();
        await _timeline.Received(2).GetForRunAsync(_runId, Arg.Any<CancellationToken>());
        vm.Dispose();
    }

    /// <summary>Non-vacuity for the ordering fact: a separator that fired unconditionally on the first row would still pass it.</summary>
    [Fact]
    public async Task WithNoExceptions_NoRowDrawsTheGroupRule()
    {
        _timeline.GetForRunAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<AgentTimelineEvent>
        {
            Row(1, AgentTimelineEventKind.ToolCall, ToolGateDecision.AutoApprovedPolicy),
            Row(2, AgentTimelineEventKind.ToolCall, ToolGateDecision.ApprovedOnce),
        });

        var vm = CreateVm();
        await vm.LoadTimelineAsync();

        Assert.Equal(2, vm.Timeline.Count);
        Assert.All(vm.Timeline, r => Assert.False(r.ShowGroupSeparator));
        Assert.All(vm.Timeline, r => Assert.False(r.IsException));
        Assert.Null(vm.TimelineExceptionBadge);
    }

    [Fact]
    public async Task AFailedReadSaysSo_AndIsNeverRenderedAsAnEmptyTrace()
    {
        _timeline.GetForRunAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("the store is gone"));

        var vm = CreateVm();
        await vm.LoadTimelineAsync();

        // "No tool decisions were recorded" is a positive claim, and this read proved nothing of the sort.
        Assert.True(vm.HasTimelineReadError);
        Assert.False(vm.HasNoTimeline);
        Assert.Empty(vm.Timeline);

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

        // Located by the suffix, not by index: the table is no longer chronological.
        Assert.Equal(2, vm.Timeline.Count);
        var failed = Assert.Single(vm.Timeline, r => r.OutcomeSuffix is not null);
        Assert.Equal("Run_Timeline_Outcome_Failed", failed.OutcomeSuffix);
        Assert.Single(vm.Timeline, r => r.OutcomeSuffix is null);
    }

    [Fact]
    public async Task ARowIsAttributedToItsStep_WhileThatStepIsStillInThePlan()
    {
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
        // Await the priming read rather than loading a second time, which would race its apply.
        await vm.TimelineLoadTask!;

        Assert.Equal(2, vm.Timeline.Count);
        var attributed = Assert.Single(vm.Timeline, r => r.StepLabel is not null);
        Assert.Equal("Run_Timeline_Step", attributed.StepLabel); // the stubbed Format echoes the key
        Assert.Single(vm.Timeline, r => r.StepLabel is null);
    }

    [Theory]
    [MemberData(nameof(EveryDecision))]
    public void EveryDecisionOrdinalMapsToALabel(ToolGateDecision decision)
    {
        Assert.False(string.IsNullOrWhiteSpace(RunProgressViewModel.DecisionLabelKey(decision)));
    }

    /// <summary>The theory above only asserts a non-empty key, so a missing arm passes it on the Unknown fall-through.</summary>
    [Fact]
    public void AParkedForApprovalRow_IsLabelledAsAwaiting_NotAsUnknownAndNotAsDenied()
    {
        var key = RunProgressViewModel.DecisionLabelKey(ToolGateDecision.ParkedForApproval);

        Assert.Equal("Run_Timeline_Decision_AwaitingApproval", key);
        Assert.NotEqual("Run_Timeline_Decision_Unknown", key);
        Assert.NotEqual(RunProgressViewModel.DecisionLabelKey(ToolGateDecision.DeniedNotGranted), key);
    }

    /// <summary>Asserted as equality against the existing categories, not just "not unknown": a fold into the
    /// wrong bucket is the other way to get this wrong.</summary>
    [Fact]
    public void TheSessionTierDecisions_FoldIntoAutoApprovedAndApproved_NotIntoUnknown()
    {
        var granted = RunProgressViewModel.DecisionLabelKey(ToolGateDecision.AutoApprovedSessionGrant);
        var approved = RunProgressViewModel.DecisionLabelKey(ToolGateDecision.ApprovedForSession);

        Assert.Equal("Run_Timeline_Decision_AutoApproved", granted);
        Assert.Equal(RunProgressViewModel.DecisionLabelKey(ToolGateDecision.AutoApprovedStandingGrant), granted);

        Assert.Equal("Run_Timeline_Decision_Approved", approved);
        Assert.Equal(RunProgressViewModel.DecisionLabelKey(ToolGateDecision.ApprovedOnce), approved);

        Assert.NotEqual("Run_Timeline_Decision_Unknown", granted);
        Assert.NotEqual("Run_Timeline_Decision_Unknown", approved);
    }

    public static TheoryData<ToolGateDecision> EveryDecision()
    {
        var data = new TheoryData<ToolGateDecision>();
        foreach (var d in Enum.GetValues<ToolGateDecision>())
            data.Add(d);
        // An ordinal no build knows: the render must label it, never throw.
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
        // The exact-set form forces every new property through this assertion: the store holds no path and no
        // payload, so a row must not invent a place to put one.
        var actual = typeof(TimelineRowViewModel)
            .GetProperties()
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "DecisionLabel", "IsException", "OutcomeSuffix", "Severity", "ShowGroupSeparator",
                "StepLabel", "TimeLabel", "ToolName",
            },
            actual);
    }

    [Fact]
    public async Task ANullTimelineServiceRendersAsEmpty()
    {
        // The timeline service is trailing-optional: production passes one, other tests do not, and neither
        // path may throw.
        var vm = new RunProgressViewModel(_runs, _runId, _loc, _resume, NullLogger.Instance);
        await vm.LoadTimelineAsync();

        Assert.True(vm.HasNoTimeline);
        Assert.Empty(vm.Timeline);
    }

    private RunProgressViewModel CreateVm(ITimelineWatcher? watcher = null)
    {
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());
        return new RunProgressViewModel(_runs, _runId, _loc, _resume, NullLogger.Instance, _timeline,
            timelineWatcher: watcher);
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
        CreatedAt: DateTime.UtcNow,
        ToolCallId: null, Round: null, StepOrdinal: null, RequestedAt: null, DecidedAt: null);
}
