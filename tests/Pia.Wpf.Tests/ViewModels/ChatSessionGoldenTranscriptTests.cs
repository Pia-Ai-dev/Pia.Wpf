using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
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

    private static ChatTurnRequest BuildRequest(ChatSession session)
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
            TurnSetup = new AssistantTurnSetup("system", null, SupportsTools: false, WebSearchActive: false),
            AtCommands = [],
            TokenizationEnabled = false,
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
}
