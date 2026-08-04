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

/// <summary>
/// Covers <c>AiClientService.IsContextLengthError</c> and the ORDER in which the tool-not-supported catch
/// bodies log — the only two things this change adds.
/// <para>
/// WHY THIS FILE EXISTS: <c>IsToolNotSupportedError</c> answers true for essentially ANY 400, so a request
/// that overflowed the model's context window was reported as "retrying without tools" — the wrong top-line
/// diagnosis in a support log, plus one wasted round trip re-sending the same oversized list. The fix is
/// diagnosis only: a metadata-only Warning naming the real cause, emitted BEFORE the tool-not-supported line.
/// Nothing about the retry changes, which is why half of this file pins the retry behaviour exactly as it is.
/// </para>
/// <para>
/// NOTE: <c>IsToolNotSupportedError</c> itself has no test anywhere in the suite (it is <c>private</c>). This
/// file does not change that; it pins its OBSERVABLE effect — the tool-disabled retry — end to end instead.
/// Same seam as <c>AiClientServiceInStepCompactionTests</c>: the REAL service against a scripted
/// <see cref="IChatClient"/>.
/// </para>
/// </summary>
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
        // One per provider family Pia talks to: OpenAI/Azure/vLLM, Anthropic, Gemini, Mistral,
        // llama.cpp/Ollama, OpenRouter.
        Assert.True(
            AiClientService.IsContextLengthError(
                new HttpRequestException(body, null, HttpStatusCode.BadRequest)),
            $"a real context-length rejection was not recognised: {body}");
    }

    [Fact]
    public void ContextLengthShape_IsClassified_OnTheExceptionTypeTheOpenAiAdapterThrows()
    {
        // Built from the body alone, so its Status is 0 (measured) — which is exactly why the classifier does
        // not re-check the status code: the call site's filter has already established 400/404 through
        // IsToolNotSupportedError, and a re-check here would go blind on this shape.
        Assert.True(AiClientService.IsContextLengthError(new ClientResultException(OpenAiOverflow)));
    }

    [Theory]
    [InlineData("Service request failed.\nStatus: 400 (Bad Request)\n\n{\"error\":{\"message\":\"Invalid value: 'tools' is not supported by this model.\",\"type\":\"invalid_request_error\"}}")]
    [InlineData("{\"error\":{\"message\":\"tool use is not supported for this model\",\"type\":\"invalid_request_error\"}}")]
    [InlineData("{\"error\":{\"message\":\"Unrecognized request argument supplied: tool_choice\"}}")]
    public void ToolNotSupportedShapes_AreNotMisclassified(string body)
    {
        // The genuine tool-capability rejections must keep reading as exactly that. If one of these ever
        // classified as an overflow, the new line would slander a working diagnosis.
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
        // Type gate, the same defensive posture as IsToolNotSupportedError: only the two exception types a
        // provider adapter actually throws are considered.
        Assert.False(AiClientService.IsContextLengthError(
            new InvalidOperationException("maximum context length is 8192 tokens")));
    }

    [Fact]
    public async Task StreamingOverflow_NamesTheRealCauseFirst_AndStillRetriesWithoutTools()
    {
        var run = await RunAsync(
            streaming: true,
            firstCallError: new HttpRequestException(OpenAiOverflow, null, HttpStatusCode.BadRequest));

        // 1. The real cause is named, and named FIRST. This ordering is the entire point of the change: the
        //    catch BODY logs the tool-support line, so the diagnosis has to be its first statement.
        var overflow = run.Logger.IndexOf("Context overflow");
        var toolLine = run.Logger.IndexOf("retrying without tools");
        Assert.True(overflow >= 0, "the context-overflow diagnosis line was not logged at all");
        Assert.True(toolLine >= 0, "the pre-existing tool-disabled retry line disappeared");
        Assert.True(
            overflow < toolLine,
            $"the wrong diagnosis was logged first (overflow line at {overflow}, tool-support line at {toolLine})");

        // 2. Privacy — the whole risk of this change. The new line carries metadata only: no provider body,
        //    no user content, no exception attached. Release logs get attached to support tickets. Scoped to
        //    the new line on purpose: SensitiveDebug IS live in a Debug test build, so a suite-wide
        //    assertion would be measuring something else.
        var entry = run.Logger.Entries[overflow];
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Null(entry.Exception);
        Assert.DoesNotContain("context_length_exceeded", entry.Message);
        Assert.DoesNotContain("maximum context length", entry.Message);
        Assert.DoesNotContain(GoalText, entry.Message);

        // 3. It says what it knows: the round, the message count, and the configured budget.
        Assert.Contains("carried 1 message(s)", entry.Message);
        Assert.Contains("contextBudgetConfigured=True", entry.Message);
        Assert.Contains("windowTokens=8000", entry.Message);
        Assert.Contains("maxOutputTokens=2000", entry.Message);

        // 4. BEHAVIOUR UNCHANGED — the promise of this item, and therefore a GUARD, not red-green evidence:
        //    every assertion in this block already passes on the pre-change source. Two provider calls (the
        //    rejected one and the tool-disabled retry), the retry carries no tools, the turn still completes.
        Assert.Equal(2, run.OptionsPerCall.Count);
        Assert.Single(run.OptionsPerCall[0]!.Tools!);
        Assert.Null(run.OptionsPerCall[1]!.Tools);
        Assert.Contains(run.Items, i => i is TextDelta { Text: "final answer" });
        Assert.Single(run.Items.OfType<Finished>());
    }

    [Fact]
    public async Task NonStreamingOverflow_NamesTheRealCauseFirstToo_AndStillRetriesWithoutTools()
    {
        // Site parity: the catch body is duplicated per provider path, and both executors funnel through this
        // one method — the Headless chain (HeadlessTurnExecutor -> BackgroundAssistantTurnRunner) and the Live
        // path (ChatSession.RunModelExchangeAsync). So the non-streaming body has to carry the same call in
        // the same position, or half the fleet keeps the wrong diagnosis.
        var run = await RunAsync(
            streaming: false,
            firstCallError: new HttpRequestException(OpenAiOverflow, null, HttpStatusCode.BadRequest));

        var overflow = run.Logger.IndexOf("Context overflow");
        var toolLine = run.Logger.IndexOf("retrying without tools");
        Assert.True(overflow >= 0, "the non-streaming path did not log the context-overflow diagnosis");
        Assert.True(toolLine >= 0, "the pre-existing tool-disabled retry line disappeared");
        Assert.True(overflow < toolLine, "the non-streaming path logged the wrong diagnosis first");

        // Guard block again — green before and after.
        Assert.Equal(2, run.OptionsPerCall.Count);
        Assert.Single(run.OptionsPerCall[0]!.Tools!);
        Assert.Null(run.OptionsPerCall[1]!.Tools);
        Assert.Contains(run.Items, i => i is TextDelta { Text: "final answer" });
    }

    [Fact]
    public async Task UnrecognisedBadRequest_LogsNoOverflowLine_AndBehavesExactlyAsBefore()
    {
        // The substring list WILL miss provider phrasings. When it does, the change degrades to exactly the
        // old behaviour — the tool-disabled retry, and only the old warning — never to worse.
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

    /// <summary>
    /// Drives the real service with tools enabled: the first provider call throws
    /// <paramref name="firstCallError"/>, the tool-disabled retry answers with text. Records the
    /// <see cref="ChatOptions"/> of every call so the retry can be inspected, and captures every log line.
    /// </summary>
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
                Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(chatClient));
        // A FRESH ChatOptions per call: the retry must be observable as "no tools", which only works if the
        // handler does not hand back the same instance the first call had its tools written into.
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

    /// <summary>Captures every line, in order — the order is what this file asserts.</summary>
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