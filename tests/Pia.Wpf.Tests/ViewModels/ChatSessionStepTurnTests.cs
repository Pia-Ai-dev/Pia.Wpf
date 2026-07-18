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
/// The additive step-turn path (§13.7/§16 R4): a step-turn clears IsStreaming and detokenizes PII
/// via the shared per-exchange cleanup, converts exceptions into a failed StepTurnResult (never
/// ChatState.Error / a RunFailed snackbar), keeps the ephemeral instruction out of the transcript,
/// and restores the ambients.
/// </summary>
public sealed class ChatSessionStepTurnTests
{
    private readonly IAiClientService _ai = Substitute.For<IAiClientService>();
    private readonly IPluginService _plugins = Substitute.For<IPluginService>();
    private readonly IActionCardBuilder _cards = Substitute.For<IActionCardBuilder>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly ITokenMapService _tokenMap = Substitute.For<ITokenMapService>();
    private readonly IToolPermissionService _permissions = Substitute.For<IToolPermissionService>();

    public ChatSessionStepTurnTests()
    {
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        _loc.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => (string)ci[0]);
    }

    private ChatSession CreateSession() => new(
        _tokenMap, _ai, _plugins, _cards, _permissions, _loc, NullLogger.Instance, _ => true);

    private static StepTurnSpec Spec(bool tokenizationEnabled) => new(
        RunId: Guid.NewGuid(),
        Ordinal: 0,
        Intent: "do the thing",
        ExpectedArtifact: "artifact",
        SystemPrompt: "system",
        Persona: new PersonaAttribution(Guid.NewGuid(), "Pia", "🤖"),
        Provider: new AiProvider { Name = "Test", Endpoint = "http://localhost", ProviderType = AiProviderType.OpenAI },
        Tools: null,
        SupportsTools: false,
        WebSearchActive: false,
        TokenizationEnabled: tokenizationEnabled);

    private void ReturnsStream(Func<IAsyncEnumerable<ChatStreamItem>> factory)
    {
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<Func<FunctionCallContent, Task<object?>>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => factory());
    }

    private static async IAsyncEnumerable<ChatStreamItem> Stream(params ChatStreamItem[] items)
    {
        foreach (var item in items)
        {
            yield return item;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<ChatStreamItem> ThrowingStream(Exception ex)
    {
        await Task.Yield();
        throw ex;
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    [Fact]
    public async Task RunStepTurn_ClearsIsStreaming_DetokenizesPii()
    {
        _tokenMap.Detokenize(Arg.Any<string>()).Returns(ci => "DETOK:" + (string)ci[0]);
        ReturnsStream(() => Stream(new TextDelta("<tok>"), new Finished(null, "m")));

        var session = CreateSession();
        session.Messages.Add(new AssistantMessage(ChatRole.User, "goal"));
        var states = new List<ChatState>();
        session.StateChanged += (_, e) => states.Add(e.NewState);
        var failed = 0;
        session.RunFailed += (_, _) => failed++;

        TaskAmbient.Current = null;
        TokenMapAmbient.Current = null;

        var result = await session.RunStepTurnAsync(Spec(tokenizationEnabled: true), new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        Assert.True(result.Succeeded);
        var assistant = session.Messages.Last(m => !m.IsUser);
        Assert.False(assistant.IsStreaming);
        _tokenMap.Received().Detokenize(Arg.Any<string>());
        Assert.StartsWith("DETOK:", assistant.Content);
        Assert.NotEqual(ChatState.Error, session.State);
        Assert.DoesNotContain(ChatState.Error, states);
        Assert.Equal(0, failed);
        Assert.Null(TaskAmbient.Current);
        Assert.Null(TokenMapAmbient.Current);
        // Ephemeral instruction is never mirrored into the transcript (§13.7).
        Assert.Equal(2, session.Messages.Count);
        Assert.DoesNotContain(session.Messages, m => m.Content.Contains("Execute step 1"));
    }

    [Fact]
    public async Task RunStepTurn_Exception_ReturnsFailedResult_NoErrorState()
    {
        ReturnsStream(() => ThrowingStream(new InvalidOperationException("boom")));

        var session = CreateSession();
        session.Messages.Add(new AssistantMessage(ChatRole.User, "goal"));
        var failed = 0;
        session.RunFailed += (_, _) => failed++;

        var result = await session.RunStepTurnAsync(Spec(tokenizationEnabled: false), new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
        Assert.Contains("boom", result.Error);
        Assert.NotEqual(ChatState.Error, session.State);
        Assert.Equal(0, failed);
        Assert.False(session.Messages.Last(m => !m.IsUser).IsStreaming);
    }

    [Fact]
    public async Task RunStepTurn_Cancelled_ReturnsCancelledResult()
    {
        ReturnsStream(() => ThrowingStream(new OperationCanceledException("cancelled")));

        var session = CreateSession();
        session.Messages.Add(new AssistantMessage(ChatRole.User, "goal"));

        var result = await session.RunStepTurnAsync(Spec(tokenizationEnabled: false), new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        Assert.True(result.Cancelled);
        Assert.False(result.Succeeded);
        Assert.NotEqual(ChatState.Error, session.State);
    }
}
