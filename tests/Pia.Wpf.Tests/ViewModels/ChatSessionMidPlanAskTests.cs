using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// <b>Batch 18 G5 on the LIVE path (18 D7, owner Q5).</b> <c>LiveTurnExecutor</c> makes no step decision of its
/// own — it delegates to <c>ChatSession.RunStepTurnAsync</c> — so the interactive half of the mid-plan ask has to
/// be driven here, exactly as hermes #9's was in <c>ChatSessionStepOutcomeSignalTests</c> beside this file.
/// <para>
/// <b>Why this file exists at all.</b> There are TWO executors, and a tool wired into only one of them leaves the
/// interactive path silently without it. 18 D7 records the interactive symptom as INFERRED from shared code
/// rather than observed, which makes "interactive works the same way" a claim that has to be measured rather than
/// assumed. The scoping half (which turn shapes are OFFERED the tool) is
/// <c>LiveTurnExecutorStepToolScopeTests</c>; this file is the interception and the result.
/// </para>
/// <para>
/// <b>Neutralize</b> the headline facts by deleting the <c>_userInputRequest</c> pre-route arm in
/// <c>ChatSession.HandleToolCall</c> — the call then routes, dead-ends at "Unknown tool.", and
/// <c>UserInputQuestion</c> comes back null, so the run would never park. Deleting the tool instead reds
/// everything and proves nothing.
/// </para>
/// <para>
/// net10.0-windows cannot execute on macOS — these tests are written, not run; execution is deferred to
/// Windows/CI.
/// </para>
/// </summary>
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

    /// <summary>
    /// A step spec shaped as <c>LiveTurnExecutor.BuildSpec</c> would have produced it. <paramref name="canAsk"/>
    /// is the OWNER Q1 axis: the ask tool is appended for a root run and withheld for a delegated one — and
    /// <c>emit_step_result</c> is present either way, because the store that intercepts the ask is armed from
    /// THAT tool's presence ("this is a real step turn") while its <c>CanAsk</c> comes from the ask tool's.
    /// </summary>
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

    /// <summary>
    /// An interactive step that calls <c>request_user_input</c> carries the question out on
    /// <c>StepTurnResult.UserInputQuestion</c>, which is what the orchestrator parks on. The model is told the
    /// run is stopping, and — 18 D6 — the OUTCOME members are untouched by the ask.
    /// </summary>
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
        // 18 D6: no third outcome. The step declared nothing, so Outcome stays null and the run's Done/Failed
        // mapping is exactly what it was — the ask rides its own member.
        Assert.Null(result.Outcome);
    }

    /// <summary>
    /// <b>GUARD, and the reason the sink exists at all.</b> A turn that was never offered the tool — an ordinary
    /// chat turn, or any non-step turn — routes a hallucinated <c>request_user_input</c> the ordinary way and
    /// gets the honest "Unknown tool." answer, with no question captured. This is the fact that makes the choke
    /// point per-executor instead of in <c>AssistantPromptComposer.PrepareTurn</c> (owner Q5): a composer-level
    /// tool would leak the ask into chat, voice, MCP and @-command turns, none of which has a run to park.
    /// </summary>
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

    /// <summary>
    /// <b>OWNER Q1 on the live path, at the sink.</b> A DELEGATED step's call is still INTERCEPTED — no routing,
    /// no "Unknown tool.", no <c>UnknownTool</c> audit row — and answered with the redirect to
    /// <c>emit_step_result</c>. No question comes out, so nothing parks.
    /// <para>
    /// This is where this store deliberately diverges from <c>StepOutcomeStore</c>'s armed-iff-offered rule. That
    /// rule protects NON-step turns (the fact above); on a step turn the tool is part of the vocabulary and is
    /// merely refused, and a refusal that names the working channel is strictly better than a dead end.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// <b>CONTAINMENT, and it bites harder here than on the unattended surface.</b> A write the model requests
    /// AFTER the ask does not reach the gate at all — so no ACTION CARD is raised. Without the guard a human
    /// would be asked to approve a write belonging to a step that is already being thrown away, and approving it
    /// would execute a side effect the resumed step then performs a SECOND time. At-most-once, not tidiness.
    /// </summary>
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
        // ALL FOUR parameters matched, including the two optional ones: the production call site passes
        // `toolClass:`, so a DidNotReceive written against the two-argument shape would match nothing and pass
        // whether or not a card was ever built.
        _cards.DidNotReceive().Build(
            Arg.Any<PluginToolCall>(), Arg.Any<bool>(), Arg.Any<ToolGateDecision?>(), Arg.Any<ToolClass?>());
    }

    /// <summary>
    /// <b>GUARD</b>. The sink is DISARMED when the step turn ends, proven with an ORDINARY chat turn — the only
    /// caller that can observe a leak, since a second step turn re-assigns the field on entry and would pass
    /// either way. A session outlives the run that borrowed it, so a leaked sink would let a later chat turn
    /// swallow a hallucinated <c>request_user_input</c> instead of answering "Unknown tool.".
    /// </summary>
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
