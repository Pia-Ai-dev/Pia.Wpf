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

    /// <summary>A parked or refused call five hundred rows deep in a chronological list is a call nobody sees.
    /// This run is not parked, so its park row is history and the only exception left is the blocked call.</summary>
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
            // The exception block is one row now, so seq 4 stands alone.
            first =>
            {
                Assert.Equal("Run_Timeline_Decision_Blocked", first.DecisionLabel);
                Assert.Equal(RunDecisionSeverity.Refused, first.Severity);
                Assert.False(first.ShowGroupSeparator);
            },
            // …then the rule on the first routine row, and that block newest first (seq 3, 2, 1).
            second =>
            {
                Assert.Equal("Run_Timeline_Decision_AutoApproved", second.DecisionLabel);
                Assert.Equal(RunDecisionSeverity.Routine, second.Severity);
                Assert.True(second.ShowGroupSeparator);
            },
            third =>
            {
                Assert.Equal("Run_Timeline_Decision_NotExecuted", third.DecisionLabel);
                Assert.Equal(RunDecisionSeverity.Routine, third.Severity);
                Assert.False(third.ShowGroupSeparator);
            },
            fourth =>
            {
                Assert.Equal("Run_Timeline_Decision_AutoApproved", fourth.DecisionLabel);
                Assert.Equal(RunDecisionSeverity.Routine, fourth.Severity);
                Assert.False(fourth.ShowGroupSeparator);
            });

        // The badge is the first exception category with a count, and on a run nobody is parked on there is no
        // awaiting category to outrank the blocked one.
        Assert.Collection(vm.DecisionPills,
            first => Assert.Equal("Run_Timeline_Pill_Blocked", first.Text),
            second => Assert.Equal("Run_Timeline_Pill_NotExecuted", second.Text),
            third => Assert.Equal("Run_Timeline_Pill_AutoApproved", third.Text));
        Assert.Equal("Run_Timeline_Pill_Blocked", vm.TimelineExceptionBadge);
        Assert.Equal(RunDecisionSeverity.Refused, vm.TimelineExceptionSeverity);
    }

    /// <summary>
    /// The gate writes the park row seconds before the run reaches WaitingForInput, so the load that first sees
    /// that row runs with IsToolApprovalPause false, and the projection that sets it true reads no trace. A pill
    /// derived on either path alone would never appear.
    /// </summary>
    [Fact]
    public async Task AParkThatLandsAfterTheTraceRead_StillLightsTheAwaitingPill_WithNoSecondStoreRead()
    {
        _timeline.GetForRunAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<AgentTimelineEvent>
        {
            Row(1, AgentTimelineEventKind.ToolCall, ToolGateDecision.AutoApprovedPolicy),
            Row(2, AgentTimelineEventKind.ToolCall, ToolGateDecision.ParkedForApproval),
        });
        StubRun(AgentRunState.Running);

        var vm = CreateVm();
        await vm.TimelineLoadTask!; // the live run's priming read

        // The 41-second window between the audit row and the pause.
        Assert.DoesNotContain(vm.DecisionPills, p => p.Text == "Run_Timeline_Pill_AwaitingApproval");
        Assert.Single(vm.Timeline, r => r.DecisionLabel == "Run_Timeline_Decision_NotExecuted");

        StubRun(AgentRunState.WaitingForInput, ToolApprovalEnvelope("write_file"));
        await vm.RefreshAsync();

        Assert.True(vm.IsToolApprovalPause);
        // Equality, not "at most one": an absent pill is the defect this test exists for.
        Assert.Single(vm.DecisionPills, p => p.Text == "Run_Timeline_Pill_AwaitingApproval");
        Assert.Equal("Run_Timeline_Pill_AwaitingApproval", vm.TimelineExceptionBadge);
        Assert.Equal(RunDecisionSeverity.Awaiting, vm.TimelineExceptionSeverity);
        Assert.Equal("Run_Timeline_Decision_AwaitingApproval", vm.Timeline[0].DecisionLabel);
        // One read total, so the pill came from the cached snapshot rather than a re-read.
        await _timeline.Received(1).GetForRunAsync(_runId, Arg.Any<CancellationToken>());
        vm.Dispose();
    }

    /// <summary>Without the identity guard a refactor silently reintroduces one row rebuild per RunChanged, and
    /// a run emits roughly five hundred of them.</summary>
    [Fact]
    public async Task ANonParkProjection_DoesNotRebuildTheTraceRows()
    {
        _timeline.GetForRunAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<AgentTimelineEvent>
        {
            Row(1, AgentTimelineEventKind.ToolCall, ToolGateDecision.ApprovedOnce),
            Row(2, AgentTimelineEventKind.ToolCall, ToolGateDecision.AutoApprovedPolicy),
        });
        StubRun(AgentRunState.Running);

        var vm = CreateVm();
        await vm.TimelineLoadTask!;
        var row = vm.Timeline[0];
        var pill = vm.DecisionPills[0];

        for (var i = 0; i < 5; i++)
            _runs.RunChanged += Raise.EventWith(new AgentRunChangedEventArgs(_runId, AgentRunState.Running, null));

        Assert.Same(row, vm.Timeline[0]);
        Assert.Same(pill, vm.DecisionPills[0]);
        await _timeline.Received(1).GetForRunAsync(_runId, Arg.Any<CancellationToken>());
        vm.Dispose();
    }

    /// <summary>The store is per-step and first-call-wins, so a run re-parked on the same tool is stopped on the
    /// newer call only — the older park row already had its answer.</summary>
    [Fact]
    public async Task ASecondParkOnTheSameTool_LeavesOnlyTheNewerRowAwaiting()
    {
        var older = new DateTime(2026, 8, 31, 12, 5, 0, DateTimeKind.Utc);
        var newer = new DateTime(2026, 8, 31, 12, 47, 0, DateTimeKind.Utc);
        _timeline.GetForRunAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<AgentTimelineEvent>
        {
            Row(1, AgentTimelineEventKind.ToolCall, ToolGateDecision.ParkedForApproval, createdAt: older),
            Row(2, AgentTimelineEventKind.ToolCall, ToolGateDecision.AutoApprovedPolicy),
            Row(3, AgentTimelineEventKind.ToolCall, ToolGateDecision.ParkedForApproval, createdAt: newer),
        });
        StubRun(AgentRunState.WaitingForInput, ToolApprovalEnvelope("write_file"));

        var vm = CreateVm();
        await vm.TimelineLoadTask!;

        var awaiting = Assert.Single(vm.Timeline, r => r.DecisionLabel == "Run_Timeline_Decision_AwaitingApproval");
        Assert.Equal(newer.ToLocalTime().ToString("t"), awaiting.TimeLabel);
        Assert.Equal(RunDecisionSeverity.Awaiting, awaiting.Severity);

        var superseded = Assert.Single(vm.Timeline, r => r.DecisionLabel == "Run_Timeline_Decision_NotExecuted");
        Assert.Equal(older.ToLocalTime().ToString("t"), superseded.TimeLabel);
        Assert.Equal(RunDecisionSeverity.Routine, superseded.Severity);

        Assert.Single(vm.DecisionPills, p => p.Text == "Run_Timeline_Pill_AwaitingApproval");
        vm.Dispose();
    }

    /// <summary>Non-vacuity for the tool-name term: without it every park row on a parked run would read awaiting.</summary>
    [Fact]
    public async Task AParkRowOnARunParkedOnADifferentTool_ReadsNotExecuted()
    {
        _timeline.GetForRunAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<AgentTimelineEvent>
        {
            Row(1, AgentTimelineEventKind.ToolCall, ToolGateDecision.ParkedForApproval),
            Row(2, AgentTimelineEventKind.ToolCall, ToolGateDecision.ParkedForApproval, toolName: "remember"),
        });
        StubRun(AgentRunState.WaitingForInput, ToolApprovalEnvelope("remember"));

        var vm = CreateVm();
        await vm.TimelineLoadTask!;

        // Located by tool name, never by index: the table is not chronological.
        var awaiting = Assert.Single(vm.Timeline, r => r.ToolName == "remember");
        Assert.Equal("Run_Timeline_Decision_AwaitingApproval", awaiting.DecisionLabel);
        Assert.Equal(RunDecisionSeverity.Awaiting, awaiting.Severity);

        var superseded = Assert.Single(vm.Timeline, r => r.ToolName == "write_file");
        Assert.Equal("Run_Timeline_Decision_NotExecuted", superseded.DecisionLabel);
        Assert.Equal(RunDecisionSeverity.Routine, superseded.Severity);
        vm.Dispose();
    }

    /// <summary>The reported defect: a finished run claiming approvals are still outstanding. WaitingForInput is
    /// disjoint from the terminal set, so the count is zero by construction rather than by a clamp.</summary>
    [Theory]
    [InlineData(AgentRunState.Completed)]
    [InlineData(AgentRunState.Failed)]
    [InlineData(AgentRunState.Cancelled)]
    public async Task ATerminalRunShowsNoAwaitingPill_AndItsParkRowsReadNotExecuted(AgentRunState state)
    {
        _timeline.GetForRunAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<AgentTimelineEvent>
        {
            Row(1, AgentTimelineEventKind.ToolCall, ToolGateDecision.ParkedForApproval),
            Row(2, AgentTimelineEventKind.ToolCall, ToolGateDecision.AutoApprovedPolicy),
            Row(3, AgentTimelineEventKind.ToolCall, ToolGateDecision.AutoApprovedPolicy),
            Row(4, AgentTimelineEventKind.ToolCall, ToolGateDecision.ParkedForApproval, toolName: "remember"),
            Row(5, AgentTimelineEventKind.ToolCall, ToolGateDecision.AutoApprovedPolicy),
            Row(6, AgentTimelineEventKind.ToolCall, ToolGateDecision.AutoApprovedPolicy),
        });
        StubRun(state);

        var vm = CreateVm();
        await vm.TimelineLoadTask!;

        Assert.False(vm.IsToolApprovalPause);
        Assert.DoesNotContain(vm.DecisionPills, p => p.Text == "Run_Timeline_Pill_AwaitingApproval");
        Assert.Null(vm.TimelineExceptionBadge);
        Assert.False(vm.HasTimelineExceptionBadge);
        Assert.Equal(RunDecisionSeverity.Routine, vm.TimelineExceptionSeverity);
        Assert.All(vm.Timeline, r => Assert.False(r.IsException));

        // The fact is kept, only the copy and the palette change.
        var notExecuted = vm.Timeline.Where(r => r.DecisionLabel == "Run_Timeline_Decision_NotExecuted").ToList();
        Assert.Equal(2, notExecuted.Count);
        Assert.Single(vm.DecisionPills, p => p.Text == "Run_Timeline_Pill_NotExecuted");
        vm.Dispose();
    }

    /// <summary>Same VM, park then settle. A terminal RefreshAsync does latch one extra trace read through
    /// _settledTraceRead, which is why the no-re-read fact lives in the projection test and not here.</summary>
    [Fact]
    public async Task AParkedRunThatSettles_DropsTheAwaitingPillWithoutASecondStoreRead()
    {
        _timeline.GetForRunAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<AgentTimelineEvent>
        {
            Row(1, AgentTimelineEventKind.ToolCall, ToolGateDecision.AutoApprovedPolicy),
            Row(2, AgentTimelineEventKind.ToolCall, ToolGateDecision.ParkedForApproval),
        });
        StubRun(AgentRunState.WaitingForInput, ToolApprovalEnvelope("write_file"));

        var vm = CreateVm();
        await vm.TimelineLoadTask!;
        Assert.Single(vm.DecisionPills, p => p.Text == "Run_Timeline_Pill_AwaitingApproval");

        StubRun(AgentRunState.Completed);
        await vm.RefreshAsync();

        Assert.False(vm.IsToolApprovalPause);
        Assert.DoesNotContain(vm.DecisionPills, p => p.Text == "Run_Timeline_Pill_AwaitingApproval");
        Assert.Null(vm.TimelineExceptionBadge);
        Assert.Single(vm.Timeline, r => r.DecisionLabel == "Run_Timeline_Decision_NotExecuted");
        vm.Dispose();
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

    private void StubRun(AgentRunState state, string? extraJson = null) =>
        _runs.GetAsync(_runId, Arg.Any<CancellationToken>()).Returns(new AgentRun
        {
            Id = _runId,
            State = state,
            Plan = [],
            ExtraJson = extraJson,
        });

    private static string ToolApprovalEnvelope(string tool) =>
        $"{{\"paused\":true,\"reason\":\"tool-approval\",\"tool\":\"{tool}\"}}";

    private RunProgressViewModel CreateVm(ITimelineWatcher? watcher = null)
    {
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());
        return new RunProgressViewModel(_runs, _runId, _loc, _resume, NullLogger.Instance, _timeline,
            timelineWatcher: watcher);
    }

    private AgentTimelineEvent Row(
        long seq, AgentTimelineEventKind kind, ToolGateDecision decision,
        AgentTimelineOutcome outcome = AgentTimelineOutcome.Ok, Guid? stepId = null,
        string? toolName = null, DateTime? createdAt = null) => new(
        Id: Guid.NewGuid(),
        RunId: _runId,
        StepId: stepId,
        Seq: seq,
        Kind: kind,
        Surface: ToolGateSurface.Interactive,
        Decision: decision,
        Outcome: outcome,
        ToolName: kind == AgentTimelineEventKind.TraceTruncated ? string.Empty : toolName ?? "write_file",
        ToolClass: ToolClass.Files,
        PluginId: null,
        ArgsChars: 12,
        ResultChars: 20,
        DurationMs: 5,
        CreatedAt: createdAt ?? DateTime.UtcNow,
        ToolCallId: null, Round: null, StepOrdinal: null, RequestedAt: null, DecidedAt: null);
}
