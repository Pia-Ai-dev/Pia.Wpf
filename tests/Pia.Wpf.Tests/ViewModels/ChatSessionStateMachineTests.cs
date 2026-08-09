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

// The loop is UI-thread-affine in production; here it runs synchronously on the test thread (no Task.Run), which is
// exactly how its continuations behave.
public class ChatSessionStateMachineTests
{
    private readonly IAiClientService _ai = Substitute.For<IAiClientService>();
    private readonly IPluginService _plugins = Substitute.For<IPluginService>();
    private readonly IActionCardBuilder _cards = Substitute.For<IActionCardBuilder>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly ITokenMapService _tokenMap = Substitute.For<ITokenMapService>();
    private readonly IToolPermissionService _permissions = Substitute.For<IToolPermissionService>();

    public ChatSessionStateMachineTests()
    {
        // Loc returns the key as its own value so empty-response/error text is non-null.
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        _loc.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => (string)ci[0]);
    }

    // isActive => true: a standalone session (no manager) is treated as foreground,
    // so a successful turn settles to Idle (matches today's single-active behavior).
    private ChatSession CreateSession() => new(
        _tokenMap, _ai, _plugins, _cards, _permissions, _loc, NullLogger.Instance, _ => true);

    // isActive => false: a backgrounded session — a successful turn settles to Completed.
    private ChatSession CreateBackgroundSession() => new(
        _tokenMap, _ai, _plugins, _cards, _permissions, _loc, NullLogger.Instance, _ => false);

    [Fact]
    public void SetActiveRun_RaisesOnChange_AndIsIdempotent()
    {
        var session = CreateSession();
        var raised = new List<Guid?>();
        session.ActiveRunChanged += (_, id) => raised.Add(id);

        var runId = Guid.NewGuid();
        session.SetActiveRun(runId);
        session.SetActiveRun(runId);   // no-op — unchanged
        session.SetActiveRun(null);

        Assert.Equal(new Guid?[] { runId, null }, raised);
        Assert.Null(session.ActiveRunId);
    }

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
        Exception ex, Action? beforeThrow = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        beforeThrow?.Invoke();
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
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
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
    public async Task ReasoningDelta_PopulatesThinkingContent_SeparateFromVisibleText()
    {
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(_ => Stream(
                new ReasoningDelta("I should greet "),
                new ReasoningDelta("the user."),
                new TextDelta("Hello!")));

        var session = CreateSession();
        await session.RunTurnAsync(BuildRequest(session), CancellationToken.None);

        var message = session.Messages.Last(m => !m.IsUser);
        Assert.Equal("Hello!", message.Content);
        Assert.Equal("I should greet the user.", message.ThinkingContent);
        Assert.True(message.HasThinkingContent);
    }

    [Fact]
    public async Task NoReasoning_TurnStillTimesThinkingPhase()
    {
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(_ => Stream(new TextDelta("Hello!")));

        var session = CreateSession();
        await session.RunTurnAsync(BuildRequest(session), CancellationToken.None);

        // The live indicator was up for the whole exchange, so the retrospective
        // chip must show even though the provider forwarded no reasoning trace.
        var message = session.Messages.Last(m => !m.IsUser);
        Assert.False(message.HasThinkingContent);
        Assert.True(message.HasReasoningDuration);
        Assert.True(message.ShowReasoningSummary);
    }

    [Fact]
    public async Task InlineThinkTags_PopulateThinkingContent_AndAreStrippedFromVisibleText()
    {
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(_ => Stream(new TextDelta("<think>weighing options</think>"), new TextDelta("Answer")));

        var session = CreateSession();
        await session.RunTurnAsync(BuildRequest(session), CancellationToken.None);

        var message = session.Messages.Last(m => !m.IsUser);
        Assert.Equal("Answer", message.Content);
        Assert.Equal("weighing options", message.ThinkingContent);
    }

    [Fact]
    public async Task BackgroundSession_StreamingContent_EndsInCompleted()
    {
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(_ => Stream(new TextDelta("Done in background")));

        var session = CreateBackgroundSession();

        await session.RunTurnAsync(BuildRequest(session), CancellationToken.None);

        Assert.Equal(ChatState.Completed, session.State);
        Assert.False(session.IsStreaming);
    }

    [Fact]
    public async Task HandledException_EndsInError()
    {
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
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
        var session = CreateSession();
        // Models a user stop: the session CTS fires mid-stream (the Stop button's Cancel()), then the
        // exchange aborts — only a FIRED token classifies the OCE as a cancel, not a transport fault.
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(_ => ThrowingStream(new OperationCanceledException(), () => session.Cancel()));

        await session.RunTurnAsync(BuildRequest(session), CancellationToken.None);

        Assert.Equal(ChatState.Idle, session.State);
        Assert.False(session.IsStreaming);
    }

    [Fact]
    public async Task Cancel_DuringSetupWindow_AbortsTurn_NoAiCall_NoEmptyResponse()
    {
        // A Cancel click while the manager is still resolving settings: BeginTurn has created the per-turn CTS, the
        // user cancels, then the run starts. It must be honoured without also reporting an empty response.
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
            Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>());
        // No empty-response double-report over the Cancelled snackbar, and the bubble is
        // not overwritten with empty-response text.
        Assert.DoesNotContain(RunFailureKind.Empty, failures);
        Assert.NotEqual("Msg_Assistant_EmptyResponse", session.Messages.Last(m => !m.IsUser).Content);
    }

    [Fact]
    public async Task AtFiles_InjectedFileContent_LandsInUserMessage_AndCommandIsStripped()
    {
        // The session must inline the injected file into the AI-visible user turn, so a model that will not call
        // read_file still sees it, while the persisted message keeps the original @Files token.
        IList<ChatMessage>? captured = null;
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci => { captured = (IList<ChatMessage>)ci[0]; return Stream(new TextDelta("ok")); });

        var session = CreateSession();
        var user = new AssistantMessage(ChatRole.User, "@Files:\"test.ps1\" what does this script do?");
        var assistant = new AssistantMessage(ChatRole.Assistant) { IsStreaming = true };
        session.Messages.Add(user);
        session.Messages.Add(assistant);

        var injected = "<attached_file path=\"test.ps1\" total_lines=\"1\">\nWrite-Host \"hi\"\n</attached_file>";
        var request = new ChatTurnRequest
        {
            UserMessage = user,
            AssistantMessage = assistant,
            Provider = new AiProvider { Name = "Test", Endpoint = "http://localhost", ProviderType = AiProviderType.OpenAI },
            TurnSetup = new AssistantTurnSetup("system", null, SupportsTools: true, WebSearchActive: false),
            AtCommands = [new AtCommand { Domain = AtCommandDomain.Files, ItemTitle = "test.ps1" }],
            InjectedFileContext = injected,
            TokenizationEnabled = false,
        };

        await session.RunTurnAsync(request, CancellationToken.None);

        Assert.NotNull(captured);
        var text = captured!.Single(m => m.Role == ChatRole.User).Text;
        Assert.Contains("what does this script do?", text); // the question survived stripping
        Assert.Contains("Write-Host \"hi\"", text);          // the file content is now present
        Assert.DoesNotContain("@Files", text);                // the command token was stripped out
        // Injection is ephemeral: the stored/displayed user message is untouched.
        Assert.Contains("@Files", user.Content!);
    }

    [Fact]
    public async Task AtFiles_TrailingCommand_LeavesTheSentenceWithItsObject()
    {
        // Deleting the token left a message ending mid-phrase, which the model read back as a
        // truncated request instead of an instruction about the file.
        IList<ChatMessage>? captured = null;
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci => { captured = (IList<ChatMessage>)ci[0]; return Stream(new TextDelta("ok")); });

        var session = CreateSession();
        var user = new AssistantMessage(ChatRole.User, "please check if we already implemented the @Files:\"plan.md\"");
        var assistant = new AssistantMessage(ChatRole.Assistant) { IsStreaming = true };
        session.Messages.Add(user);
        session.Messages.Add(assistant);

        var request = new ChatTurnRequest
        {
            UserMessage = user,
            AssistantMessage = assistant,
            Provider = new AiProvider { Name = "Test", Endpoint = "http://localhost", ProviderType = AiProviderType.OpenAI },
            TurnSetup = new AssistantTurnSetup("system", null, SupportsTools: true, WebSearchActive: false),
            AtCommands = [new AtCommand { Domain = AtCommandDomain.Files, ItemTitle = "plan.md" }],
            InjectedFileContext = "<attached_file path=\"plan.md\" total_lines=\"1\">\nplan\n</attached_file>",
            TokenizationEnabled = false,
        };

        await session.RunTurnAsync(request, CancellationToken.None);

        Assert.NotNull(captured);
        var text = captured!.Single(m => m.Role == ChatRole.User).Text;
        Assert.StartsWith("please check if we already implemented the plan.md", text);
        Assert.DoesNotContain("@Files", text);
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
        _cards.Build(Arg.Any<PluginToolCall>(), Arg.Any<bool>(), Arg.Any<ToolGateDecision?>(), Arg.Any<ToolClass?>()).Returns(card);
        _cards.ResolveStatusText(Arg.Any<string>()).Returns("running");
        _cards.ResolveSuccessTitle(Arg.Any<string>()).Returns("Saved");

        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var handler = ci.ArgAt<ToolCallHandler?>(3);
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

        session.Messages.CollectionChanged += (_, _) => { };
        var run = session.RunTurnAsync(request, CancellationToken.None);

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

    [Fact]
    public async Task AlwaysAllow_OnEligibleTool_PersistsGrant_AndExecutes()
    {
        var executed = false;
        var pending = new PluginToolCall(
            ToolName: "create_todo",
            PluginId: BuiltInPluginDefaults.TodoPluginId,
            PluginName: "todo",
            Description: "Create a todo",
            Details: null,
            Execute: () => { executed = true; return Task.FromResult<object?>("done"); });

        _plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?)null, (PluginToolCall?)pending));

        // Eligible, but NOT already granted → user is prompted; they click "Always allow".
        _permissions.IsAutoApproveEligible("create_todo").Returns(true);
        _permissions.IsGranted(BuiltInPluginDefaults.TodoPluginId, "create_todo").Returns(false);

        var card = NewCard("create_todo", BuiltInPluginDefaults.TodoPluginId);
        _cards.Build(Arg.Any<PluginToolCall>(), Arg.Any<bool>(), Arg.Any<ToolGateDecision?>(), Arg.Any<ToolClass?>()).Returns(card);
        _cards.ResolveStatusText(Arg.Any<string>()).Returns("running");
        _cards.ResolveSuccessTitle(Arg.Any<string>()).Returns("Saved");

        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci => StreamWithToolCall(ci.ArgAt<ToolCallHandler?>(3)));

        var session = CreateSession();
        var run = session.RunTurnAsync(ToolRequest(session), CancellationToken.None);

        await WaitUntilAsync(() => card.IsPending);
        card.AlwaysAllowCommand.Execute(null);

        await run;

        await _permissions.Received().GrantAsync(BuiltInPluginDefaults.TodoPluginId, "create_todo");
        Assert.True(executed);
    }

    [Fact]
    public async Task McpTool_Ungranted_IsGated_ShownNotAutoRun_ThenAlwaysAllowPersistsGrant()
    {
        // An interactive MCP call is not auto-run. MCP is grantable as a class, so "Always allow" persists a
        // standing grant — unlike write_file.
        var executed = false;
        var mcpPluginId = Guid.NewGuid();
        var pending = new PluginToolCall(
            ToolName: "mcp_search",
            PluginId: mcpPluginId,
            PluginName: "linear",
            Description: "linear: mcp_search",
            Details: null,
            Execute: () => { executed = true; return Task.FromResult<object?>("done"); });

        _plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?)null, (PluginToolCall?)pending));
        _plugins.IsMcpTool("mcp_search").Returns(true);        // external tool → grantable
        _permissions.IsAutoApproveEligible("mcp_search").Returns(false); // not a built-in
        _permissions.IsGranted(mcpPluginId, "mcp_search").Returns(false); // no standing grant yet

        var card = NewCard("mcp_search", mcpPluginId);
        _cards.Build(Arg.Any<PluginToolCall>(), Arg.Any<bool>(), Arg.Any<ToolGateDecision?>(), Arg.Any<ToolClass?>()).Returns(card);
        _cards.ResolveStatusText(Arg.Any<string>()).Returns("running");
        _cards.ResolveSuccessTitle(Arg.Any<string>()).Returns("Done");

        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci => StreamWithToolCall(ci.ArgAt<ToolCallHandler?>(3)));

        var session = CreateSession();
        var run = session.RunTurnAsync(ToolRequest(session), CancellationToken.None);

        await WaitUntilAsync(() => card.IsPending);
        Assert.False(executed);   // gated — MCP is not auto-run; waiting on the user
        Assert.Equal(ChatState.WaitingForTool, session.State);

        card.AlwaysAllowCommand.Execute(null);
        await run;

        await _permissions.Received().GrantAsync(mcpPluginId, "mcp_search"); // MCP grantable → grant persisted
        Assert.True(executed);
    }

    [Fact]
    public async Task AllowOnce_Executes_ButDoesNotGrant()
    {
        var executed = false;
        var pending = new PluginToolCall(
            ToolName: "create_todo",
            PluginId: BuiltInPluginDefaults.TodoPluginId,
            PluginName: "todo",
            Description: "Create a todo",
            Details: null,
            Execute: () => { executed = true; return Task.FromResult<object?>("done"); });

        _plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?)null, (PluginToolCall?)pending));
        _permissions.IsAutoApproveEligible("create_todo").Returns(true);
        _permissions.IsGranted(BuiltInPluginDefaults.TodoPluginId, "create_todo").Returns(false);

        var card = NewCard("create_todo", BuiltInPluginDefaults.TodoPluginId);
        _cards.Build(Arg.Any<PluginToolCall>(), Arg.Any<bool>(), Arg.Any<ToolGateDecision?>(), Arg.Any<ToolClass?>()).Returns(card);
        _cards.ResolveStatusText(Arg.Any<string>()).Returns("running");
        _cards.ResolveSuccessTitle(Arg.Any<string>()).Returns("Saved");

        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci => StreamWithToolCall(ci.ArgAt<ToolCallHandler?>(3)));

        var session = CreateSession();
        var run = session.RunTurnAsync(ToolRequest(session), CancellationToken.None);

        await WaitUntilAsync(() => card.IsPending);
        card.AllowOnceCommand.Execute(null);

        await run;

        Assert.True(executed);
        await _permissions.DidNotReceive().GrantAsync(Arg.Any<Guid>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Decline_ReturnsDeclineString_AndDoesNotExecute()
    {
        var executed = false;
        object? toolResult = null;
        var pending = new PluginToolCall(
            ToolName: "create_todo",
            PluginId: BuiltInPluginDefaults.TodoPluginId,
            PluginName: "todo",
            Description: "Create a todo",
            Details: null,
            Execute: () => { executed = true; return Task.FromResult<object?>("done"); });

        _plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?)null, (PluginToolCall?)pending));
        _permissions.IsAutoApproveEligible("create_todo").Returns(true);
        _permissions.IsGranted(BuiltInPluginDefaults.TodoPluginId, "create_todo").Returns(false);

        var card = NewCard("create_todo", BuiltInPluginDefaults.TodoPluginId);
        _cards.Build(Arg.Any<PluginToolCall>(), Arg.Any<bool>(), Arg.Any<ToolGateDecision?>(), Arg.Any<ToolClass?>()).Returns(card);
        _cards.ResolveStatusText(Arg.Any<string>()).Returns("running");

        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci => StreamWithToolCallCapture(ci.ArgAt<ToolCallHandler?>(3), r => toolResult = r));

        var session = CreateSession();
        var run = session.RunTurnAsync(ToolRequest(session), CancellationToken.None);

        await WaitUntilAsync(() => card.IsPending);
        card.DeclineCommand.Execute(null);

        await run;

        Assert.False(executed);
        Assert.Equal(
            "User declined the create_todo operation. Do not retry. Ask the user what they would like to do instead.",
            toolResult);
    }

    [Fact]
    public async Task GrantedEligibleTool_AutoApproves_WithoutWaiting_CardAddedBeforeExecute()
    {
        // The ordering is asserted from INSIDE Execute: a post-call assert would pass vacuously.
        AssistantMessage? owningMessage = null;
        var card = NewCard("create_todo", BuiltInPluginDefaults.TodoPluginId);
        card.State = ActionCardState.Accepted; // mock returns the pre-resolved bypass card

        var sawCardResolvedDuringExecute = false;
        var sawWaitingForToolBeforeExecute = false;

        var pending = new PluginToolCall(
            ToolName: "create_todo",
            PluginId: BuiltInPluginDefaults.TodoPluginId,
            PluginName: "todo",
            Description: "Create a todo",
            Details: null,
            Execute: () =>
            {
                sawCardResolvedDuringExecute =
                    owningMessage is not null
                    && owningMessage.ActionCards.Contains(card)
                    && card.State == ActionCardState.Accepted;
                return Task.FromResult<object?>("done");
            });

        _plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?)null, (PluginToolCall?)pending));

        // Eligible AND already granted → bypass: no prompt, no WaitingForTool.
        _permissions.IsAutoApproveEligible("create_todo").Returns(true);
        _permissions.IsGranted(BuiltInPluginDefaults.TodoPluginId, "create_todo").Returns(true);

        // Only the bypass build path returns the resolved card, keyed on the DECISION the gate resolved rather
        // than on a bare `true`.
        _cards.Build(Arg.Any<PluginToolCall>(), Arg.Any<bool>(),
            ToolGateDecision.AutoApprovedStandingGrant, Arg.Any<ToolClass?>()).Returns(card);
        _cards.ResolveStatusText(Arg.Any<string>()).Returns("running");
        _cards.ResolveSuccessTitle(Arg.Any<string>()).Returns("Saved");

        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci => StreamWithToolCall(ci.ArgAt<ToolCallHandler?>(3)));

        var session = CreateSession();
        var request = ToolRequest(session);
        owningMessage = request.AssistantMessage;
        var states = new List<ChatState>();
        session.StateChanged += (_, e) =>
        {
            states.Add(e.NewState);
            if (e.NewState == ChatState.WaitingForTool)
                sawWaitingForToolBeforeExecute = true;
        };

        await session.RunTurnAsync(request, CancellationToken.None);

        Assert.True(sawCardResolvedDuringExecute, "card must be added and resolved before Execute runs");
        Assert.False(sawWaitingForToolBeforeExecute, "a granted bypass must not enter WaitingForTool");
        Assert.DoesNotContain(ChatState.WaitingForTool, states);
        Assert.Contains(card, owningMessage!.ActionCards);
    }

    [Fact]
    public async Task GrantedDestructiveMcpTool_IsNotAutoApproved_StillPrompts()
    {
        // The interactive gate is eligible = IsAutoApproveEligible || (IsMcpTool && !IsDeleteLike); dropping the
        // !IsDeleteLike term would silently auto-run a granted MCP delete in the foreground.
        var executed = false;
        var mcpPluginId = Guid.NewGuid();
        var pending = new PluginToolCall(
            ToolName: "remove_page",
            PluginId: mcpPluginId,
            PluginName: "notion",
            Description: "notion: remove_page",
            Details: null,
            Execute: () => { executed = true; return Task.FromResult<object?>("removed"); });

        _plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?)null, (PluginToolCall?)pending));
        _plugins.IsMcpTool("remove_page").Returns(true);                     // external…
        _permissions.IsAutoApproveEligible("remove_page").Returns(false);    // …not a built-in allowlist tool
        _permissions.IsGranted(mcpPluginId, "remove_page").Returns(true);    // and ALREADY granted

        var card = NewCard("remove_page", mcpPluginId);
        _cards.Build(Arg.Any<PluginToolCall>(), Arg.Any<bool>(), Arg.Any<ToolGateDecision?>(), Arg.Any<ToolClass?>()).Returns(card);
        _cards.ResolveStatusText(Arg.Any<string>()).Returns("running");
        _cards.ResolveSuccessTitle(Arg.Any<string>()).Returns("Done");

        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci => StreamWithToolCall(ci.ArgAt<ToolCallHandler?>(3)));

        var session = CreateSession();
        var states = new List<ChatState>();
        session.StateChanged += (_, e) => states.Add(e.NewState);
        var run = session.RunTurnAsync(ToolRequest(session), CancellationToken.None);

        await WaitUntilAsync(() => card.IsPending);
        Assert.False(executed);                                   // the grant did NOT bypass the gate
        Assert.Equal(ChatState.WaitingForTool, session.State);    // the user is being asked
        Assert.Contains(ChatState.WaitingForTool, states);

        card.DeclineCommand.Execute(null);
        await run;

        Assert.False(executed);
        await _permissions.DidNotReceive().GrantAsync(Arg.Any<Guid>(), Arg.Any<string>());
    }

    [Fact]
    public async Task ForgedGrant_OnIneligibleTool_StillPrompts_AndDoesNotGrant()
    {
        var executed = false;
        var pending = new PluginToolCall(
            ToolName: "write_file",
            PluginId: BuiltInPluginDefaults.FilesPluginId,
            PluginName: "files",
            Description: "Write a file",
            Details: null,
            Execute: () => { executed = true; return Task.FromResult<object?>("done"); });

        _plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?)null, (PluginToolCall?)pending));

        // Forged/stale grant: IsGranted is true, but the tool is NOT eligible. The gate
        // must re-check eligibility and refuse to auto-bypass.
        _permissions.IsAutoApproveEligible("write_file").Returns(false);
        _permissions.IsGranted(BuiltInPluginDefaults.FilesPluginId, "write_file").Returns(true);

        var card = NewCard("write_file", BuiltInPluginDefaults.FilesPluginId);
        _cards.Build(Arg.Any<PluginToolCall>(), Arg.Any<bool>(), Arg.Any<ToolGateDecision?>(), Arg.Any<ToolClass?>()).Returns(card);
        _cards.ResolveStatusText(Arg.Any<string>()).Returns("running");
        _cards.ResolveSuccessTitle(Arg.Any<string>()).Returns("Saved");

        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci => StreamWithToolCall(ci.ArgAt<ToolCallHandler?>(3)));

        var session = CreateSession();
        var states = new List<ChatState>();
        session.StateChanged += (_, e) => states.Add(e.NewState);

        var request = ToolRequest(session);
        var run = session.RunTurnAsync(request, CancellationToken.None);

        await WaitUntilAsync(() => card.IsPending);
        Assert.Equal(ChatState.WaitingForTool, session.State);
        Assert.Contains(ChatState.WaitingForTool, states);

        // Even clicking "Always allow" on an ineligible tool degrades to AllowOnce (no grant).
        card.AlwaysAllowCommand.Execute(null);
        await run;

        Assert.True(executed); // degraded to AllowOnce: executed once
        await _permissions.DidNotReceive().GrantAsync(Arg.Any<Guid>(), Arg.Any<string>());
    }

    // The guard fails CLOSED (treat as external), so a delete-like tool is still not auto-approved.
    [Fact]
    public async Task WhenMcpDerivationThrows_TheTurnSurvives_AndTheToolIsTreatedAsExternal()
    {
        var executed = false;
        var pluginId = BuiltInPluginDefaults.FilesPluginId;
        var pending = new PluginToolCall(
            ToolName: "delete_file",
            PluginId: pluginId,
            PluginName: "files",
            Description: "Delete a file",
            Details: null,
            Execute: () => { executed = true; return Task.FromResult<object?>("done"); });

        _plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?)null, (PluginToolCall?)pending));
        _plugins.IsMcpTool("delete_file").Returns(_ => throw new InvalidOperationException("route table exploded"));

        // Even a (forged) standing grant must not auto-run it: fail-closed makes the class External, and a
        // delete-like external tool is never auto-approved.
        _permissions.IsAutoApproveEligible("delete_file").Returns(false);
        _permissions.IsGranted(pluginId, "delete_file").Returns(true);

        var card = NewCard("delete_file", pluginId);
        _cards.Build(Arg.Any<PluginToolCall>(), Arg.Any<bool>(), Arg.Any<ToolGateDecision?>(), Arg.Any<ToolClass?>()).Returns(card);
        _cards.ResolveStatusText(Arg.Any<string>()).Returns("running");
        _cards.ResolveSuccessTitle(Arg.Any<string>()).Returns("Done");

        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci => StreamWithToolCall(ci.ArgAt<ToolCallHandler?>(3)));

        var session = CreateSession();
        var run = session.RunTurnAsync(ToolRequest(session), CancellationToken.None);

        await WaitUntilAsync(() => card.IsPending);
        Assert.False(executed);                                   // not auto-approved
        Assert.Equal(ChatState.WaitingForTool, session.State);

        card.AlwaysAllowCommand.Execute(null);
        await run;

        // The turn completed instead of throwing out of the tool loop.
        Assert.NotEqual(ChatState.Error, session.State);
        Assert.True(executed);                                    // degraded to AllowOnce
        await _permissions.DidNotReceive().GrantAsync(Arg.Any<Guid>(), Arg.Any<string>());
    }

    private static ActionCardInfo NewCard(string toolName, Guid pluginId) => new()
    {
        Title = toolName,
        Summary = toolName,
        Category = ActionCardCategory.Todo,
        ToolName = toolName,
        PluginId = pluginId,
    };

    private ChatTurnRequest ToolRequest(ChatSession session)
    {
        var request = BuildRequest(session);
        return new ChatTurnRequest
        {
            UserMessage = request.UserMessage,
            AssistantMessage = request.AssistantMessage,
            Provider = request.Provider,
            TurnSetup = new AssistantTurnSetup("system", new List<AITool>(), SupportsTools: true, WebSearchActive: false),
            AtCommands = [],
            TokenizationEnabled = false,
        };
    }

    private static async IAsyncEnumerable<ChatStreamItem> StreamWithToolCallCapture(
        ToolCallHandler? handler, Action<object?> capture)
    {
        if (handler is not null)
        {
            var call = new FunctionCallContent("call-1", "create_todo", new Dictionary<string, object?>());
            capture(await handler(call, new ToolDispatchContext(1)));
        }
        yield return new TextDelta("Done.");
        await Task.Yield();
    }

    private static async IAsyncEnumerable<ChatStreamItem> StreamWithToolCall(
        ToolCallHandler? handler)
    {
        if (handler is not null)
        {
            var call = new FunctionCallContent("call-1", "create_todo", new Dictionary<string, object?>());
            await handler(call, new ToolDispatchContext(1));
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
