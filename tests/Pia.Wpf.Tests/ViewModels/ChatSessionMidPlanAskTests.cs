using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary><c>LiveTurnExecutor</c> delegates step decisions to <c>ChatSession.RunStepTurnAsync</c>, so the interactive half of the mid-plan ask is driven here; <c>LiveTurnExecutorStepToolScopeTests</c> covers which turn shapes are offered the tool.</summary>
public sealed class ChatSessionMidPlanAskTests
{
    private const string TheQuestion = "Which cluster should I deploy to?";

    private readonly IAiClientService _ai = Substitute.For<IAiClientService>();
    private readonly IPluginService _plugins = Substitute.For<IPluginService>();
    private readonly IActionCardBuilder _cards = Substitute.For<IActionCardBuilder>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly ITokenMapService _tokenMap = Substitute.For<ITokenMapService>();
    private readonly IToolPermissionService _permissions = Substitute.For<IToolPermissionService>();

    /// <summary>What the session handed back to the model, per tool call.</summary>
    private readonly List<object?> _toolReplies = [];

    /// <summary>Names that actually reached a routed tool's <c>Execute()</c>.</summary>
    private readonly List<string> _executed = [];

    public ChatSessionMidPlanAskTests()
    {
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        _loc.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => (string)ci[0]);
        _cards.ResolveStatusText(Arg.Any<string>()).Returns("running");
    }

    private ChatSession CreateSession() => new(
        _tokenMap, _ai, _plugins, _cards, _permissions, _loc, NullLogger.Instance, _ => true);

    /// <summary>A step spec shaped as <c>LiveTurnExecutor.BuildSpec</c> would produce it; <paramref name="canAsk"/> controls whether the ask tool is appended (root run) or withheld (delegated step).</summary>
    private static StepTurnSpec Spec(bool isStepTurn = true, bool canAsk = true)
    {
        var setup = new AssistantTurnSetup(
            "system",
            [AIFunctionFactory.Create(() => "ok", "unrelated_tool", "not a step tool")],
            SupportsTools: true,
            WebSearchActive: false);
        if (isStepTurn)
            setup = AgentStepTools.WithStepResultTool(setup);
        if (isStepTurn && canAsk)
            setup = AgentStepTools.WithRequestUserInputTool(setup);

        return new StepTurnSpec(
            RunId: Guid.NewGuid(),
            Ordinal: 0,
            Intent: "do the thing",
            ExpectedArtifact: null,
            SystemPrompt: setup.SystemPrompt,
            Persona: new PersonaAttribution(Guid.NewGuid(), "Pia", "🤖"),
            Provider: new AiProvider { Name = "Test", Endpoint = "http://localhost", ProviderType = AiProviderType.OpenAI },
            Tools: setup.Tools,
            SupportsTools: setup.SupportsTools,
            WebSearchActive: false,
            TokenizationEnabled: false);
    }

    private static FunctionCallContent Ask(string question, string callId = "call-ask") =>
        new(callId, AgentStepTools.RequestUserInputToolName,
            new Dictionary<string, object?> { ["question"] = question });

    private void ArrangeExchange(Func<ToolCallHandler?, Task<string>> drive)
    {
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(),
                cancellationToken: Arg.Any<CancellationToken>(), contextBudget: Arg.Any<AgentContextBudget?>())
            .Returns(ci => Stream(ci.ArgAt<ToolCallHandler?>(3), drive));
    }

    private async IAsyncEnumerable<ChatStreamItem> Stream(
        ToolCallHandler? handler, Func<ToolCallHandler?, Task<string>> drive)
    {
        ToolCallHandler? recording = handler is null
            ? null
            : async (call, ctx) =>
            {
                var reply = await handler(call, ctx);
                _toolReplies.Add(reply);
                return reply;
            };

        var text = await drive(recording);
        if (!string.IsNullOrEmpty(text))
            yield return new TextDelta(text);
        await Task.Yield();
    }

    // ---- the headline ----

    /// <summary>An interactive step that calls <c>request_user_input</c> carries the question out on <c>StepTurnResult.UserInputQuestion</c>, which is what the orchestrator parks on.</summary>
    [Fact]
    public async Task AStepThatAsks_CarriesTheQuestionOutOnTheResult()
    {
        ArrangeExchange(async handler =>
        {
            await handler!(Ask(TheQuestion), new ToolDispatchContext(1));
            return "let me check with you first";
        });

        var result = await CreateSession().RunStepTurnAsync(
            Spec(), new RunContext("goal", RunProfile.Interactive), TestContext.Current.CancellationToken);

        Assert.Equal(TheQuestion, result.UserInputQuestion);
        Assert.Contains(UserInputRequestStore.Accepted, _toolReplies.Select(r => r as string));
        // The step declared nothing, so Outcome stays null — the ask rides its own member.
        Assert.Null(result.Outcome);
    }

    /// <summary>The ask tool is scoped per-executor, not composer-wide, so a turn never offered it just routes a hallucinated <c>request_user_input</c> as an ordinary unknown tool call.</summary>
    [Fact]
    public async Task ATurnThatWasNotOfferedTheTool_StillRoutesTheNameAndFindsNothing()
    {
        _plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?, PluginToolCall?)?)null);
        ArrangeExchange(async handler =>
        {
            await handler!(Ask(TheQuestion), new ToolDispatchContext(1));
            return "some text";
        });

        var result = await CreateSession().RunStepTurnAsync(
            Spec(isStepTurn: false), new RunContext("goal", RunProfile.Interactive),
            TestContext.Current.CancellationToken);

        Assert.Null(result.UserInputQuestion);
        Assert.Contains("Unknown tool.", _toolReplies.Select(r => r as string));
        await _plugins.Received().RouteToolCallAsync(
            Arg.Is<FunctionCallContent>(c => c.Name == AgentStepTools.RequestUserInputToolName),
            Arg.Any<CancellationToken>());
    }

    /// <summary>A delegated step's ask is still intercepted and redirected to <c>emit_step_result</c>, not routed as unknown and not parked.</summary>
    [Fact]
    public async Task ADelegatedStepsAsk_IsInterceptedAndRedirected_NotRoutedAndNotParked()
    {
        _plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?, PluginToolCall?)?)null);
        ArrangeExchange(async handler =>
        {
            await handler!(Ask(TheQuestion), new ToolDispatchContext(1));
            return "some text";
        });

        var result = await CreateSession().RunStepTurnAsync(
            Spec(canAsk: false), new RunContext("goal", RunProfile.Interactive),
            TestContext.Current.CancellationToken);

        Assert.Null(result.UserInputQuestion);
        Assert.Contains(UserInputRequestStore.RefusedForDelegatedStep, _toolReplies.Select(r => r as string));
        Assert.DoesNotContain("Unknown tool.", _toolReplies.Select(r => r as string));
        await _plugins.DidNotReceive().RouteToolCallAsync(
            Arg.Is<FunctionCallContent>(c => c.Name == AgentStepTools.RequestUserInputToolName),
            Arg.Any<CancellationToken>());
    }

    /// <summary>A write requested after the ask must not reach the approval gate — approving it would let the resumed step execute the side effect a second time.</summary>
    [Fact]
    public async Task AWriteRequestedAfterTheAsk_NeverReachesTheGate()
    {
        _plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var name = ci.Arg<FunctionCallContent>().Name;
                return ((object? Result, PluginToolCall? PendingAction)?)(null, new PluginToolCall(
                    name, Guid.NewGuid(), "files", "desc", null,
                    () => { _executed.Add(name); return Task.FromResult<object?>("did it"); }));
            });

        ArrangeExchange(async handler =>
        {
            await handler!(Ask(TheQuestion), new ToolDispatchContext(1));
            await handler(
                new FunctionCallContent("call-write", "write_file", new Dictionary<string, object?>()),
                new ToolDispatchContext(2));
            return "text";
        });

        var result = await CreateSession().RunStepTurnAsync(
            Spec(), new RunContext("goal", RunProfile.Interactive), TestContext.Current.CancellationToken);

        Assert.Equal(TheQuestion, result.UserInputQuestion);
        Assert.Empty(_executed);
        // Matches all four parameters (incl. the two optional ones) — the production call passes toolClass,
        // so a two-argument match would pass vacuously.
        _cards.DidNotReceive().Build(
            Arg.Any<PluginToolCall>(), Arg.Any<bool>(), Arg.Any<ToolGateDecision?>(), Arg.Any<ToolClass?>());
    }

    /// <summary>The sink is disarmed when the step turn ends, so a later ordinary chat turn still routes a hallucinated <c>request_user_input</c> instead of swallowing it.</summary>
    [Fact]
    public async Task TheSinkIsDisarmedAfterTheStep_SoALaterChatTurnStillRoutesTheName()
    {
        _plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?, PluginToolCall?)?)null);
        var session = CreateSession();
        var ct = TestContext.Current.CancellationToken;

        ArrangeExchange(async handler =>
        {
            await handler!(Ask(TheQuestion), new ToolDispatchContext(1));
            return "first";
        });
        var step = await session.RunStepTurnAsync(Spec(), new RunContext("goal", RunProfile.Interactive), ct);
        Assert.Equal(TheQuestion, step.UserInputQuestion);

        _toolReplies.Clear();
        ArrangeExchange(async handler =>
        {
            await handler!(Ask("leaked into the next turn", "call-leak"), new ToolDispatchContext(1));
            return "second";
        });
        var user = new AssistantMessage(ChatRole.User, "hi");
        var assistant = new AssistantMessage(ChatRole.Assistant) { IsStreaming = true };
        session.Messages.Add(user);
        session.Messages.Add(assistant);
        await session.RunTurnAsync(new ChatTurnRequest
        {
            UserMessage = user,
            AssistantMessage = assistant,
            Provider = new AiProvider { Name = "Test", Endpoint = "http://localhost", ProviderType = AiProviderType.OpenAI },
            TurnSetup = new AssistantTurnSetup("system", [], SupportsTools: true, WebSearchActive: false),
            AtCommands = [],
            TokenizationEnabled = false,
        }, ct);

        Assert.Contains("Unknown tool.", _toolReplies.Select(r => r as string));
    }
}
