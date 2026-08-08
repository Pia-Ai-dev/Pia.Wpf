using System.Net.Http;
using System.Text.Json;
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
/// Running a call whose arguments failed to parse made the tool reject its own empty input — a
/// <c>read_file</c> with no path answered "Path is outside the assistant files folder" — and the model
/// reissued the identical call until the round budget ran out.
/// </summary>
public class AiClientServiceMalformedToolArgumentsTests
{
    [Fact]
    public async Task MalformedArguments_AreNotDispatched_AndReportTheRealReason()
    {
        var dispatched = new List<string>();
        var sent = await RunAsync(dispatched);

        Assert.Empty(dispatched);

        var results = sent[^1]
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .ToList();
        var result = Assert.Single(results);
        Assert.Equal("call-1", result.CallId);
        Assert.Equal(AiClientService.MalformedToolArgumentsResult, result.Result);
    }

    [Fact]
    public async Task MalformedArguments_StillLetTheLoopContinue()
    {
        // The turn must reach the model's next answer rather than stall on the skipped call.
        var sent = await RunAsync([]);
        Assert.Equal(2, sent.Count);
    }

    private static FunctionCallContent MalformedCall() =>
        FunctionCallContent.CreateFromParsedArguments(
            """{"path":"A.cs"}{"path":"B.cs"}""",
            "call-1",
            "read_file",
            static json => JsonSerializer.Deserialize<Dictionary<string, object?>>(json)!);

    private static async Task<List<List<ChatMessage>>> RunAsync(List<string> dispatched)
    {
        var sent = new List<List<ChatMessage>>();
        var round = 0;

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                sent.Add([.. ci.ArgAt<IEnumerable<ChatMessage>>(0)]);
                return round++ == 0 ? MalformedToolCallRound() : FinalAnswer();
            });

        var handler = Substitute.For<IAiProviderHandler>();
        handler.ProviderType.Returns(AiProviderType.OpenAI);
        handler.CreateChatClientAsync(
                Arg.Any<AiProvider>(), Arg.Any<string?>(), Arg.Any<HttpClient>(),
                Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(chatClient));
        handler.CreateChatOptions(Arg.Any<AiProvider>(), Arg.Any<bool>()).Returns(_ => new ChatOptions());

        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());

        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());

        var sut = new AiClientService(
            new DpapiHelper(NullLogger<DpapiHelper>.Instance),
            httpFactory,
            settings,
            new AiProviderHandlerResolver([handler]),
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

        await foreach (var _ in sut.GetChatCompletionWithToolsAsync(
            [new ChatMessage(ChatRole.User, "go")],
            provider,
            tools: null,
            toolHandler: (call, _) =>
            {
                dispatched.Add(call.Name);
                return Task.FromResult<object?>("ran");
            },
            mode: null,
            cancellationToken: TestContext.Current.CancellationToken))
        {
            // Drain: the assertions are on what reached the provider.
        }

        return sent;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> MalformedToolCallRound()
    {
        yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [MalformedCall()] };
        await Task.Yield();
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> FinalAnswer()
    {
        yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("final answer")] };
        await Task.Yield();
    }
}
