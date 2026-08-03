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
/// hermes #9 on the LIVE path. <c>LiveTurnExecutor</c> makes no step-success decision of its own — it
/// delegates to <c>ChatSession.RunStepTurnAsync</c>, so that is where the facts have to be driven. The live
/// predicate was a DIFFERENT premise in kind from the headless one (exception-absence plus the
/// empty-response downgrade, rather than non-empty text), which is exactly why a fix in one executor would
/// have been half a fix — this file is the other half.
/// <para>
/// The discriminating pair is <see cref="DeclaredFailure_WithPlentyOfText_DoesNotSucceed"/> and
/// <see cref="DeclaredSuccess_WithNoTextAtAll_Succeeds"/>. <b>Neutralize</b> both by commenting out the
/// <c>if (claim is not null) { succeeded = …; error = …; }</c> block that sits after the <c>finally</c> in
/// <c>RunStepTurnAsync</c>. Deleting the tool instead reds everything and proves nothing.
/// </para>
/// </summary>
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

    /// <summary>A step spec whose tool list is what <c>LiveTurnExecutor.BuildSpec</c> would have produced —
    /// with the declaration tool when <paramref name="offerStepResultTool"/>, without it otherwise.</summary>
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
    private void ArrangeExchange(Func<Func<FunctionCallContent, Task<object?>>?, Task<string>> drive)
    {
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<Func<FunctionCallContent, Task<object?>>?>(), Arg.Any<string?>(), Arg.Any<Guid?>(),
                cancellationToken: Arg.Any<CancellationToken>(), contextBudget: Arg.Any<AgentContextBudget?>())
            .Returns(ci => Stream(ci.ArgAt<Func<FunctionCallContent, Task<object?>>?>(3), drive));
    }

    private async IAsyncEnumerable<ChatStreamItem> Stream(
        Func<FunctionCallContent, Task<object?>>? handler,
        Func<Func<FunctionCallContent, Task<object?>>?, Task<string>> drive)
    {
        Func<FunctionCallContent, Task<object?>>? recording = handler is null
            ? null
            : async call =>
            {
                var reply = await handler(call);
                _toolReplies.Add(reply);
                return reply;
            };

        var text = await drive(recording);
        if (!string.IsNullOrEmpty(text))
            yield return new TextDelta(text);
        await Task.Yield();
    }

    // ---- the discriminating pair ----

    /// <summary>
    /// <b>THE RED DEMO.</b> The step throws nothing, streams a full paragraph of articulate prose, and
    /// declares <c>succeeded:false</c>. The old live predicate — no exception and no empty placeholder —
    /// returned <c>Succeeded:true</c>, which the orchestrator writes as <c>AgentStepStatus.Done</c>.
    /// </summary>
    [Fact]
    public async Task DeclaredFailure_WithPlentyOfText_DoesNotSucceed()
    {
        const string eloquent =
            "I tried to publish the release notes, but the target folder is read-only, so nothing was "
            + "written. Below is the text I would have published.";
        ArrangeExchange(async handler =>
        {
            await handler!(Emit(succeeded: false, summary: "the target folder is read-only"));
            return eloquent;
        });

        var session = CreateSession();
        var result = await session.RunStepTurnAsync(
            Spec(offerStepResultTool: true), new RunContext("goal", RunProfile.Interactive),
            TestContext.Current.CancellationToken);

        // The text really was there and nothing threw — this is the exact case the old predicate got wrong.
        Assert.Equal(eloquent, result.VisibleText);
        Assert.False(result.Succeeded);
        Assert.False(result.Cancelled);
        // The model's own reason becomes the error the orchestrator hands to ReplanAsync.
        Assert.Equal("the target folder is read-only", result.Error);
        Assert.NotNull(result.Outcome);
        Assert.False(result.Outcome!.Succeeded);
    }

    /// <summary>
    /// <b>THE INVERSE DEMO.</b> No visible text at all plus <c>succeeded:true</c>. The old predicate hit
    /// <c>CleanupPerExchange</c>'s empty-response downgrade and returned <c>Succeeded:false</c> with the
    /// localized "empty response" error; the declaration now clears both.
    /// </summary>
    [Fact]
    public async Task DeclaredSuccess_WithNoTextAtAll_Succeeds()
    {
        ArrangeExchange(async handler =>
        {
            await handler!(Emit(succeeded: true, summary: "renamed the columns", artifact: "data/clean.csv"));
            return string.Empty; // no TextDelta whatsoever
        });

        var session = CreateSession();
        var result = await session.RunStepTurnAsync(
            Spec(offerStepResultTool: true), new RunContext("goal", RunProfile.Interactive),
            TestContext.Current.CancellationToken);

        // The empty-response placeholder was still synthesized for the UI…
        Assert.Equal("Msg_Assistant_EmptyResponse", result.VisibleText);
        // …but the step is a success, because the step said so.
        Assert.True(result.Succeeded, result.Error);
        Assert.Null(result.Error);
        Assert.Equal("data/clean.csv", result.Outcome!.ArtifactRef);
    }

    // ---- the fallback ----

    /// <summary>THE FALLBACK: no declaration keeps the old predicate — non-empty text and no exception is
    /// still a success — but the result carries no claim, so the step is recorded UNCONFIRMED.</summary>
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

    /// <summary>
    /// <b>GUARD</b>. A declaration cannot paper over a transport failure: the model declares success and the
    /// exchange then throws. The step still fails — the model gets a vote on its work, not on the transport.
    /// </summary>
    [Fact]
    public async Task ADeclaredSuccess_CannotOverrideAThrownExchange()
    {
        ArrangeExchange(async handler =>
        {
            await handler!(Emit(succeeded: true, summary: "all good"));
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

    /// <summary>
    /// <b>GUARD</b>. The interception is armed by the SINK, not by the tool name: a turn that was never
    /// offered the tool routes a hallucinated <c>emit_step_result</c> the ordinary way and gets the honest
    /// "Unknown tool." answer, with no claim recorded. Without this gate an ordinary chat turn would silently
    /// swallow the call.
    /// </summary>
    [Fact]
    public async Task ATurnThatWasNotOfferedTheTool_StillRoutesTheNameAndFindsNothing()
    {
        _plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?, PluginToolCall?)?)null);
        ArrangeExchange(async handler =>
        {
            await handler!(Emit(succeeded: false, summary: "should not be believed"));
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

    /// <summary>
    /// <b>GUARD</b>. The sink is DISARMED when a step turn ends, and the turn shape that proves it is an
    /// ORDINARY chat turn — not a second step turn.
    /// <para>
    /// A second <c>RunStepTurnAsync</c> re-assigns the field on entry, so it would pass whether or not the
    /// <c>finally</c> clears anything: it observes the re-assignment, not the disarm. <c>RunTurnAsync</c>
    /// never touches the field, so it is the only caller that can see a leak — and it is the real hazard,
    /// since a session outlives the run that borrowed it. With the disarm removed this turn swallows a
    /// hallucinated <c>emit_step_result</c> instead of answering "Unknown tool.".
    /// </para>
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
            await handler!(Emit(succeeded: true, summary: "step one is fine"));
            return "first";
        });
        var step = await session.RunStepTurnAsync(
            Spec(offerStepResultTool: true), new RunContext("goal", RunProfile.Interactive), ct);
        Assert.NotNull(step.Outcome);

        // Now an ORDINARY chat turn on the same session — never offered the tool, so the model inventing the
        // name must reach routing and dead-end there.
        _toolReplies.Clear();
        ArrangeExchange(async handler =>
        {
            await handler!(Emit(succeeded: false, summary: "leaked into the next turn"));
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

    /// <summary>The model's acknowledgement tells it the verdict was taken — and, for a declared failure,
    /// not to contradict itself in the visible reply.</summary>
    [Fact]
    public async Task TheModelIsToldTheDeclarationWasRecorded()
    {
        ArrangeExchange(async handler =>
        {
            await handler!(Emit(succeeded: false, summary: "blocked"));
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
