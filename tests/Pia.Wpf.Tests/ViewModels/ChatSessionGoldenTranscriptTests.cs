using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Exceptions;
using Pia.Services.Interfaces;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// Interactive single-turn regression (§16 R11): the Chat-mode <see cref="ChatSession.RunTurnAsync"/>
/// path must be byte-for-byte behavior-preserving after the <c>RunModelExchangeAsync</c> extraction.
/// These frozen characterization snapshots pin the final transcript / state / streaming / TurnCompleted
/// / RunFailed for the canonical scenarios; a regression in the extraction breaks them.
/// </summary>
public sealed class ChatSessionGoldenTranscriptTests
{
    private readonly IAiClientService _ai = Substitute.For<IAiClientService>();
    private readonly IPluginService _plugins = Substitute.For<IPluginService>();
    private readonly IActionCardBuilder _cards = Substitute.For<IActionCardBuilder>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly ITokenMapService _tokenMap = Substitute.For<ITokenMapService>();
    private readonly IToolPermissionService _permissions = Substitute.For<IToolPermissionService>();

    public ChatSessionGoldenTranscriptTests()
    {
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        _loc.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => (string)ci[0]);
    }

    private ChatSession CreateSession(bool active = true) => new(
        _tokenMap, _ai, _plugins, _cards, _permissions, _loc, NullLogger.Instance, _ => active);

    private static ChatTurnRequest BuildRequest(ChatSession session, bool supportsTools = false, bool webSearchActive = false, bool tokenizationEnabled = false)
    {
        var user = new AssistantMessage(ChatRole.User, "hi");
        var assistant = new AssistantMessage(ChatRole.Assistant) { IsStreaming = true };
        session.Messages.Add(user);
        session.Messages.Add(assistant);
        return new ChatTurnRequest
        {
            UserMessage = user,
            AssistantMessage = assistant,
            Provider = new AiProvider { Name = "Test", Endpoint = "http://localhost", ProviderType = AiProviderType.OpenAI },
            TurnSetup = new AssistantTurnSetup("system", null, SupportsTools: supportsTools, WebSearchActive: webSearchActive),
            AtCommands = [],
            TokenizationEnabled = tokenizationEnabled,
        };
    }

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

    /// <summary>
    /// Sets up the AI stream to first drive the passed-in tool handler (the
    /// <c>supportsTools ? HandleToolCallWithStatus : null</c> wiring inside RunModelExchangeAsync)
    /// with one <see cref="FunctionCallContent"/> round, then stream the final answer.
    /// </summary>
    private void ReturnsToolThenText(string toolName, string finalText)
    {
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<Func<FunctionCallContent, Task<object?>>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var handler = ci.ArgAt<Func<FunctionCallContent, Task<object?>>?>(3);
                return ToolThenTextStream(handler, toolName, finalText);
            });
    }

    private static async IAsyncEnumerable<ChatStreamItem> ToolThenTextStream(
        Func<FunctionCallContent, Task<object?>>? handler, string toolName, string finalText)
    {
        if (handler is not null)
            await handler(new FunctionCallContent("call-1", toolName, new Dictionary<string, object?>()));
        yield return new TextDelta(finalText);
        yield return new Finished(null, "m");
        await Task.Yield();
    }

    private static async IAsyncEnumerable<ChatStreamItem> ThrowingStream(Exception ex, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        throw ex;
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    [Fact]
    public async Task PlainText_SettlesIdle_ContentAndTurnCompleted()
    {
        ReturnsStream(() => Stream(new TextDelta("Hello "), new TextDelta("world"), new Finished(null, "m")));
        var session = CreateSession();
        var succeeded = (bool?)null;
        session.TurnCompleted += (_, e) => succeeded = e.Succeeded;

        await session.RunTurnAsync(BuildRequest(session), CancellationToken.None);

        Assert.Equal("Hello world", session.Messages.Last(m => !m.IsUser).Content);
        Assert.Equal(ChatState.Idle, session.State);
        Assert.False(session.IsStreaming);
        Assert.True(succeeded);
        Assert.Null(session.Cts);
    }

    [Fact]
    public async Task ReasoningPlusThinkTags_MergeIntoThinkingContent()
    {
        ReturnsStream(() => Stream(
            new ReasoningDelta("planning"),
            new TextDelta("<think>more</think>answer"),
            new Finished(null, "m")));
        var session = CreateSession();

        await session.RunTurnAsync(BuildRequest(session), CancellationToken.None);

        var msg = session.Messages.Last(m => !m.IsUser);
        Assert.Equal("answer", msg.Content);
        Assert.Contains("planning", msg.ThinkingContent);
        Assert.Contains("more", msg.ThinkingContent);
    }

    [Fact]
    public async Task EmptyResponse_SynthesizesPlaceholder_RaisesEmptyRunFailed_TurnCompletedFalse()
    {
        ReturnsStream(() => Stream(new Finished(null, "m")));
        var session = CreateSession();
        RunFailedEventArgs? failure = null;
        session.RunFailed += (_, e) => failure = e;
        bool? succeeded = null;
        session.TurnCompleted += (_, e) => succeeded = e.Succeeded;

        await session.RunTurnAsync(BuildRequest(session), CancellationToken.None);

        Assert.Equal("Msg_Assistant_EmptyResponse", session.Messages.Last(m => !m.IsUser).Content);
        Assert.Equal(ChatState.Idle, session.State);
        Assert.NotNull(failure);
        Assert.Equal(RunFailureKind.Empty, failure!.Kind);
        Assert.False(succeeded);
    }

    [Fact]
    public async Task CancelMidStream_SettlesIdle_RaisesCancelledRunFailed()
    {
        ReturnsStream(() => ThrowingStream(new OperationCanceledException("cancel")));
        var session = CreateSession();
        RunFailedEventArgs? failure = null;
        session.RunFailed += (_, e) => failure = e;

        await session.RunTurnAsync(BuildRequest(session), CancellationToken.None);

        Assert.Equal(ChatState.Idle, session.State);
        Assert.False(session.IsStreaming);
        Assert.NotNull(failure);
        Assert.Null(session.Cts);
    }

    [Fact]
    public async Task Timeout_SettlesError_RaisesTimeoutRunFailed()
    {
        ReturnsStream(() => ThrowingStream(new LlmTimeoutException("Test", 30)));
        var session = CreateSession();
        RunFailedEventArgs? failure = null;
        session.RunFailed += (_, e) => failure = e;

        await session.RunTurnAsync(BuildRequest(session), CancellationToken.None);

        Assert.Equal(ChatState.Error, session.State);
        Assert.False(session.IsStreaming);
        Assert.NotNull(failure);
        Assert.Equal(RunFailureKind.Timeout, failure!.Kind);
    }

    [Fact]
    public async Task Truncated_AppendsNotice_SettlesError()
    {
        ReturnsStream(() => ThrowingStream(new LlmTruncatedException("Test", 5)));
        var session = CreateSession();
        RunFailedEventArgs? failure = null;
        session.RunFailed += (_, e) => failure = e;

        await session.RunTurnAsync(BuildRequest(session), CancellationToken.None);

        Assert.Equal(ChatState.Error, session.State);
        Assert.Equal(RunFailureKind.Truncated, failure!.Kind);
    }

    [Fact]
    public async Task BackgroundSession_ProducesContent_SettlesCompleted()
    {
        ReturnsStream(() => Stream(new TextDelta("done"), new Finished(null, "m")));
        var session = CreateSession(active: false);

        await session.RunTurnAsync(BuildRequest(session), CancellationToken.None);

        Assert.Equal(ChatState.Completed, session.State);
        Assert.Equal("done", session.Messages.Last(m => !m.IsUser).Content);
    }

    /// <summary>
    /// R11/R4 golden: SupportsTools=true wires the tool handler into the extracted
    /// RunModelExchangeAsync. A tool round must invoke the handler + run the status path, then the
    /// final content still settles Idle — a regression that dropped the handler would break this.
    /// </summary>
    [Fact]
    public async Task SupportsTools_ToolRound_InvokesHandler_StatusPathRuns_SettlesIdle()
    {
        _cards.ResolveStatusText(Arg.Any<string>()).Returns("Reading file…");
        ReturnsToolThenText("read_file", "final answer");
        var session = CreateSession();

        await session.RunTurnAsync(BuildRequest(session, supportsTools: true), CancellationToken.None);

        // The tool handler ran (RunModelExchangeAsync wired supportsTools → HandleToolCallWithStatus).
        await _plugins.Received().RouteToolCallAsync(
            Arg.Is<FunctionCallContent>(c => c.Name == "read_file"), Arg.Any<CancellationToken>());
        _cards.Received().ResolveStatusText("read_file"); // status/action-card path executed
        var msg = session.Messages.Last(m => !m.IsUser);
        Assert.Equal("final answer", msg.Content);
        Assert.Equal(ChatState.Idle, session.State);
        Assert.False(session.IsStreaming);
    }

    /// <summary>
    /// R11/R4 golden: WebSearchActive=true runs the ApplyWebCitations post-process moved into
    /// RunModelExchangeAsync. Citations must be extracted from the final content into Sources and the
    /// raw URL rewritten to a marker — a regression that dropped the post-process would break this.
    /// </summary>
    [Fact]
    public async Task WebSearchActive_ExtractsCitations_FromFinalContent()
    {
        ReturnsStream(() => Stream(
            new TextDelta("See [Example](https://example.com/page) for details."),
            new Finished(null, "m")));
        var session = CreateSession();

        await session.RunTurnAsync(BuildRequest(session, webSearchActive: true), CancellationToken.None);

        var msg = session.Messages.Last(m => !m.IsUser);
        Assert.Single(msg.Sources); // ApplyWebCitations ran on the final content
        Assert.Contains("example.com", msg.Sources[0].Url);
        Assert.DoesNotContain("https://example.com/page", msg.Content); // link rewritten to a citation marker
        Assert.Equal(ChatState.Idle, session.State);
    }

    /// <summary>
    /// R11/R4 golden: with TokenizationEnabled=true the extracted <c>CleanupPerExchange</c> must still run
    /// its safety-net PII detokenization on the final content and restore the ambient token-map/task
    /// context. The other golden cases run tokenization off, so this is the only guard on that leg — a
    /// regression that dropped it would leak tokenized PII into the (syncing) transcript or leave a stale
    /// ambient bleeding into the next turn.
    /// </summary>
    [Fact]
    public async Task TokenizationEnabled_SafetyNetDetokenizes_AndRestoresAmbients()
    {
        // The AI substitute is the RAW client (not the tokenizing decorator), so no mid-stream
        // detokenization happens — the ONLY detokenize is the finally-time safety net in CleanupPerExchange.
        _tokenMap.Detokenize(Arg.Any<string>()).Returns(ci => ((string)ci[0]).Replace("<PII_1>", "Alice"));
        ReturnsStream(() => Stream(new TextDelta("Hello <PII_1>"), new Finished(null, "m")));
        var session = CreateSession();

        // Ambients are clean before the turn; RunTurnAsync sets them and CleanupPerExchange must restore them.
        var ambientBefore = TokenMapAmbient.Current;
        var taskBefore = TaskAmbient.Current;
        Assert.Null(ambientBefore);
        Assert.Null(taskBefore);

        await session.RunTurnAsync(BuildRequest(session, tokenizationEnabled: true), CancellationToken.None);

        var msg = session.Messages.Last(m => !m.IsUser);
        Assert.Equal("Hello Alice", msg.Content);            // safety-net detokenize ran on the final content
        _tokenMap.Received().Detokenize(Arg.Any<string>());
        Assert.Equal(ambientBefore, TokenMapAmbient.Current); // token-map ambient restored (no leak)
        Assert.Equal(taskBefore, TaskAmbient.Current);        // task ambient restored (no leak)
        Assert.Equal(ChatState.Idle, session.State);
        Assert.False(session.IsStreaming);
        Assert.Null(session.Cts);
    }
}
