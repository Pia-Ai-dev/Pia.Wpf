using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Services.Plugins;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// Exercises the relocated run loop's state machine. The loop is UI-thread-affine
/// in production; here it runs synchronously on the test thread (no Task.Run), which
/// is exactly how its continuations behave.
/// </summary>
public class ChatSessionStateMachineTests
{
    private readonly IAiClientService _ai = Substitute.For<IAiClientService>();
    private readonly IPluginService _plugins = Substitute.For<IPluginService>();
    private readonly IActionCardBuilder _cards = Substitute.For<IActionCardBuilder>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly ITokenMapService _tokenMap = Substitute.For<ITokenMapService>();

    public ChatSessionStateMachineTests()
    {
        // Loc returns the key as its own value so empty-response/error text is non-null.
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        _loc.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => (string)ci[0]);
    }

    // isActive => true: a standalone session (no manager) is treated as foreground,
    // so a successful turn settles to Idle (matches today's single-active behavior).
    private ChatSession CreateSession() => new(
        _tokenMap, _ai, _plugins, _cards, _loc, NullLogger.Instance, _ => true);

    // isActive => false: a backgrounded session — a successful turn settles to Completed.
    private ChatSession CreateBackgroundSession() => new(
        _tokenMap, _ai, _plugins, _cards, _loc, NullLogger.Instance, _ => false);

    private static ChatTurnRequest BuildRequest(ChatSession session, string userText = "hi")
    {
        var user = new AssistantMessage(ChatRole.User, userText);
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

    private static async IAsyncEnumerable<ChatStreamItem> Stream(
        params ChatStreamItem[] items)
    {
        foreach (var item in items)
        {
            yield return item;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<ChatStreamItem> ThrowingStream(
        Exception ex, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        throw ex;
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    [Fact]
    public async Task StreamingText_EndsInIdle_WithContent()
    {
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<Func<FunctionCallContent, Task<object?>>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Stream(new TextDelta("Hello "), new TextDelta("world")));

        var session = CreateSession();
        var states = new List<ChatState>();
        session.StateChanged += (_, e) => states.Add(e.NewState);

        await session.RunTurnAsync(BuildRequest(session), CancellationToken.None);

        Assert.Equal(ChatState.Idle, session.State);
        Assert.False(session.IsStreaming);
        Assert.Contains(ChatState.Running, states);
        Assert.Equal("Hello world", session.Messages.Last(m => !m.IsUser).Content);
    }

    [Fact]
    public async Task BackgroundSession_StreamingContent_EndsInCompleted()
    {
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<Func<FunctionCallContent, Task<object?>>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Stream(new TextDelta("Done in background")));

        var session = CreateBackgroundSession();

        await session.RunTurnAsync(BuildRequest(session), CancellationToken.None);

        // A backgrounded turn that produced content ends Completed (unread result).
        Assert.Equal(ChatState.Completed, session.State);
        Assert.False(session.IsStreaming);
    }

    [Fact]
    public async Task HandledException_EndsInError()
    {
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<Func<FunctionCallContent, Task<object?>>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => ThrowingStream(new InvalidOperationException("boom")));

        var session = CreateSession();
        RunFailedEventArgs? failure = null;
        session.RunFailed += (_, e) => failure = e;

        await session.RunTurnAsync(BuildRequest(session), CancellationToken.None);

        Assert.Equal(ChatState.Error, session.State);
        Assert.False(session.IsStreaming);
        Assert.NotNull(failure);
    }

    [Fact]
    public async Task Cancellation_EndsInIdle_NotError()
    {
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<Func<FunctionCallContent, Task<object?>>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => ThrowingStream(new OperationCanceledException()));

        var session = CreateSession();

        await session.RunTurnAsync(BuildRequest(session), CancellationToken.None);

        Assert.Equal(ChatState.Idle, session.State);
        Assert.False(session.IsStreaming);
    }

    [Fact]
    public async Task Cancel_DuringSetupWindow_AbortsTurn_NoAiCall_NoEmptyResponse()
    {
        // Mirrors a Cancel click while the manager resolves settings/persona/provider:
        // BeginTurn() has created the per-turn CTS, the user cancels, then the run starts.
        // The cancel must be honored (C1) and must NOT also report an empty response.
        var session = CreateSession();
        var request = BuildRequest(session);
        var failures = new List<RunFailureKind>();
        session.RunFailed += (_, e) => failures.Add(e.Kind);

        session.BeginTurn(); // CTS now live (as StartTurnAsync does before its setup awaits)
        session.Cancel();    // the cancel lands on the live CTS instead of being lost

        await session.RunTurnAsync(request, CancellationToken.None);

        Assert.Equal(ChatState.Idle, session.State); // cancelled → Idle, not Error
        Assert.False(session.IsStreaming);
        // The AI client is never called because the token was already cancelled at entry.
        _ai.DidNotReceive().GetChatCompletionWithToolsAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
            Arg.Any<Func<FunctionCallContent, Task<object?>>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        // No empty-response double-report over the Cancelled snackbar, and the bubble is
        // not overwritten with empty-response text.
        Assert.DoesNotContain(RunFailureKind.Empty, failures);
        Assert.NotEqual("Msg_Assistant_EmptyResponse", session.Messages.Last(m => !m.IsUser).Content);
    }

    [Fact]
    public async Task ActionCard_TransitionsThroughWaitingForTool()
    {
        var pending = new PluginToolCall(
            ToolName: "create_todo",
            PluginId: BuiltInPluginDefaults.TodoPluginId,
            PluginName: "todo",
            Description: "Create a todo",
            Details: null,
            Execute: () => Task.FromResult<object?>("done"));

        _plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?)null, (PluginToolCall?)pending));

        var card = new ActionCardInfo
        {
            Title = "Create a todo",
            Summary = "Create a todo",
            Category = ActionCardCategory.Todo,
            ToolName = "create_todo",
        };
        _cards.Build(Arg.Any<PluginToolCall>(), Arg.Any<bool>(), Arg.Any<bool>()).Returns(card);
        _cards.ResolveStatusText(Arg.Any<string>()).Returns("running");
        _cards.ResolveSuccessTitle(Arg.Any<string>()).Returns("Saved");

        // Stream invokes the tool handler, then yields a closing text delta.
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<Func<FunctionCallContent, Task<object?>>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var handler = ci.ArgAt<Func<FunctionCallContent, Task<object?>>?>(3);
                return StreamWithToolCall(handler);
            });

        var session = CreateSession();
        var request = BuildRequest(session);
        // SupportsTools must be true for the handler to be wired.
        request = new ChatTurnRequest
        {
            UserMessage = request.UserMessage,
            AssistantMessage = request.AssistantMessage,
            Provider = request.Provider,
            TurnSetup = new AssistantTurnSetup("system", new List<AITool>(), SupportsTools: true, WebSearchActive: false),
            AtCommands = [],
            TokenizationEnabled = false,
        };

        var states = new List<ChatState>();
        session.StateChanged += (_, e) => states.Add(e.NewState);

        // Accept the card shortly after it appears.
        session.Messages.CollectionChanged += (_, _) => { };
        var run = session.RunTurnAsync(request, CancellationToken.None);

        // Spin until the card is pending, then accept it.
        await WaitUntilAsync(() => card.IsPending);
        Assert.Equal(ChatState.WaitingForTool, session.State);
        card.AllowOnceCommand.Execute(null);

        await run;

        Assert.Equal(ChatState.Idle, session.State);
        Assert.Contains(ChatState.WaitingForTool, states);
        // WaitingForTool then back to Running before the terminal Idle.
        var waitIdx = states.IndexOf(ChatState.WaitingForTool);
        Assert.Contains(ChatState.Running, states.Skip(waitIdx + 1));
    }

    private static async IAsyncEnumerable<ChatStreamItem> StreamWithToolCall(
        Func<FunctionCallContent, Task<object?>>? handler)
    {
        if (handler is not null)
        {
            var call = new FunctionCallContent("call-1", "create_todo", new Dictionary<string, object?>());
            await handler(call);
        }
        yield return new TextDelta("Saved your todo.");
        await Task.Yield();
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs = 5000)
    {
        var start = Environment.TickCount64;
        while (!predicate())
        {
            if (Environment.TickCount64 - start > timeoutMs)
                throw new TimeoutException("Condition not met within timeout.");
            await Task.Delay(10);
        }
    }
}
