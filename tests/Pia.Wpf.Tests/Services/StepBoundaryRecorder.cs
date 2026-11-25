using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
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
/// A real tool loop over a scripted chat client that records every request as Pia Cloud serializes it, so a
/// step boundary can be compared byte for byte with the request before it.
/// </summary>
internal sealed class StepBoundaryRecorder
{
    internal const string RoundOneText = "Reading the inventory first.";
    internal const string StepOneAnswer = "The inventory has three rows.";
    internal const string StepTwoAnswer = "Wrote the report.";
    internal const string SmallResult = "SKU-1001,Blue Widget,4,10,3.50";

    // Over the carry cap, so a capped carry shows up as a divergence.
    internal static readonly string LargeResult = new('x', AgentToolCarryover.MaxCarriedResultChars + 500);

    /// <summary>Requests 0–2 are step 1 (two tool rounds, then the answer); request 3 opens step 2.</summary>
    public List<JsonArray> Sent { get; } = [];

    public IAiClientService Ai { get; }

    public StepBoundaryRecorder()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                Sent.Add(PiaCloudChatClient.SerializeMessages(ci.ArgAt<IEnumerable<ChatMessage>>(0)));
                return (Sent.Count - 1) switch
                {
                    0 => Round(new TextContent(RoundOneText), Read("call-1", "inventory.csv")),
                    1 => Round(Read("call-2", "catalog.csv")),
                    2 => Round(new TextContent(StepOneAnswer)),
                    _ => Round(new TextContent(StepTwoAnswer)),
                };
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

        Ai = new AiClientService(
            new DpapiHelper(NullLogger<DpapiHelper>.Instance),
            httpFactory,
            settings,
            new AiProviderHandlerResolver([handler]),
            Substitute.For<IAuthService>(),
            NullLogger<AiClientService>.Instance,
            new ProviderRequestThrottle(settings, NullLogger<ProviderRequestThrottle>.Instance));
    }

    /// <summary>Answers every read: the catalog over the carry cap, anything else small.</summary>
    public static void RouteReads(IPluginService plugins) =>
        plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<(object? Result, PluginToolCall? PendingAction)?>(
                (Path(ci.ArgAt<FunctionCallContent>(0)) == "catalog.csv" ? LargeResult : SmallResult, null)));

    /// <summary>
    /// Step 2's first request must be step 1's last request, then step 1's answer as received, then one new
    /// user instruction. <paramref name="from"/> = 1 skips the system message when the personas differ.
    /// </summary>
    public void AssertStepTwoExtendsStepOne(int from = 0)
    {
        Assert.Equal(4, Sent.Count);
        var expected = Sent[2].Select(Json)
            .Append(Json(PiaCloudChatClient.SerializeMessages([new ChatMessage(ChatRole.Assistant, StepOneAnswer)])[0]))
            .ToList();
        var actual = Sent[3].Select(Json).ToList();

        for (var i = from; i < expected.Count; i++)
        {
            var got = i < actual.Count - 1 ? actual[i] : null;
            if (got != expected[i])
            {
                Assert.Fail($"Step 2's first request diverges from step 1's last request at message {i} "
                    + $"(step 1 sent {Sent[2].Count}, step 2 sent {actual.Count}).\n"
                    + $"expected: {Clip(expected[i])}\nactual:   {Clip(got ?? "<instruction or nothing>")}");
            }
        }

        Assert.Equal(expected.Count + 1, actual.Count);
        Assert.Equal("user", Sent[3][^1]!["role"]!.GetValue<string>());
    }

    private static string Json(JsonNode? node) => node!.ToJsonString();

    private static string Clip(string json) => json.Length > 300 ? json[..300] + "…" : json;

    private static string? Path(FunctionCallContent call) =>
        call.Arguments?.TryGetValue("path", out var value) == true
            ? value switch { JsonElement e => e.GetString(), _ => value?.ToString() }
            : null;

    private static FunctionCallContent Read(string callId, string path) =>
        new(callId, "read_file", new Dictionary<string, object?> { ["path"] = path });

    private static async IAsyncEnumerable<ChatResponseUpdate> Round(params AIContent[] contents)
    {
        yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [.. contents] };
        await Task.Yield();
    }
}
