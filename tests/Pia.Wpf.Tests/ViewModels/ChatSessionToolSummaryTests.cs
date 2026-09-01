using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// Interactive replies replay as text only, so without a record of its own calls the model answers a
/// question about them from nothing. These pin the record: it reaches the next turn, and it carries no
/// payload.
/// </summary>
public sealed class ChatSessionToolSummaryTests
{
    private readonly IAiClientService _ai = Substitute.For<IAiClientService>();
    private readonly IPluginService _plugins = Substitute.For<IPluginService>();
    private readonly IActionCardBuilder _cards = Substitute.For<IActionCardBuilder>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly ITokenMapService _tokenMap = Substitute.For<ITokenMapService>();
    private readonly IToolPermissionService _permissions = Substitute.For<IToolPermissionService>();

    private readonly List<IList<ChatMessage>> _sent = [];

    public ChatSessionToolSummaryTests()
    {
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        _loc.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => (string)ci[0]);
    }

    private ChatSession CreateSession() => new(
        _tokenMap, _ai, _plugins, _cards, _permissions, _loc, NullLogger.Instance, _ => true);

    private ChatTurnRequest BuildRequest(ChatSession session, string userText)
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
            TurnSetup = new AssistantTurnSetup("system", null, SupportsTools: true, WebSearchActive: false),
            AtCommands = [],
            TokenizationEnabled = false,
        };
    }

    /// <summary>Turn 1 makes one tool call then answers; turn 2 just answers. Both capture what was sent.</summary>
    private void ReturnsToolThenText(string toolName, string path, string finalText)
    {
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _sent.Add(ci.ArgAt<IList<ChatMessage>>(0));
                var handler = _sent.Count == 1 ? ci.ArgAt<ToolCallHandler?>(3) : null;
                return ToolThenTextStream(handler, toolName, path, finalText);
            });
    }

    private static async IAsyncEnumerable<ChatStreamItem> ToolThenTextStream(
        ToolCallHandler? handler, string toolName, string path, string finalText)
    {
        if (handler is not null)
        {
            var args = new Dictionary<string, object?> { ["path"] = path };
            await handler(new FunctionCallContent("call-1", toolName, args), new ToolDispatchContext(1));
        }
        yield return new TextDelta(finalText);
        yield return new Finished(null, "m");
        await Task.Yield();
    }

    [Fact]
    public async Task ToolCall_IsRecordedOnTheReply_AndReachesTheNextTurn()
    {
        ReturnsToolThenText("read_file", "notes.txt", "Done.");
        var session = CreateSession();

        await session.RunTurnAsync(BuildRequest(session, "read my notes"), CancellationToken.None);
        await session.RunTurnAsync(BuildRequest(session, "what did you just do?"), CancellationToken.None);

        var replay = _sent[1].First(m => m.Role == ChatRole.Assistant).Text;
        Assert.Contains("read_file", replay, StringComparison.Ordinal);
        Assert.Contains("notes.txt", replay, StringComparison.Ordinal);
        Assert.Contains("tool calls made while producing this reply", replay, StringComparison.Ordinal);

        // The bubble the user sees is untouched — the record rides ToChatMessage(overrideText).
        Assert.Equal("Done.", session.Messages.First(m => !m.IsUser).Content);
    }

    [Fact]
    public async Task ATurnWithNoToolCalls_AddsNothing()
    {
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _sent.Add(ci.ArgAt<IList<ChatMessage>>(0));
                return ToolThenTextStream(null, "", "", "Hello.");
            });
        var session = CreateSession();

        await session.RunTurnAsync(BuildRequest(session, "hi"), CancellationToken.None);
        await session.RunTurnAsync(BuildRequest(session, "again"), CancellationToken.None);

        var replay = _sent[1].First(m => m.Role == ChatRole.Assistant).Text;
        Assert.Equal("Hello.", replay);
    }
}
