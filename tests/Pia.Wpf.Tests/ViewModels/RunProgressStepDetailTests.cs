using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Tests.TestInfrastructure;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

public sealed class RunProgressStepDetailTests
{
    private readonly Guid _runId = Guid.NewGuid();
    private readonly Guid _stepId = Guid.NewGuid();
    private readonly IAgentRunService _runs = Substitute.For<IAgentRunService>();
    private readonly IAgentTimelineService _timeline = Substitute.For<IAgentTimelineService>();
    private readonly IAgentToolExchangeStore _store = Substitute.For<IAgentToolExchangeStore>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly IAgentRunResumeService _resume = Substitute.For<IAgentRunResumeService>();
    private readonly RunLiveTextWatcher _carrier = new(NullLogger<RunLiveTextWatcher>.Instance);

    public RunProgressStepDetailTests()
    {
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        _loc.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => (string)ci[0]);
        _timeline.GetForRunAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentTimelineEvent>());
        Run(AgentStepStatus.Running);
    }

    [Fact]
    public void Carrier_RelaysToEverySubscriber_EvenWhenOneThrows()
    {
        var seen = 0;
        _carrier.TextAppended += _ => throw new InvalidOperationException();
        _carrier.TextAppended += _ => seen++;

        _carrier.Publish(new RunLiveTextChunk(_runId, _stepId, RunLiveTextKind.Text, "x"));

        Assert.Equal(1, seen);
    }

    [Fact]
    public async Task LiveText_ShowsOnTheRunningStep_SplitsThinkTags_AndIgnoresOtherRuns()
    {
        var vm = await LoadedVm();

        Publish(RunLiveTextKind.Text, "<think>pondering</think>Reading the ");
        Publish(RunLiveTextKind.Text, "tariff page");
        Publish(RunLiveTextKind.Reasoning, " more");
        _carrier.Publish(new RunLiveTextChunk(Guid.NewGuid(), _stepId, RunLiveTextKind.Text, "other run"));

        var row = vm.Steps[0];
        Assert.Equal("Reading the tariff page", row.LiveText);
        Assert.Contains("more", row.LiveReasoning);
        Assert.Contains("pondering", row.LiveReasoning);
        Assert.DoesNotContain("other run", row.LiveText);
    }

    [Fact]
    public async Task LiveText_ClearsWhenTheExchangeEnds_AndOnARoundBreak()
    {
        var vm = await LoadedVm();
        Publish(RunLiveTextKind.Text, "first turn");
        Publish(RunLiveTextKind.RoundBreak, string.Empty);
        Assert.Null(vm.Steps[0].LiveText);

        Publish(RunLiveTextKind.Text, "second");
        Assert.Equal("second", vm.Steps[0].LiveText);

        Publish(RunLiveTextKind.Ended, string.Empty);
        Assert.Null(vm.Steps[0].LiveText);
    }

    [Fact]
    public async Task LiveText_ClearsWhenTheStepSettles()
    {
        var vm = await LoadedVm();
        Publish(RunLiveTextKind.Text, "working");

        Run(AgentStepStatus.Done);
        await vm.RefreshAsync();

        Assert.Null(vm.Steps[0].LiveText);
    }

    [Fact]
    public async Task LiveText_KeepsOnlyTheTail()
    {
        var vm = await LoadedVm();
        Publish(RunLiveTextKind.Text, new string('a', 5000) + "END");

        var text = vm.Steps[0].LiveText!;
        Assert.True(text.Length <= RunProgressViewModel.LiveTailChars + 1);
        Assert.EndsWith("END", text);
    }

    [Fact]
    public async Task LiveText_CoalescesABurstIntoOnePost()
    {
        var queue = new QueueSyncContext();
        SynchronizationContext.SetSynchronizationContext(queue);
        var deferred = new RunProgressViewModel(_runs, _runId, _loc, _resume, NullLogger.Instance);
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());
        var refresh = deferred.RefreshAsync();
        while (!refresh.IsCompleted || queue.Count > 0)
        {
            queue.Drain();
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
        deferred.AttachLiveText(_carrier);

        for (var i = 0; i < 50; i++) Publish(RunLiveTextKind.Text, "x");

        Assert.Equal(1, queue.Count);
        queue.Drain();
        Assert.Equal(new string('x', 50), deferred.Steps[0].LiveText);
    }

    [Fact]
    public async Task Disposing_StopsListening()
    {
        var vm = await LoadedVm();
        vm.Dispose();

        Publish(RunLiveTextKind.Text, "late");

        Assert.Null(vm.Steps[0].LiveText);
    }

    [Fact]
    public async Task ToolRows_AreBuiltPerStep_InCallOrder_AndSurviveAReload()
    {
        var other = Guid.NewGuid();
        var rows = new List<AgentTimelineEvent>
        {
            Row(1, "web_search", durationMs: 2100),
            Row(2, "web_fetch", outcome: AgentTimelineOutcome.Error),
            Row(3, "read_file", stepId: other),
        };
        _timeline.GetForRunAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(rows);

        var vm = await LoadedVm();
        var tools = vm.Steps[0].ToolRows;

        Assert.Equal(["web_search", "web_fetch"], tools.Select(t => t.ToolName));
        Assert.Equal(RunStepToolRowViewModel.FormatDuration(2100), tools[0].DurationLabel);
        Assert.Equal("RunStepDetail_Outcome_Ok", tools[0].OutcomeLabel);
        Assert.True(tools[1].IsFailed);

        var first = tools[0];
        first.IsExpanded = true;
        rows.Add(Row(4, "web_search"));
        await vm.LoadTimelineAsync();

        Assert.Equal(3, tools.Count);
        Assert.Same(first, tools[0]);
        Assert.True(first.IsExpanded);
        Assert.Equal(1, tools[2].Occurrence);
    }

    [Fact]
    public async Task OpeningARow_LoadsInputAndResult_ThroughTheStepRead()
    {
        _timeline.GetForRunAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentTimelineEvent> { Row(1, "web_fetch", callId: "c1") });
        _store.GetStepCallsAsync(_runId, _stepId, Arg.Any<CancellationToken>()).Returns(new AgentStepCallDetails(
            [Call("c1", "web_fetch", "{\"url\":\"https://example.org/a\"}", AgentToolDetailState.Kept, "body",
                AgentToolDetailState.Truncated)], []));

        var vm = await LoadedVm();
        var row = vm.Steps[0].ToolRows[0];
        await row.ToggleCommand.ExecuteAsync(null);

        Assert.True(row.IsExpanded);
        Assert.Equal("{\"url\":\"https://example.org/a\"}", row.InputText);
        Assert.Equal("\"https://example.org/a\"", row.ArgumentLine);
        Assert.Equal("body", row.ResultText);
        Assert.Equal("RunStepDetail_State_Truncated", row.ResultNote);
        Assert.False(row.HasDetailNote);
    }

    [Fact]
    public async Task AReadFailure_SaysSo()
    {
        _timeline.GetForRunAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentTimelineEvent> { Row(1, "web_fetch") });
        _store.GetStepCallsAsync(_runId, _stepId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException());

        var vm = await LoadedVm();
        var row = vm.Steps[0].ToolRows[0];
        await row.LoadDetailAsync(CancellationToken.None);

        Assert.Equal("RunStepDetail_ReadFailed", row.DetailNote);
    }

    [Theory]
    [InlineData(AgentToolDetailState.Omitted, AgentToolDetailState.Missing,
        "RunStepDetail_State_Omitted", "RunStepDetail_State_Missing")]
    [InlineData(AgentToolDetailState.Kept, AgentToolDetailState.Truncated, null, "RunStepDetail_State_Truncated")]
    public void DetailStates_AreNamed(AgentToolDetailState args, AgentToolDetailState result, string? inputNote,
        string? resultNote)
    {
        var row = new RunStepToolRowViewModel(Guid.NewGuid(), _loc, null);
        var call = Call("c", "t", args == AgentToolDetailState.Kept ? "{}" : null, args, null, result);

        row.ApplyDetail(call, AgentStepCallDetails.Empty, readFailed: false);

        Assert.Equal(inputNote, row.InputNote);
        Assert.Equal(resultNote, row.ResultNote);
    }

    [Fact]
    public void AnUnmatchedCall_InANotRecordedRound_SaysTheRoundWasNotKept()
    {
        var row = new RunStepToolRowViewModel(Guid.NewGuid(), _loc, null) { Round = 3 };

        row.ApplyDetail(null, new AgentStepCallDetails([], [3]), readFailed: false);
        Assert.Equal("RunStepDetail_NotRecordedRound", row.DetailNote);

        row.ApplyDetail(null, new AgentStepCallDetails([], [1]), readFailed: false);
        Assert.Equal("RunStepDetail_NoRecord", row.DetailNote);
    }

    [Fact]
    public void Match_PrefersAUniqueId_ThenFallsBackToNameRoundAndOrder()
    {
        var details = new AgentStepCallDetails(
        [
            Call("", "web_search", "{\"q\":1}", round: 1),
            Call("", "web_search", "{\"q\":2}", round: 1),
            Call("dup", "read_file", "{\"a\":1}", round: 2),
            Call("dup", "read_file", "{\"a\":2}", round: 2),
            Call("u", "web_fetch", "{}", round: 2),
        ], []);

        Assert.Equal("{}", RunStepToolRowViewModel.Match(details, "u", "web_fetch", 2, 0)!.Arguments);
        Assert.Equal("{\"q\":2}", RunStepToolRowViewModel.Match(details, null, "web_search", 1, 1)!.Arguments);
        Assert.Equal("{\"a\":2}", RunStepToolRowViewModel.Match(details, "dup", "read_file", 2, 1)!.Arguments);
        Assert.Null(RunStepToolRowViewModel.Match(details, null, "web_search", 1, 5));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("{\"count\":3,\"query\":\"tarif 2026\"}", "\"tarif 2026\"")]
    [InlineData("{\"x\":5}", "{\"x\":5}")]
    [InlineData("not json", "not json")]
    public void ShortArgumentLine_PicksTheTellingValue(string? json, string? expected) =>
        Assert.Equal(expected, RunStepToolRowViewModel.ShortArgumentLine(json));

    [Fact]
    public void ShortArgumentLine_IsCapped()
    {
        var line = RunStepToolRowViewModel.ShortArgumentLine($"{{\"path\":\"{new string('p', 200)}\"}}")!;
        Assert.Equal(RunStepToolRowViewModel.ArgumentLineMax, line.Length);
    }

    private void Publish(RunLiveTextKind kind, string text) =>
        _carrier.Publish(new RunLiveTextChunk(_runId, _stepId, kind, text));

    private void Run(AgentStepStatus stepStatus) =>
        _runs.GetAsync(_runId, Arg.Any<CancellationToken>()).Returns(new AgentRun
        {
            Id = _runId,
            State = AgentRunState.Running,
            Plan = [new AgentStep { Id = _stepId, Ordinal = 1, Title = "Research", Status = stepStatus }],
        });

    private async Task<RunProgressViewModel> LoadedVm()
    {
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());
        var vm = new RunProgressViewModel(_runs, _runId, _loc, _resume, NullLogger.Instance, _timeline,
            toolCalls: _store);
        vm.AttachLiveText(_carrier);
        await vm.RefreshAsync();
        if (vm.TimelineLoadTask is { } load) await load;
        return vm;
    }

    private static AgentToolCallDetail Call(string id, string tool, string? args,
        AgentToolDetailState argsState = AgentToolDetailState.Kept, string? result = "r",
        AgentToolDetailState resultState = AgentToolDetailState.Kept, int? round = 1) =>
        new(id, tool, round, DateTime.UtcNow, args, argsState, result, resultState);

    private AgentTimelineEvent Row(long seq, string toolName, Guid? stepId = null, long? durationMs = 5,
        AgentTimelineOutcome outcome = AgentTimelineOutcome.Ok, string? callId = null) => new(
        Id: Guid.NewGuid(),
        RunId: _runId,
        StepId: stepId ?? _stepId,
        Seq: seq,
        Kind: AgentTimelineEventKind.ToolCall,
        Surface: ToolGateSurface.Unattended,
        Decision: ToolGateDecision.AutoApprovedPolicy,
        Outcome: outcome,
        ToolName: toolName,
        ToolClass: ToolClass.Files,
        PluginId: null,
        ArgsChars: 12,
        ResultChars: 20,
        DurationMs: durationMs,
        CreatedAt: DateTime.UtcNow,
        ToolCallId: callId, Round: 1, StepOrdinal: null, RequestedAt: null, DecidedAt: null);

    private sealed class QueueSyncContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback, object?)> _posts = new();

        public int Count => _posts.Count;

        public override void Post(SendOrPostCallback d, object? state) => _posts.Enqueue((d, state));

        public override void Send(SendOrPostCallback d, object? state) => d(state);

        public void Drain()
        {
            while (_posts.Count > 0)
            {
                var (d, state) = _posts.Dequeue();
                d(state);
            }
        }
    }
}
