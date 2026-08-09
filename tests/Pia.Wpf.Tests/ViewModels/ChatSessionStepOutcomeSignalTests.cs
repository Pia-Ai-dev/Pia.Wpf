using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary><c>LiveTurnExecutor</c> makes no step-success decision of its own, so the facts have to be driven
/// through <c>ChatSession.RunStepTurnAsync</c>.</summary>
public sealed class ChatSessionStepOutcomeSignalTests
{
    private readonly IAiClientService _ai = Substitute.For<IAiClientService>();
    private readonly IPluginService _plugins = Substitute.For<IPluginService>();
    private readonly IActionCardBuilder _cards = Substitute.For<IActionCardBuilder>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly ITokenMapService _tokenMap = Substitute.For<ITokenMapService>();
    private readonly IToolPermissionService _permissions = Substitute.For<IToolPermissionService>();

    /// <summary>What the session handed back to the model, per tool call.</summary>
    private readonly List<object?> _toolReplies = [];

    public ChatSessionStepOutcomeSignalTests()
    {
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]); // echo the key so a string is assertable
        _loc.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => (string)ci[0]);
        _cards.ResolveStatusText(Arg.Any<string>()).Returns("running");
    }

    private ChatSession CreateSession() => new(
        _tokenMap, _ai, _plugins, _cards, _permissions, _loc, NullLogger.Instance, _ => true);

    /// <summary>The tool list <c>LiveTurnExecutor.BuildSpec</c> would have produced for such a step.</summary>
    private static StepTurnSpec Spec(bool offerStepResultTool)
    {
        var setup = new AssistantTurnSetup(
            "system",
            [AIFunctionFactory.Create(() => "ok", "unrelated_tool", "not the step-result tool")],
            SupportsTools: true,
            WebSearchActive: false);
        if (offerStepResultTool)
            setup = AgentStepTools.WithStepResultTool(setup);

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

    private static FunctionCallContent Emit(bool succeeded, string summary, string? artifact = null)
    {
        var args = new Dictionary<string, object?> { ["succeeded"] = succeeded, ["summary"] = summary };
        if (artifact is not null) args["artifact_ref"] = artifact;
        return new FunctionCallContent("call-emit", AgentStepTools.EmitStepResultToolName, args);
    }

    /// <summary>Arranges the AI client to run <paramref name="drive"/> against the session's tool handler and
    /// then stream whatever text it returns (empty string = no TextDelta at all).</summary>
    private void ArrangeExchange(Func<ToolCallHandler?, Task<string>> drive)
    {
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(),
                cancellationToken: Arg.Any<CancellationToken>(), contextBudget: Arg.Any<AgentContextBudget?>())
            .Returns(ci => Stream(ci.ArgAt<ToolCallHandler?>(3), drive));
    }

    private async IAsyncEnumerable<ChatStreamItem> Stream(
        ToolCallHandler? handler,
        Func<ToolCallHandler?, Task<string>> drive)
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

    // ---- the discriminating pair ----

    [Fact]
    public async Task DeclaredFailure_WithPlentyOfText_DoesNotSucceed()
    {
        const string eloquent =
            "I tried to publish the release notes, but the target folder is read-only, so nothing was "
            + "written. Below is the text I would have published.";
        ArrangeExchange(async handler =>
        {
            await handler!(Emit(succeeded: false, summary: "the target folder is read-only"), new ToolDispatchContext(1));
            return eloquent;
        });

        var session = CreateSession();
        var result = await session.RunStepTurnAsync(
            Spec(offerStepResultTool: true), new RunContext("goal", RunProfile.Interactive),
            TestContext.Current.CancellationToken);

        // Text was there and nothing threw, which a text-and-exception predicate reads as success.
        Assert.Equal(eloquent, result.VisibleText);
        Assert.False(result.Succeeded);
        Assert.False(result.Cancelled);
        // The model's own reason becomes the error the orchestrator hands to ReplanAsync.
        Assert.Equal("the target folder is read-only", result.Error);
        Assert.NotNull(result.Outcome);
        Assert.False(result.Outcome!.Succeeded);
    }

    /// <summary>The empty-response placeholder is UI text: carried on as <c>VisibleText</c> it would reach the
    /// critic prompt as a result line contradicting the declared success right above it.</summary>
    [Fact]
    public async Task DeclaredSuccess_WithNoTextAtAll_Succeeds()
    {
        ArrangeExchange(async handler =>
        {
            await handler!(Emit(succeeded: true, summary: "renamed the columns", artifact: "data/clean.csv"), new ToolDispatchContext(1));
            return string.Empty; // no TextDelta whatsoever
        });

        var session = CreateSession();
        var result = await session.RunStepTurnAsync(
            Spec(offerStepResultTool: true), new RunContext("goal", RunProfile.Interactive),
            TestContext.Current.CancellationToken);

        // Synthesized for the UI, so the chat bubble is not blank…
        Assert.Equal("Msg_Assistant_EmptyResponse", session.Messages[^1].Content);
        // …but not what the step reports having produced.
        Assert.Equal(string.Empty, result.VisibleText);
        Assert.True(result.Succeeded, result.Error);
        Assert.Null(result.Error);
        Assert.Equal("data/clean.csv", result.Outcome!.ArtifactRef);
    }

    // ---- the fallback ----

    /// <summary>With no declaration the text-and-exception predicate still decides, and the step is unconfirmed.</summary>
    [Fact]
    public async Task NoDeclaration_FallsBackToTheOldPredicate_AndIsUnconfirmed()
    {
        ArrangeExchange(_ => Task.FromResult("I did the thing."));

        var session = CreateSession();
        var result = await session.RunStepTurnAsync(
            Spec(offerStepResultTool: true), new RunContext("goal", RunProfile.Interactive),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        Assert.Null(result.Outcome);
    }

    /// <summary>The fallback's other half: no declaration and no text is still the empty-response failure.</summary>
    [Fact]
    public async Task NoDeclaration_AndNoText_StillFails()
    {
        ArrangeExchange(_ => Task.FromResult(string.Empty));

        var session = CreateSession();
        var result = await session.RunStepTurnAsync(
            Spec(offerStepResultTool: true), new RunContext("goal", RunProfile.Interactive),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal("Msg_Assistant_EmptyResponse", result.Error);
        Assert.Null(result.Outcome);
    }

    /// <summary>The model gets a vote on its own work, not on the transport.</summary>
    [Fact]
    public async Task ADeclaredSuccess_CannotOverrideAThrownExchange()
    {
        ArrangeExchange(async handler =>
        {
            await handler!(Emit(succeeded: true, summary: "all good"), new ToolDispatchContext(1));
            throw new InvalidOperationException("boom");
        });

        var session = CreateSession();
        var result = await session.RunStepTurnAsync(
            Spec(offerStepResultTool: true), new RunContext("goal", RunProfile.Interactive),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal("boom", result.Error);
        Assert.Null(result.Outcome);
    }

    // ---- scoping / the interception gate ----

    /// <summary>The interception is armed by the sink, not by the tool name, or an ordinary chat turn would
    /// silently swallow a hallucinated call.</summary>
    [Fact]
    public async Task ATurnThatWasNotOfferedTheTool_StillRoutesTheNameAndFindsNothing()
    {
        _plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?, PluginToolCall?)?)null);
        ArrangeExchange(async handler =>
        {
            await handler!(Emit(succeeded: false, summary: "should not be believed"), new ToolDispatchContext(1));
            return "some text";
        });

        var session = CreateSession();
        var result = await session.RunStepTurnAsync(
            Spec(offerStepResultTool: false), new RunContext("goal", RunProfile.Interactive),
            TestContext.Current.CancellationToken);

        Assert.Contains("Unknown tool.", _toolReplies.Select(r => r as string));
        Assert.Null(result.Outcome);
        Assert.True(result.Succeeded, result.Error); // fell back to the text heuristic, as an unoffered turn must
        await _plugins.Received().RouteToolCallAsync(
            Arg.Is<FunctionCallContent>(c => c.Name == AgentStepTools.EmitStepResultToolName),
            Arg.Any<CancellationToken>());
    }

    /// <summary>A second step turn re-assigns the sink on entry, so only an ordinary chat turn can observe the
    /// disarm — and a session outlives the run that borrowed it.</summary>
    [Fact]
    public async Task TheSinkIsDisarmedAfterTheStep_SoALaterChatTurnStillRoutesTheName()
    {
        _plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?, PluginToolCall?)?)null);
        var session = CreateSession();
        var ct = TestContext.Current.CancellationToken;

        ArrangeExchange(async handler =>
        {
            await handler!(Emit(succeeded: true, summary: "step one is fine"), new ToolDispatchContext(1));
            return "first";
        });
        var step = await session.RunStepTurnAsync(
            Spec(offerStepResultTool: true), new RunContext("goal", RunProfile.Interactive), ct);
        Assert.NotNull(step.Outcome);

        // An ordinary chat turn on the same session: the invented name must reach routing and dead-end there.
        _toolReplies.Clear();
        ArrangeExchange(async handler =>
        {
            await handler!(Emit(succeeded: false, summary: "leaked into the next turn"), new ToolDispatchContext(1));
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
        await _plugins.Received().RouteToolCallAsync(
            Arg.Is<FunctionCallContent>(c => c.Name == AgentStepTools.EmitStepResultToolName),
            Arg.Any<CancellationToken>());
    }

    /// <summary>The acknowledgement tells the model not to contradict its own declared failure in the reply.</summary>
    [Fact]
    public async Task TheModelIsToldTheDeclarationWasRecorded()
    {
        ArrangeExchange(async handler =>
        {
            await handler!(Emit(succeeded: false, summary: "blocked"), new ToolDispatchContext(1));
            return "text";
        });

        var session = CreateSession();
        await session.RunStepTurnAsync(
            Spec(offerStepResultTool: true), new RunContext("goal", RunProfile.Interactive),
            TestContext.Current.CancellationToken);

        var reply = Assert.IsType<string>(Assert.Single(_toolReplies));
        Assert.Contains("FAILED", reply, StringComparison.Ordinal);
    }
}
