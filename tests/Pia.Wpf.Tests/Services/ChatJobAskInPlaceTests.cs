using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>A chat job's step asks on its block and carries on in the same round; anything else keeps today's gate.</summary>
public sealed class ChatJobAskInPlaceTests
{
    private readonly Guid _runId = Guid.NewGuid();
    private readonly Guid _chatId = Guid.NewGuid();
    private readonly Guid _pluginId = Guid.NewGuid();
    private readonly IPluginService _plugins = Substitute.For<IPluginService>();
    private readonly IToolPermissionService _permissions = Substitute.For<IToolPermissionService>();
    private readonly IAgentRunService _runService = Substitute.For<IAgentRunService>();
    private readonly RecordingTimelineService _timeline = new();
    private readonly FakeAttendance _attendance = new();
    private readonly List<bool> _clock = [];
    private int _executed;

    private sealed class FakeAttendance : IChatAttendance
    {
        public bool Attended { get; set; } = true;

        public bool IsAttended(Guid chatId) => Attended;
    }

    private RunAskCoordinator Broker(TimeSpan? timeout = null) =>
        new(_attendance, _runService, NullLogger<RunAskCoordinator>.Instance, timeout: timeout ?? TimeSpan.FromSeconds(30));

    private BackgroundAssistantTurnRunner Runner(bool mcp = false)
    {
        _plugins.IsMcpTool(Arg.Any<string>()).Returns(mcp);
        _plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((object? Result, PluginToolCall? PendingAction)?)(null, new PluginToolCall(
                ci.Arg<FunctionCallContent>().Name, _pluginId, mcp ? "some-mcp-server" : "files", "desc", null,
                () =>
                {
                    Interlocked.Increment(ref _executed);
                    return Task.FromResult<object?>("done");
                })));
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());
        return new BackgroundAssistantTurnRunner(
            Substitute.For<IAiClientService>(), _plugins, _permissions,
            Substitute.For<IAssistantPromptComposer>(), Substitute.For<IPersonaService>(),
            Substitute.For<IAssistantChatService>(), Substitute.For<IChatTitleService>(), settings,
            () => Substitute.For<ITokenMapService>(), _runService,
            new ExecutingRunStore(), NullLogger<BackgroundAssistantTurnRunner>.Instance, null);
    }

    private ToolApprovalStore Store(IRunAskBroker broker, bool chatJob = true, bool canPark = true,
        ISessionToolGrantStore? session = null) =>
        new(canPark, session, isTopLevelUserRun: true, isChatJob: chatJob,
            ask: new RunAskContext(broker, _runId, _chatId, Guid.NewGuid(), _clock.Add,
                TestContext.Current.CancellationToken));

    private static FunctionCallContent Call(string name = "write_file") =>
        new("call-1", name, new Dictionary<string, object?> { ["path"] = "a.txt" });

    private Task<object?> Dispatch(BackgroundAssistantTurnRunner runner, ToolApprovalStore store, string name = "write_file") =>
        runner.ReplayToolCallAsync(Call(name), [], round: 1, timeline: new AgentTimelineScope(_timeline, _runId, null),
            approvals: store);

    private static async Task<RunToolQuestion> PendingAsync(IRunAskBroker broker, Guid runId)
    {
        for (var i = 0; i < 500; i++)
        {
            if (broker.GetPending(runId) is { } q)
                return q;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("the step never asked");
    }

    [Theory]
    [InlineData(ToolDecision.AllowOnce, ToolGateDecision.ApprovedOnce)]
    [InlineData(ToolDecision.AllowForSession, ToolGateDecision.ApprovedForSession)]
    [InlineData(ToolDecision.AlwaysAllow, ToolGateDecision.ApprovedAlways)]
    public async Task AnAttendedChatJob_WaitsInPlace_AndRunsTheCallInTheSameRound(
        ToolDecision decision, ToolGateDecision audited)
    {
        var broker = Broker();
        var store = Store(broker);
        var call = Dispatch(Runner(), store);

        var question = await PendingAsync(broker, _runId);
        Assert.Equal("write_file", question.ToolName);
        Assert.Contains("a.txt", question.DisplayArgs);
        Assert.Equal(0, _executed);
        Assert.True(broker.Answer(_runId, question.RequestId, decision));

        Assert.Equal("done", await call);
        Assert.Equal(1, _executed);
        Assert.Null(store.PendingToolName);
        Assert.Null(broker.GetPending(_runId));
        Assert.Equal(audited, Assert.Single(_timeline.Rows).Decision);
        Assert.Equal([false, true], _clock);
        _runService.Received(1).SetWorkClockRunning(_runId, false);
        _runService.Received(1).SetWorkClockRunning(_runId, true);
    }

    [Fact]
    public async Task EachChoice_WritesExactlyTheCardsGrant()
    {
        foreach (var decision in new[] { ToolDecision.AllowOnce, ToolDecision.AllowForSession, ToolDecision.AlwaysAllow })
        {
            _permissions.ClearReceivedCalls();
            var broker = Broker();
            var call = Dispatch(Runner(), Store(broker));
            var question = await PendingAsync(broker, _runId);
            broker.Answer(_runId, question.RequestId, decision);
            await call;

            _permissions.Received(decision == ToolDecision.AllowForSession ? 1 : 0).GrantForSession(_pluginId, "write_file");
            await _permissions.Received(decision == ToolDecision.AlwaysAllow ? 1 : 0).GrantAsync(_pluginId, "write_file");
        }
    }

    [Fact]
    public async Task Deny_ReturnsTheCardsDecline_AndRunsNothing()
    {
        var broker = Broker();
        var store = Store(broker);
        var call = Dispatch(Runner(), store);
        var question = await PendingAsync(broker, _runId);
        broker.Answer(_runId, question.RequestId, ToolDecision.Decline);

        Assert.StartsWith("User declined", (string)(await call)!);
        Assert.Equal(0, _executed);
        Assert.Null(store.PendingToolName);
        Assert.Equal(ToolGateDecision.DeclinedByUser, Assert.Single(_timeline.Rows).Decision);
        _permissions.DidNotReceiveWithAnyArgs().GrantForSession(default, default!);
    }

    [Fact]
    public async Task NobodyAnswering_ParksTheRun_AndALateClickWritesNoGrant()
    {
        var broker = Broker(TimeSpan.FromMilliseconds(200));
        var store = Store(broker);
        var call = Dispatch(Runner(), store);
        var question = await PendingAsync(broker, _runId);

        var result = (string)(await call)!;

        Assert.StartsWith("Paused", result);
        Assert.Equal("write_file", store.PendingToolName);
        Assert.Equal(0, _executed);
        Assert.False(broker.Answer(_runId, question.RequestId, ToolDecision.AlwaysAllow));
        await _permissions.DidNotReceiveWithAnyArgs().GrantAsync(default, default!);
        Assert.Equal([false, true], _clock);
    }

    [Fact]
    public async Task AChatNotOnScreen_ParksAtOnce_WithoutAsking()
    {
        _attendance.Attended = false;
        var broker = Broker();
        var store = Store(broker);

        var result = (string)(await Dispatch(Runner(), store))!;

        Assert.StartsWith("Paused", result);
        Assert.Equal("write_file", store.PendingToolName);
        Assert.Empty(_clock);
        _runService.DidNotReceive().SetWorkClockRunning(Arg.Any<Guid>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task AnMcpCall_InAChatJob_IsAskedAndParked_InsteadOfRefused()
    {
        _attendance.Attended = false;
        var store = Store(Broker());

        var result = (string)(await Dispatch(Runner(mcp: true), store, "mcp_tool"))!;

        Assert.StartsWith("Paused", result);
        Assert.Equal("mcp_tool", store.PendingToolName);
    }

    [Fact]
    public async Task AnMcpCall_OutsideAChatJob_IsStillRefused()
    {
        var broker = Broker();
        var store = Store(broker, chatJob: false);

        var result = (string)(await Dispatch(Runner(mcp: true), store, "mcp_tool"))!;

        Assert.StartsWith("Denied", result);
        Assert.Null(store.PendingToolName);
        Assert.Null(broker.GetPending(_runId));
    }

    [Fact]
    public async Task AnotherRun_ParksAsToday_WithoutAskingOnTheBlock()
    {
        var broker = Broker();
        var store = Store(broker, chatJob: false);

        var result = (string)(await Dispatch(Runner(), store))!;

        Assert.StartsWith("Paused", result);
        Assert.Null(store.Ask);
        Assert.Empty(_clock);
    }

    [Fact]
    public async Task ARunThatCannotPark_IsNeverAChatJob_EvenWhenMarked()
    {
        var store = Store(Broker(), chatJob: true, canPark: false);

        var result = (string)(await Dispatch(Runner(mcp: true), store, "mcp_tool"))!;

        Assert.False(store.IsChatJob);
        Assert.Null(store.Ask);
        Assert.StartsWith("Denied", result);
    }

    [Fact]
    public async Task ASessionGrantForAnMcpTool_CountsInAChatJob_AndNowhereElse()
    {
        var session = new SessionToolGrantStore();
        session.Grant(_pluginId, "mcp_tool");

        var chatJob = Store(Broker(), session: session);
        Assert.Equal("done", await Dispatch(Runner(mcp: true), chatJob, "mcp_tool"));

        var background = Store(Broker(), chatJob: false, session: session);
        Assert.StartsWith("Denied", (string)(await Dispatch(Runner(mcp: true), background, "mcp_tool"))!);
        Assert.Equal(1, _executed);
    }

    [Fact]
    public async Task Stopping_DuringAWait_CancelsTheStep_AndClosesTheQuestion()
    {
        using var cts = new CancellationTokenSource();
        var broker = Broker();
        var store = new ToolApprovalStore(true, null, true, isChatJob: true,
            ask: new RunAskContext(broker, _runId, _chatId, null, _clock.Add, cts.Token));
        var call = Dispatch(Runner(), store);
        await PendingAsync(broker, _runId);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Null(broker.GetPending(_runId));
        Assert.Equal(0, _executed);
    }
}
