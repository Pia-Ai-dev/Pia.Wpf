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
/// The tool loop's image hand-over, driven through the public iterator with a fake chat client: a handler
/// parks a picture, the loop appends it after the round's LAST result, and the next round swaps it for text.
/// </summary>
public class AiClientServiceToolImageTests
{
    private static ToolLoopImage Image(string callId = "call-1") =>
        new(callId, [1, 2, 3, 4], "image/jpeg", 4, 4, "a caption");

    [Fact]
    public async Task ParkedImage_IsAppendedAfterEveryToolResultOfTheRound()
    {
        var harness = new Harness { CallsPerRound = 2 };
        harness.OnDispatch = call =>
        {
            if (call.CallId == "call-1") ToolLoopImageChannel.Current!.Park(Image());
        };

        await harness.RunAsync();

        var sent = harness.Sent[1];
        var roles = sent.Select(m => m.Role).ToList();

        // No user message wedged between two tool results: a provider rejects that request outright.
        var firstTool = roles.IndexOf(ChatRole.Tool);
        var lastTool = roles.LastIndexOf(ChatRole.Tool);
        Assert.True(firstTool >= 0 && lastTool > firstTool, "the round appended two tool results");
        for (var i = firstTool; i <= lastTool; i++)
            Assert.NotEqual(ChatRole.User, roles[i]);

        var withImages = sent.Where(ToolLoopImageMessages.CarriesImage).ToList();
        var image = Assert.Single(withImages);
        Assert.Same(sent[^1], image);
        Assert.Equal(ChatRole.User, image.Role);
        Assert.Contains("a caption", image.Text);
    }

    [Fact]
    public async Task ParkedImage_IsSentOnce_ThenReplacedByThePlaceholder()
    {
        var harness = new Harness { ToolRounds = 2 };
        var parked = false;
        harness.OnDispatch = _ =>
        {
            if (parked) return;
            parked = true;
            ToolLoopImageChannel.Current!.Park(Image());
        };

        await harness.RunAsync();

        var roundOne = harness.Sent[1].Where(ToolLoopImageMessages.CarriesImage).ToList();
        Assert.Single(roundOne);
        var roundTwo = harness.Sent[2].Where(ToolLoopImageMessages.CarriesImage).ToList();
        Assert.Empty(roundTwo);

        var tagged = harness.Sent[2].Where(ToolLoopImageMessages.IsTagged).ToList();
        var placeholder = Assert.Single(tagged);
        Assert.Equal(ToolLoopImageMessages.Placeholder(4, 4), placeholder.Text);
    }

    [Fact]
    public async Task ImageMessage_IsNotCarriedInTheToolRoundExchange()
    {
        var harness = new Harness();
        harness.OnDispatch = _ => ToolLoopImageChannel.Current!.Park(Image());

        var items = await harness.RunAsync();

        var exchange = items.OfType<ToolRoundExchange>().First();
        var contents = exchange.Messages.SelectMany(m => m.Contents).ToList();
        Assert.Equal("call-1", Assert.Single(contents.OfType<FunctionCallContent>()).CallId);
        Assert.Equal("call-1", Assert.Single(contents.OfType<FunctionResultContent>()).CallId);
        Assert.Empty(contents.OfType<DataContent>());
    }

    [Theory]
    [InlineData(AiProviderType.OpenAI)]
    [InlineData(AiProviderType.PiaCloud)]
    public async Task Channel_CarriesTheRoundsProviderType(AiProviderType providerType)
    {
        // BOTH types flip: AiProviderHandlerResolver keys on the handler's ProviderType, so a mismatch
        // fails before the loop ever runs.
        var harness = new Harness { ProviderType = providerType };
        AiProviderType? seen = null;
        harness.OnDispatch = _ => seen = ToolLoopImageChannel.Current?.ProviderType;

        await harness.RunAsync();

        Assert.Equal(providerType, seen);
    }

    [Fact]
    public async Task Channel_IsGoneAfterTheDispatch()
    {
        var harness = new Harness();
        harness.OnDispatch = _ => ToolLoopImageChannel.Current!.Park(Image());

        await harness.RunAsync();

        Assert.Null(ToolLoopImageChannel.Current);
    }

    /// <summary>An image parked in the LAST round rides the tool-free wrap-up — its first and only delivery.</summary>
    [Fact]
    public async Task WrapUpRound_CarriesAnUnconsumedImageOnce()
    {
        var harness = new Harness { MaxToolRounds = 1, ToolRounds = 1, WrapUp = true };
        harness.OnDispatch = _ => ToolLoopImageChannel.Current!.Park(Image());

        await harness.RunAsync();

        var wrapUp = Assert.Single(harness.WrapUpSent);
        var carried = wrapUp.Where(ToolLoopImageMessages.CarriesImage).ToList();
        Assert.Single(carried);
    }

    [Fact]
    public async Task MalformedCall_SkipsDispatch_AndStillDrainsTheRound()
    {
        var harness = new Harness { CallsPerRound = 2, MalformFirstCall = true };
        harness.OnDispatch = _ => ToolLoopImageChannel.Current!.Park(Image("call-2"));

        await harness.RunAsync();

        Assert.Equal(["call-2"], harness.Dispatched);
        var sent = harness.Sent[1];
        var withImages = sent.Where(ToolLoopImageMessages.CarriesImage).ToList();
        Assert.Same(sent[^1], Assert.Single(withImages));
    }

    private sealed class Harness
    {
        public IChatClient ChatClient { get; } = Substitute.For<IChatClient>();

        public int MaxToolRounds { get; init; } = 24;

        /// <summary>How many consecutive rounds answer with tool calls before the final text round.</summary>
        public int ToolRounds { get; init; } = 1;

        public int CallsPerRound { get; init; } = 1;

        public bool MalformFirstCall { get; init; }

        public bool WrapUp { get; init; }

        public AiProviderType ProviderType { get; init; } = AiProviderType.OpenAI;

        public Action<FunctionCallContent>? OnDispatch { get; set; }

        public List<string> Dispatched { get; } = [];

        public List<List<ChatMessage>> Sent { get; } = [];

        public List<List<ChatMessage>> WrapUpSent { get; } = [];

        public async Task<List<ChatStreamItem>> RunAsync()
        {
            var handler = Substitute.For<IAiProviderHandler>();
            handler.ProviderType.Returns(ProviderType);
            handler.CreateChatClientAsync(
                    Arg.Any<AiProvider>(), Arg.Any<string?>(), Arg.Any<HttpClient>(),
                    Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(ChatClient));
            handler.CreateChatOptions(Arg.Any<AiProvider>(), Arg.Any<bool>()).Returns(_ => new ChatOptions());

            var round = 0;
            ChatClient.GetStreamingResponseAsync(
                    Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    Sent.Add([.. ci.ArgAt<IEnumerable<ChatMessage>>(0)]);
                    return round++ < ToolRounds ? ToolCallRound(CallsPerRound, MalformFirstCall) : TextRound("done");
                });
            ChatClient.GetResponseAsync(
                    Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    WrapUpSent.Add([.. ci.ArgAt<IEnumerable<ChatMessage>>(0)]);
                    return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "best effort")));
                });

            var httpFactory = Substitute.For<IHttpClientFactory>();
            httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());

            var settings = Substitute.For<ISettingsService>();
            settings.GetSettingsAsync().Returns(new AppSettings { MaxToolRoundsPerStep = MaxToolRounds });

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
                ProviderType = ProviderType,
                SupportsStreaming = true,
                SupportsToolCalling = true,
            };

            var items = new List<ChatStreamItem>();
            await foreach (var item in sut.GetChatCompletionWithToolsAsync(
                [new ChatMessage(ChatRole.User, "go")],
                provider,
                [AIFunctionFactory.Create(() => "ok", "read_file", "reads a file")],
                toolHandler: (call, _) =>
                {
                    Dispatched.Add(call.CallId ?? "");
                    OnDispatch?.Invoke(call);
                    return Task.FromResult<object?>("tool output");
                },
                mode: null,
                cancellationToken: TestContext.Current.CancellationToken))
            {
                items.Add(item);
            }

            Assert.True(WrapUp || WrapUpSent.Count == 0, "an unexpected tool-free wrap-up round was spent");
            return items;
        }

        private static async IAsyncEnumerable<ChatResponseUpdate> ToolCallRound(int calls, bool malformFirst)
        {
            var contents = new List<AIContent>();
            for (var i = 1; i <= calls; i++)
            {
                contents.Add(malformFirst && i == 1
                    ? new FunctionCallContent($"call-{i}", "read_file", null) { Exception = new InvalidOperationException("bad json") }
                    : new FunctionCallContent($"call-{i}", "read_file", new Dictionary<string, object?> { ["path"] = "A.cs" }));
            }

            yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = contents };
            await Task.Yield();
        }

        private static async IAsyncEnumerable<ChatResponseUpdate> TextRound(string text)
        {
            yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent(text)] };
            await Task.Yield();
        }
    }
}
