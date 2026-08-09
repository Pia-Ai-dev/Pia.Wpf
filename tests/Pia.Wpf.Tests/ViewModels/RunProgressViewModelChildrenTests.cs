using System.Collections.Immutable;
using System.Reflection;
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
/// Deliberately not a merged timeline: <c>Seq</c> is monotonic only within a run id and <c>CreatedAt</c> is
/// rejected as an ordering source, so the rows are two per-run views side by side.
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

    [Fact]
    public async Task AChildlessRunShowsNothing()
    {
        var vm = CreateVm();
        await vm.RefreshAsync();

        Assert.False(vm.HasChildren);
        Assert.Empty(vm.Children);
        Assert.Null(vm.ChildrenNote);
    }

    /// <summary>Without the widened filter every child event is dropped and a fan-out renders as permanently unfinished.</summary>
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

        // The event carries the CHILD's id — the case the narrow filter dropped.
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

    /// <summary>A rebuild on every <c>RunChanged</c> would collapse an expanded row and discard its loaded trace.</summary>
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

    /// <summary>The parent's <c>Timeline</c> is untouched, which is the observable form of "no merged ordering".</summary>
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
    /// <c>RunChanged</c> fires off the UI thread, so a mutable set here would be a data race; asserted by TYPE
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
    /// An EXACT member list, not a name blacklist, so a later convenience member sourced from the child chat
    /// fails here rather than quietly becoming a route for tool-result text into a VM-state dump.
    /// </summary>
    [Fact]
    public void TheChildRowCarriesNoPayload()
    {
        var actual = typeof(ChildRunRowViewModel)
            .GetProperties()
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        // HasTokens and TokensLabel are a render of InputTokens + OutputTokens and say nothing new about the
        // child run — in particular nothing about what it touched.
        Assert.Equal(
            new[]
            {
                "HasNoTimeline", "HasTimelineReadError", "HasTokens", "InputTokens", "IsExpanded", "IsFinished",
                "OutputTokens", "RunId", "State", "Timeline", "Title", "TokensLabel",
            },
            actual);
    }

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

    /// <summary>A trace that could not be read must never render as the positive claim that the child recorded no decisions.</summary>
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
    /// Without an explicit <c>MapState</c> arm the state falls through to Running, and <c>CanContinue</c> must
    /// stay false because the resume CAS only ever claims <see cref="AgentRunState.WaitingForInput"/>.
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
        CreatedAt: DateTime.UtcNow,
        ToolCallId: null, Round: null, StepOrdinal: null, RequestedAt: null, DecidedAt: null);
}
