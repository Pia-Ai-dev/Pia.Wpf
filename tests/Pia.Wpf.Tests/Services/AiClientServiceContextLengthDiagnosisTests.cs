using System.ClientModel;
using System.Net;
using System.Net.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Providers;
using Xunit;

namespace Pia.Tests.Services;

/// <summary><c>IsToolNotSupportedError</c> answers true for essentially any 400, so a context overflow used to
/// be reported as "retrying without tools" — the wrong top-line diagnosis in a support log.</summary>
public class AiClientServiceContextLengthDiagnosisTests
{
    /// <summary>A real OpenAI overflow body, trimmed. Also the shape Azure OpenAI and vLLM return.</summary>
    private const string OpenAiOverflow =
        "Service request failed.\nStatus: 400 (Bad Request)\n\n{\"error\":{\"message\":\"This model's " +
        "maximum context length is 8192 tokens. However, your messages resulted in 12034 tokens. Please " +
        "reduce the length of the messages.\",\"type\":\"invalid_request_error\",\"param\":\"messages\"," +
        "\"code\":\"context_length_exceeded\"}}";

    /// <summary>Stands in for user content: it must never appear in the diagnosis line.</summary>
    private const string GoalText = "THE GOAL: ship the run spine, and this text must never reach a log line.";

    [Theory]
    [InlineData(OpenAiOverflow)]
    [InlineData("{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"prompt is too long: 218898 tokens > 199999 maximum\"}}")]
    [InlineData("The input token count (1189302) exceeds the maximum number of tokens allowed (1048576).")]
    [InlineData("Too many tokens in prompt: 40000 > 32768")]
    [InlineData("the request exceeds the available context size. try increasing the context size or enable context shift")]
    [InlineData("This endpoint's maximum context length is 16384 tokens. However, you requested 20114 tokens.")]
    public void ContextLengthShapes_AreClassified(string body)
    {
        // One body per provider family Pia talks to.
        Assert.True(
            AiClientService.IsContextLengthError(
                new HttpRequestException(body, null, HttpStatusCode.BadRequest)),
            $"a real context-length rejection was not recognised: {body}");
    }

    [Fact]
    public void ContextLengthShape_IsClassified_OnTheExceptionTypeTheOpenAiAdapterThrows()
    {
        // This shape carries Status 0, which is why the classifier must not re-check the status code — the call
        // site has already established 400/404.
        Assert.True(AiClientService.IsContextLengthError(new ClientResultException(OpenAiOverflow)));
    }

    [Theory]
    [InlineData("Service request failed.\nStatus: 400 (Bad Request)\n\n{\"error\":{\"message\":\"Invalid value: 'tools' is not supported by this model.\",\"type\":\"invalid_request_error\"}}")]
    [InlineData("{\"error\":{\"message\":\"tool use is not supported for this model\",\"type\":\"invalid_request_error\"}}")]
    [InlineData("{\"error\":{\"message\":\"Unrecognized request argument supplied: tool_choice\"}}")]
    public void ToolNotSupportedShapes_AreNotMisclassified(string body)
    {
        Assert.False(
            AiClientService.IsContextLengthError(
                new HttpRequestException(body, null, HttpStatusCode.BadRequest)),
            $"a tool-support rejection was misclassified as a context overflow: {body}");
    }

    [Theory]
    [InlineData("{\"error\":{\"message\":\"Invalid value for 'temperature': must be <= 2\"}}")]
    [InlineData("{\"error\":{\"message\":\"model 'gpt-nope' does not exist\"}}")]
    [InlineData("")]
    public void UnrelatedBadRequests_AreNotClassified(string body)
    {
        Assert.False(AiClientService.IsContextLengthError(
            new HttpRequestException(body, null, HttpStatusCode.BadRequest)));
    }

    [Fact]
    public void NonProviderExceptions_AreNotClassified_EvenWithAMatchingMessage()
    {
        // Type gate: only the two exception types a provider adapter actually throws are considered.
        Assert.False(AiClientService.IsContextLengthError(
            new InvalidOperationException("maximum context length is 8192 tokens")));
    }

    [Fact]
    public async Task StreamingOverflow_NamesTheRealCauseFirst_AndStillRetriesWithoutTools()
    {
        var run = await RunAsync(
            streaming: true,
            firstCallError: new HttpRequestException(OpenAiOverflow, null, HttpStatusCode.BadRequest));

        // The catch body logs the tool-support line, so the diagnosis has to be its first statement.
        var overflow = run.Logger.IndexOf("Context overflow");
        var toolLine = run.Logger.IndexOf("retrying without tools");
        Assert.True(overflow >= 0, "the context-overflow diagnosis line was not logged at all");
        Assert.True(toolLine >= 0, "the pre-existing tool-disabled retry line disappeared");
        Assert.True(
            overflow < toolLine,
            $"the wrong diagnosis was logged first (overflow line at {overflow}, tool-support line at {toolLine})");

        // Metadata only, because release logs get attached to support tickets. Scoped to this one line:
        // SensitiveDebug is live in a Debug test build, so a suite-wide assertion would measure something else.
        var entry = run.Logger.Entries[overflow];
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Null(entry.Exception);
        Assert.DoesNotContain("context_length_exceeded", entry.Message);
        Assert.DoesNotContain("maximum context length", entry.Message);
        Assert.DoesNotContain(GoalText, entry.Message);

        Assert.Contains("carried 1 message(s)", entry.Message);
        Assert.Contains("contextBudgetConfigured=True", entry.Message);
        Assert.Contains("windowTokens=8000", entry.Message);
        Assert.Contains("maxOutputTokens=2000", entry.Message);

        // The retry is unchanged: this block already passed before the diagnosis line existed.
        Assert.Equal(2, run.OptionsPerCall.Count);
        Assert.Single(run.OptionsPerCall[0]!.Tools!);
        Assert.Null(run.OptionsPerCall[1]!.Tools);
        Assert.Contains(run.Items, i => i is TextDelta { Text: "final answer" });
        Assert.Single(run.Items.OfType<Finished>());
    }

    [Fact]
    public async Task NonStreamingOverflow_NamesTheRealCauseFirstToo_AndStillRetriesWithoutTools()
    {
        // The catch body is duplicated per provider path, so the non-streaming one needs the same call in the
        // same position or half the fleet keeps the wrong diagnosis.
        var run = await RunAsync(
            streaming: false,
            firstCallError: new HttpRequestException(OpenAiOverflow, null, HttpStatusCode.BadRequest));

        var overflow = run.Logger.IndexOf("Context overflow");
        var toolLine = run.Logger.IndexOf("retrying without tools");
        Assert.True(overflow >= 0, "the non-streaming path did not log the context-overflow diagnosis");
        Assert.True(toolLine >= 0, "the pre-existing tool-disabled retry line disappeared");
        Assert.True(overflow < toolLine, "the non-streaming path logged the wrong diagnosis first");

        Assert.Equal(2, run.OptionsPerCall.Count);
        Assert.Single(run.OptionsPerCall[0]!.Tools!);
        Assert.Null(run.OptionsPerCall[1]!.Tools);
        Assert.Contains(run.Items, i => i is TextDelta { Text: "final answer" });
    }

    [Fact]
    public async Task UnrecognisedBadRequest_LogsNoOverflowLine_AndBehavesExactlyAsBefore()
    {
        // The substring list will miss provider phrasings; when it does, the behaviour degrades to the old
        // tool-disabled retry and never to worse.
        var run = await RunAsync(
            streaming: true,
            firstCallError: new HttpRequestException(
                "{\"error\":{\"message\":\"Invalid value: 'tools' is not supported by this model.\"}}",
                null,
                HttpStatusCode.BadRequest));

        Assert.True(
            run.Logger.IndexOf("Context overflow") < 0,
            "a non-overflow 400 must not be labelled a context overflow");
        Assert.True(run.Logger.IndexOf("retrying without tools") >= 0);
        Assert.Equal(2, run.OptionsPerCall.Count);
        Assert.Null(run.OptionsPerCall[1]!.Tools);
        Assert.Single(run.Items.OfType<Finished>());
    }

    private sealed record RunResult(
        List<ChatStreamItem> Items, List<ChatOptions?> OptionsPerCall, FakeLogger Logger);

    /// <summary>The first provider call throws <paramref name="firstCallError"/> and the tool-disabled retry
    /// answers with text; every call's <see cref="ChatOptions"/> and log line is recorded.</summary>
    private static async Task<RunResult> RunAsync(bool streaming, Exception firstCallError)
    {
        var logger = new FakeLogger();
        var optionsPerCall = new List<ChatOptions?>();
        var calls = 0;

        var chatClient = Substitute.For<IChatClient>();
        if (streaming)
        {
            chatClient.GetStreamingResponseAsync(
                    Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    optionsPerCall.Add(ci.ArgAt<ChatOptions?>(1));
                    if (++calls == 1)
                    {
                        throw firstCallError;
                    }

                    return FinalAnswer();
                });
        }
        else
        {
            chatClient.GetResponseAsync(
                    Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    optionsPerCall.Add(ci.ArgAt<ChatOptions?>(1));
                    if (++calls == 1)
                    {
                        throw firstCallError;
                    }

                    return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "final answer")));
                });
        }

        var handler = Substitute.For<IAiProviderHandler>();
        handler.ProviderType.Returns(AiProviderType.OpenAI);
        handler.CreateChatClientAsync(
                Arg.Any<AiProvider>(), Arg.Any<string?>(), Arg.Any<HttpClient>(),
                Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(chatClient));
        // A fresh ChatOptions per call, or the retry cannot be observed as "no tools": the first call wrote its
        // tools into the shared instance.
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
            logger,
            new ProviderRequestThrottle(settings, NullLogger<ProviderRequestThrottle>.Instance));

        var provider = new AiProvider
        {
            Name = "t",
            Endpoint = "http://localhost",
            ProviderType = AiProviderType.OpenAI,
            SupportsStreaming = streaming,
            SupportsToolCalling = true,
        };

        var items = new List<ChatStreamItem>();
        await foreach (var item in sut.GetChatCompletionWithToolsAsync(
            [new ChatMessage(ChatRole.User, GoalText)],
            provider,
            tools: [AIFunctionFactory.Create(() => "ok", "ping", "A test tool.")],
            toolHandler: (_, _) => Task.FromResult<object?>("done"),
            mode: null,
            cancellationToken: TestContext.Current.CancellationToken,
            contextBudget: new AgentContextBudget(8_000, 2_000)))
        {
            items.Add(item);
        }

        return new RunResult(items, optionsPerCall, logger);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> FinalAnswer()
    {
        yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("final answer")] };
        await Task.Yield();
    }

    /// <summary>Captures every line in order, because the order is what this file asserts.</summary>
    private sealed class FakeLogger : ILogger<AiClientService>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public int IndexOf(string fragment)
            => Entries.FindIndex(e => e.Message.Contains(fragment, StringComparison.Ordinal));

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
    }
}