using System.Collections.Immutable;
using System.Reflection;
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
/// Batch 07 D17 — the panel's sub-agent list and its parent→child drill-down. Deliberately NOT a merged
/// timeline: <c>Seq</c> is monotonic only within a run id, each child gets its own 500-event cap, and
/// <c>CreatedAt</c> is explicitly rejected as an ordering source, so the rows are two per-run views side by side.
/// </summary>
public sealed class RunProgressViewModelChildrenTests
{
    private readonly Guid _runId = Guid.NewGuid();
    private readonly IAgentRunService _runs = Substitute.For<IAgentRunService>();
    private readonly IAgentTimelineService _timeline = Substitute.For<IAgentTimelineService>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly IAgentRunResumeService _resume = Substitute.For<IAgentRunResumeService>();

    public RunProgressViewModelChildrenTests()
    {
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        _loc.Format(Arg.Any<string>(), Arg.Any<object[]>())
            .Returns(ci => (string)ci[0] + ":" + string.Join(",", (object[])ci[1]));
        _runs.GetAsync(_runId, Arg.Any<CancellationToken>()).Returns(new AgentRun { Id = _runId, State = AgentRunState.Running });
        _runs.GetChildRunsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<AgentRun>());
        _timeline.GetForRunAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<AgentTimelineEvent>());
    }

    /// <summary>
    /// T-CHILD-VM-1. The projection: one row per child, its state mapped through the SAME map the parent's chip
    /// uses, its own token totals off its own ledger, and the localized "N of M finished" line.
    /// </summary>
    [Fact]
    public async Task ChildrenAreProjectedWithStateTokensAndACount()
    {
        var done = Guid.NewGuid();
        var live = Guid.NewGuid();
        _runs.GetChildRunsAsync(_runId, Arg.Any<CancellationToken>()).Returns(new List<AgentRun>
        {
            Child(done, AgentRunState.Completed, "summarize the notes", input: 120, output: 8),
            Child(live, AgentRunState.Running, "check the numbers"),
        });

        var vm = CreateVm();
        await vm.RefreshAsync();

        Assert.True(vm.HasChildren);
        Assert.Collection(vm.Children,
            first =>
            {
                Assert.Equal(done, first.RunId);
                Assert.Equal("summarize the notes", first.Title);
                Assert.Equal(RunProgressState.Completed, first.State);
                Assert.Equal(120, first.InputTokens);
                Assert.Equal(8, first.OutputTokens);
                Assert.True(first.IsFinished);
            },
            second =>
            {
                Assert.Equal(RunProgressState.Running, second.State);
                Assert.False(second.IsFinished);
            });

        // The stubbed Format echoes "key:args", so the count really is 1 of 2 rather than any two numbers.
        Assert.Equal("Run_Children_Count:1,2", vm.ChildrenNote);
    }

    /// <summary>
    /// T-CHILD-VM-2, <b>GUARD</b>. An ordinary run delegates nothing, and the whole section stays hidden — the
    /// panel must be byte-identical to the pre-Batch-07 one for every run a build with no persona roster produces.
    /// </summary>
    [Fact]
    public async Task AChildlessRunShowsNothing()
    {
        var vm = CreateVm();
        await vm.RefreshAsync();

        Assert.False(vm.HasChildren);
        Assert.Empty(vm.Children);
        Assert.Null(vm.ChildrenNote);
    }

    /// <summary>
    /// T-CHILD-VM-3, <b>REGRESSION</b>. <c>OnRunChanged</c>'s filter must accept a CHILD's run id. Without the
    /// widened filter every child event is dropped, the rows freeze at whatever the first projection saw, and a
    /// fan-out renders as permanently unfinished with nothing failing.
    /// </summary>
    [Fact]
    public async Task AChildsRunChangedIsProjected_NotFilteredOut()
    {
        var childId = Guid.NewGuid();
        _runs.GetChildRunsAsync(_runId, Arg.Any<CancellationToken>()).Returns(new List<AgentRun>
        {
            Child(childId, AgentRunState.Running, "work"),
        });

        var vm = CreateVm();
        await vm.RefreshAsync(); // seeds the id snapshot the filter reads
        Assert.Equal(RunProgressState.Running, Assert.Single(vm.Children).State);

        _runs.GetChildRunsAsync(_runId, Arg.Any<CancellationToken>()).Returns(new List<AgentRun>
        {
            Child(childId, AgentRunState.Completed, "work", input: 5, output: 1),
        });

        // The event carries the CHILD's id — the case the pre-Batch-07 filter dropped.
        _runs.RunChanged += Raise.EventWith(new AgentRunChangedEventArgs(childId, AgentRunState.Completed, null));

        var row = Assert.Single(vm.Children);
        Assert.Equal(RunProgressState.Completed, row.State);
        Assert.Equal(5, row.InputTokens);

        // Control: an unrelated run's event is still ignored, so the filter was WIDENED and not removed. Three
        // reads so far — the ctor's initial projection, the explicit refresh, and the child's event.
        await _runs.Received(3).GetChildRunsAsync(_runId, Arg.Any<CancellationToken>());
        _runs.RunChanged += Raise.EventWith(new AgentRunChangedEventArgs(Guid.NewGuid(), AgentRunState.Running, null));
        await _runs.Received(3).GetChildRunsAsync(_runId, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// T-CHILD-VM-4, <b>REGRESSION</b>. Rows are diffed by run id, never rebuilt: a rebuild on every
    /// <c>RunChanged</c> — and a live fan-out raises one per step, per state flip and per ledger write — would
    /// collapse an expanded row and throw away its loaded trace under the user's cursor.
    /// </summary>
    [Fact]
    public async Task RowsAreDiffedByRunId_SoAnExpandedRowSurvivesAProjection()
    {
        var childId = Guid.NewGuid();
        _runs.GetChildRunsAsync(_runId, Arg.Any<CancellationToken>()).Returns(new List<AgentRun>
        {
            Child(childId, AgentRunState.Running, "work"),
        });

        var vm = CreateVm();
        await vm.RefreshAsync();
        var row = Assert.Single(vm.Children);
        row.IsExpanded = true;
        await row.TimelineLoadTask!;

        await vm.RefreshAsync();

        Assert.Same(row, Assert.Single(vm.Children)); // the SAME instance, not a replacement
        Assert.True(row.IsExpanded);
    }

    /// <summary>
    /// T-CHILD-VM-5. The drill-down: expanding a row reads THAT run's trace, through the same store call and the
    /// same off-thread hop the parent's own expander uses — and the parent's <c>Timeline</c> is untouched, which
    /// is the observable form of "no merged ordering".
    /// </summary>
    [Fact]
    public async Task ExpandingAChildLoadsThatRunsOwnTrace_AndNotTheParents()
    {
        var childId = Guid.NewGuid();
        _runs.GetChildRunsAsync(_runId, Arg.Any<CancellationToken>()).Returns(new List<AgentRun>
        {
            Child(childId, AgentRunState.Completed, "work"),
        });
        _timeline.GetForRunAsync(childId, Arg.Any<CancellationToken>()).Returns(new List<AgentTimelineEvent>
        {
            Row(childId, 1),
            Row(childId, 2),
        });

        var vm = CreateVm();
        await vm.RefreshAsync();
        var row = Assert.Single(vm.Children);

        row.IsExpanded = true;
        await row.TimelineLoadTask!;

        await _timeline.Received(1).GetForRunAsync(childId, Arg.Any<CancellationToken>());
        Assert.Equal(2, row.Timeline.Count);
        Assert.False(row.HasNoTimeline);
        Assert.Empty(vm.Timeline); // the parent's own trace was never touched
    }

    /// <summary>
    /// T-CHILD-VM-6, <b>GUARD</b>. The child-id snapshot must be an IMMUTABLE set assigned as a whole.
    /// <c>RunChanged</c> fires OFF the UI thread — that is this VM's whole premise — so the filter reads this
    /// field from a pool thread while the projection writes it on the UI thread; a mutable <c>HashSet</c> here is
    /// the exact data race <c>ChatSessionManager</c> documents for its own <c>_ownRunIds</c>. Asserted by TYPE,
    /// because a race is not observable in a test that would still pass on the broken shape.
    /// </summary>
    [Fact]
    public void TheChildIdSnapshotIsImmutable()
    {
        var field = typeof(RunProgressViewModel)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(f => f.Name.Contains("childRunIds", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(typeof(ImmutableHashSet<Guid>), field.FieldType);
    }

    /// <summary>
    /// T-CHILD-VM-7, <b>REGRESSION</b>. A child-read fault leaves the rows exactly as they were and never breaks
    /// the panel — the parent's own projection still lands. Failure-isolated bookkeeping, the standing guardrail.
    /// </summary>
    [Fact]
    public async Task AFailedChildReadLeavesTheRowsAlone_AndStillProjectsTheRun()
    {
        var childId = Guid.NewGuid();
        _runs.GetChildRunsAsync(_runId, Arg.Any<CancellationToken>()).Returns(new List<AgentRun>
        {
            Child(childId, AgentRunState.Running, "work"),
        });

        var vm = CreateVm();
        await vm.RefreshAsync();
        Assert.Single(vm.Children);

        _runs.GetChildRunsAsync(_runId, Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("db gone"));
        _runs.GetAsync(_runId, Arg.Any<CancellationToken>()).Returns(new AgentRun { Id = _runId, State = AgentRunState.Completed });

        await vm.RefreshAsync();

        Assert.Single(vm.Children);                                  // untouched, not cleared
        Assert.Equal(RunProgressState.Completed, vm.State);           // and the run itself still projected
    }

    /// <summary>
    /// T-CHILD-VM-8, <b>REGRESSION</b>. A child trace that could not be READ says so; it never renders as the
    /// positive claim that the child recorded no decisions — the same standard the parent's trace is held to.
    /// </summary>
    [Fact]
    public async Task AFailedChildTraceReadSaysSo_AndIsNotAnEmptyTrace()
    {
        var childId = Guid.NewGuid();
        _runs.GetChildRunsAsync(_runId, Arg.Any<CancellationToken>()).Returns(new List<AgentRun>
        {
            Child(childId, AgentRunState.Completed, "work"),
        });
        _timeline.GetForRunAsync(childId, Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("gone"));

        var vm = CreateVm();
        await vm.RefreshAsync();
        var row = Assert.Single(vm.Children);

        row.IsExpanded = true;
        await row.TimelineLoadTask!;

        Assert.True(row.HasTimelineReadError);
        Assert.False(row.HasNoTimeline);
        Assert.Empty(row.Timeline);
    }

    /// <summary>
    /// T-CHILD-VM-5 (Batch 07 G8), <b>REGRESSION</b>. A parent parked at
    /// <see cref="AgentRunState.WaitingForChildren"/> projects its OWN state and offers no Continue.
    /// <para>
    /// Both halves matter. Without the explicit <c>MapState</c> arm the state falls through the default to
    /// <see cref="RunProgressState.Running"/>, hiding that the work moved to the children — and the header would
    /// read from the label converter's fall-through, i.e. "Completed", on a run that is still going. And
    /// <c>CanContinue</c> must stay false: this park is not a user affordance, and the resume CAS only ever
    /// claims <see cref="AgentRunState.WaitingForInput"/>, so a Continue here would silently no-op.
    /// </para>
    /// Neutralize: delete the <c>AgentRunState.WaitingForChildren</c> arm from <c>MapState</c> — the state and
    /// the activity line both red.
    /// </summary>
    [Fact]
    public async Task WaitingForChildren_ProjectsItsOwnStateAndDoesNotOfferContinue()
    {
        _runs.GetAsync(_runId, Arg.Any<CancellationToken>())
            .Returns(new AgentRun { Id = _runId, State = AgentRunState.WaitingForChildren });

        var vm = CreateVm();
        await vm.RefreshAsync();

        Assert.Equal(RunProgressState.WaitingForChildren, vm.State);
        Assert.False(vm.CanContinue);
        Assert.False(vm.IsTruncated);
        // The stubbed localization echoes the key, so this pins the key and not merely "some text".
        Assert.Equal("Run_Activity_WaitingForChildren", vm.CurrentActivity);
    }

    private RunProgressViewModel CreateVm()
    {
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());
        return new RunProgressViewModel(_runs, _runId, _loc, _resume, NullLogger.Instance, _timeline);
    }

    private static AgentRun Child(Guid id, AgentRunState state, string goal, long input = 0, long output = 0) => new()
    {
        Id = id,
        State = state,
        Goal = goal,
        LedgerJson = $"{{\"inputTokens\":{input},\"outputTokens\":{output},\"wallClockMs\":0,\"perStep\":[]}}",
    };

    private static AgentTimelineEvent Row(Guid runId, long seq) => new(
        Id: Guid.NewGuid(),
        RunId: runId,
        StepId: null,
        Seq: seq,
        Kind: AgentTimelineEventKind.ToolCall,
        Surface: ToolGateSurface.Unattended,
        Decision: ToolGateDecision.ApprovedOnce,
        Outcome: AgentTimelineOutcome.Ok,
        ToolName: "write_file",
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
