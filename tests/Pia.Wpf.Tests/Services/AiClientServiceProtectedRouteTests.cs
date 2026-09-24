using System.Net.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Providers;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Regression: the "Protected" badge means a protected model wrote part of what the user reads. A guardrail
/// classifier ERROR that fail-closes one tool round and then recovers produced no prose, so it must NOT
/// stick; a round that answered in prose under the protected route must, even when a later round routes
/// normally. The server re-decides the route every tool round, so the client resets per round and latches
/// only on a marked round that emitted visible text.
/// </summary>
public class AiClientServiceProtectedRouteTests
{
    [Fact]
    public async Task ProtectedBadge_DoesNotLatch_WhenOnlyAnIntermediateRoundWasProtected()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => ProtectedThenToolCall(), _ => FinalCleanAnswer());

        var finished = await RunSingleTurnAsync(chatClient);
        Assert.False(finished.Protected);
    }

    [Fact]
    public async Task ProtectedBadge_IsSet_WhenTheFinalRoundIsProtected()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => ToolCallOnly(), _ => FinalProtectedAnswer());

        var finished = await RunSingleTurnAsync(chatClient);
        Assert.True(finished.Protected);
    }

    /// <summary>An agent step assembles its answer over several rounds. Prose a protected round already put
    /// in front of the user stays protected, even when the round that ends the step routes normally.</summary>
    [Fact]
    public async Task ProtectedBadge_IsSet_WhenAnEarlierRoundAnsweredInProseFromTheProtectedModel()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => ProtectedProseThenToolCall(), _ => FinalCleanAnswer());

        var finished = await RunSingleTurnAsync(chatClient);
        Assert.True(finished.Protected);
    }

    private static async Task<Finished> RunSingleTurnAsync(IChatClient chatClient)
    {
        var handler = Substitute.For<IAiProviderHandler>();
        handler.ProviderType.Returns(AiProviderType.OpenAI);
        handler.CreateChatClientAsync(
                Arg.Any<AiProvider>(), Arg.Any<string?>(), Arg.Any<HttpClient>(),
                Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(chatClient));
        handler.CreateChatOptions(Arg.Any<AiProvider>(), Arg.Any<bool>()).Returns(new ChatOptions());

        var resolver = new AiProviderHandlerResolver(new[] { handler });

        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());

        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());

        var sut = new AiClientService(
            new DpapiHelper(NullLogger<DpapiHelper>.Instance),
            httpFactory,
            settings,
            resolver,
            Substitute.For<IAuthService>(),
            NullLogger<AiClientService>.Instance,
            new ProviderRequestThrottle(settings, NullLogger<ProviderRequestThrottle>.Instance));

        var provider = new AiProvider
        {
            Name = "t",
            Endpoint = "http://localhost",
            ProviderType = AiProviderType.OpenAI,
            SupportsStreaming = true,
            SupportsToolCalling = true,
        };

        var items = new List<ChatStreamItem>();
        await foreach (var item in sut.GetChatCompletionWithToolsAsync(
            new List<ChatMessage> { new(ChatRole.User, "hi") },
            provider,
            tools: null,
            toolHandler: (_, _) => Task.FromResult<object?>("done")))
        {
            items.Add(item);
        }

        return Assert.Single(items.OfType<Finished>());
    }

    // Round 0: protected AND visibly answering, then a tool call → the prose is already on screen.
    private static async IAsyncEnumerable<ChatResponseUpdate> ProtectedProseThenToolCall()
    {
        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            AdditionalProperties = new AdditionalPropertiesDictionary { [GuardrailMarker.AdditionalPropertyKey] = true },
        };
        yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("protected prose")] };
        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new FunctionCallContent("call-1", "some_tool", new Dictionary<string, object?>())],
        };
        await Task.Yield();
    }

    // Round 0: fail-closed to the protected model (marker), then asks for a tool call → forces a 2nd round.
    private static async IAsyncEnumerable<ChatResponseUpdate> ProtectedThenToolCall()
    {
        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            AdditionalProperties = new AdditionalPropertiesDictionary { [GuardrailMarker.AdditionalPropertyKey] = true },
        };
        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new FunctionCallContent("call-1", "some_tool", new Dictionary<string, object?>())],
        };
        await Task.Yield();
    }

    // Round 1 (final answer): clean, no marker → the normal model answered.
    private static async IAsyncEnumerable<ChatResponseUpdate> FinalCleanAnswer()
    {
        yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("final answer")] };
        await Task.Yield();
    }

    // Round 0: clean, asks for a tool call → forces a 2nd round.
    private static async IAsyncEnumerable<ChatResponseUpdate> ToolCallOnly()
    {
        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new FunctionCallContent("call-1", "some_tool", new Dictionary<string, object?>())],
        };
        await Task.Yield();
    }

    // Round 1 (final answer): protected (marker) → the badge must show.
    private static async IAsyncEnumerable<ChatResponseUpdate> FinalProtectedAnswer()
    {
        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            AdditionalProperties = new AdditionalPropertiesDictionary { [GuardrailMarker.AdditionalPropertyKey] = true },
        };
        yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("final answer")] };
        await Task.Yield();
    }
}
