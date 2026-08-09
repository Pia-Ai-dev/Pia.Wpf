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
        // Both rows are routine, so they land in the trace's second block, which reads NEWEST FIRST — hence the
        // seq-2 row above the seq-1 one. The ordering itself is pinned by
        // TheTraceSortsExceptionsFirst_ThenTheRestNewestFirst; this fact only needs both rows to be there.
        Assert.Collection(vm.Timeline,
            first => Assert.Equal("Run_Timeline_Decision_AutoApproved", first.DecisionLabel),
            second => Assert.Equal("Run_Timeline_Decision_Approved", second.DecisionLabel));
        Assert.False(vm.HasNoTimeline);
    }

    /// <summary>
    /// <b>REGRESSION.</b> The trace's reading order: every row that needs a person first — Awaiting approval,
    /// Denied, Blocked — then a rule, then the rest. Each block newest-first. A parked or refused call five
    /// hundred rows deep in a chronological list is a call nobody sees, which is the defect this ordering exists
    /// to remove.
    /// <para>
    /// The rule is carried by <c>ShowGroupSeparator</c> on the FIRST row below the exception block, and by no
    /// other row — asserted on every row, not just that one, because a separator on the wrong row (or on all of
    /// them) draws rules through the middle of the table.
    /// </para>
    /// <para>Neutralize: drop the <c>.Reverse()</c> in <c>ApplyTimelineAsync</c> → the two ordering legs red;
    /// concatenate <c>routine</c> before <c>exceptions</c> → every leg reds.</para>
    /// </summary>
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
            // Exceptions, newest first: the blocked call (seq 4) above the parked one (seq 2).
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
            // …then the rule, drawn by the first routine row, and the routine block newest first (seq 3, seq 1).
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

        // The summary beside the header: one pill per category that occurred, exceptions first, and the badge is
        // the first exception category — awaiting outranks refused, because it is the one still answerable.
        Assert.Collection(vm.DecisionPills,
            first => Assert.Equal("Run_Timeline_Pill_AwaitingApproval", first.Text),
            second => Assert.Equal("Run_Timeline_Pill_Blocked", second.Text),
            third => Assert.Equal("Run_Timeline_Pill_AutoApproved", third.Text));
        Assert.Equal("Run_Timeline_Pill_AwaitingApproval", vm.TimelineExceptionBadge);
        Assert.Equal(RunDecisionSeverity.Awaiting, vm.TimelineExceptionSeverity);
    }

    /// <summary>
    /// The live half of the tool-activity section: an event the watcher observed on THIS run triggers a trace
    /// reload, so the header pills and the expanded table keep up with the calls the chat shows. The control
    /// <see cref="TimelineIsNotLoadedByRunChanged"/> still holds — the reload is driven by the timeline's own
    /// append stream, not by run projections.
    /// </summary>
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

    /// <summary>Control: events for ANOTHER run (the watcher is a process-wide singleton) must not read this
    /// run's trace, and a settled run's frozen trace stops reloading altogether.</summary>
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

        // Settle the run: the terminal projection latches the trace (one last read), and later events read
        // nothing because a settled trace cannot change again.
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

    /// <summary>
    /// The control for the fact above: with no exception rows at all, NO row draws the rule. Without this, a
    /// separator that fired on the first row unconditionally would still pass the ordering fact.
    /// </summary>
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

        // Located by the suffix itself rather than by index: the table is no longer chronological (exceptions
        // first, then newest-first), and this fact is about the PROJECTION filling the column, not about where
        // the row sits. Both legs stay — exactly one row carries the suffix and exactly one does not.
        Assert.Equal(2, vm.Timeline.Count);
        var failed = Assert.Single(vm.Timeline, r => r.OutcomeSuffix is not null);
        Assert.Equal("Run_Timeline_Outcome_Failed", failed.OutcomeSuffix);
        Assert.Single(vm.Timeline, r => r.OutcomeSuffix is null);
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
        // The live run's priming read runs through the coalescing gate; await IT rather than loading a second
        // time, which would race the prime's apply under the inline sync context.
        await vm.TimelineLoadTask!;

        // Located by the label, not by index, for the reason AFailedOutcomeCarriesTheLocalizedSuffix records: the
        // table is no longer chronological, and this fact is about the attribution, not the row's position.
        Assert.Equal(2, vm.Timeline.Count);
        var attributed = Assert.Single(vm.Timeline, r => r.StepLabel is not null);
        Assert.Equal("Run_Timeline_Step", attributed.StepLabel); // the stubbed Format echoes the key
        // A replanned-away step (here: a row with no step at all) stays unattributed rather than guessing.
        Assert.Single(vm.Timeline, r => r.StepLabel is null);
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

    /// <summary>
    /// hermes #15, and the same blind spot one batch later: <c>EveryDecisionOrdinalMapsToALabel</c> asserts
    /// only that the key is non-empty, so ordinals 13/14 pass it on the <c>Run_Timeline_Decision_Unknown</c>
    /// fall-through and BOTH new arms could be deleted with the whole suite green. The session tier is the
    /// one authority a user cannot find in Settings, so the run panel is where they read what happened —
    /// "unknown" for every session-granted call would make the tier invisible twice over.
    /// <para>
    /// Asserted as an EQUALITY against the two existing categories rather than just "not unknown": the arms
    /// are deliberate FOLDS (a session-granted call ran with nobody asked; a card answered "for this session"
    /// is a person saying yes), and a fold that landed in the wrong bucket is the other way to get this wrong.
    /// </para>
    /// <para>Neutralize: delete either <c>or ToolGateDecision.AutoApprovedSessionGrant</c> or
    /// <c>or ToolGateDecision.ApprovedForSession</c> from <c>DecisionLabelKey</c> → red.</para>
    /// </summary>
    [Fact]
    public void TheSessionTierDecisions_FoldIntoAutoApprovedAndApproved_NotIntoUnknown()
    {
        var granted = RunProgressViewModel.DecisionLabelKey(ToolGateDecision.AutoApprovedSessionGrant);
        var approved = RunProgressViewModel.DecisionLabelKey(ToolGateDecision.ApprovedForSession);

        // The call ran with nobody asked — same category as the standing grant it sits above in the resolver.
        Assert.Equal("Run_Timeline_Decision_AutoApproved", granted);
        Assert.Equal(RunProgressViewModel.DecisionLabelKey(ToolGateDecision.AutoApprovedStandingGrant), granted);

        // A person clicked "Allow this session" on THIS row — same category as the other two card answers.
        Assert.Equal("Run_Timeline_Decision_Approved", approved);
        Assert.Equal(RunProgressViewModel.DecisionLabelKey(ToolGateDecision.ApprovedOnce), approved);

        // …and neither may reach the fall-through, which is what the non-empty Theory above cannot see.
        Assert.NotEqual("Run_Timeline_Decision_Unknown", granted);
        Assert.NotEqual("Run_Timeline_Decision_Unknown", approved);
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
        //
        // The exact-set form is what forces every addition through this assertion, and the signal-band redesign is
        // the first one to take it up: Severity, IsException and ShowGroupSeparator are PRESENTATION over the
        // decision the row already carries — how loudly it reads, and whether it draws the rule that separates the
        // exception block from the rest. None of them says anything new about the call, and in particular none of
        // them names its target, which is the property this fact exists to protect.
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
        // The trailing-optional ctor argument: production passes one, RunProgressViewModelTests does not, and
        // neither path may throw.
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

    /// <summary>Runs Post callbacks inline so the projection is observable synchronously.</summary>
}
