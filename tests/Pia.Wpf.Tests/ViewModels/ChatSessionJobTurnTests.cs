using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Plugins;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.ViewModels;

public class ChatSessionJobTurnTests
{
    private readonly IAiClientService _ai = Substitute.For<IAiClientService>();
    private readonly IPluginService _plugins = Substitute.For<IPluginService>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly IChatJobToolService _jobTools = Substitute.For<IChatJobToolService>();
    private readonly Guid _chatId = Guid.NewGuid();

    public ChatSessionJobTurnTests()
    {
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        _loc.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => (string)ci[0]);
        _jobTools.Handles(Arg.Any<string>()).Returns(ci => JobTools.Names.Contains((string)ci[0]));
        _jobTools.HandleAsync(default, default!, default, default).ReturnsForAnyArgs("Paused.");
    }

    private ChatSession Session()
    {
        var session = new ChatSession(Substitute.For<ITokenMapService>(), _ai, _plugins, Substitute.For<IActionCardBuilder>(),
            Substitute.For<IToolPermissionService>(), _loc, NullLogger.Instance, _ => true);
        session.SetIdentity(_chatId, DateTime.UtcNow, null, "t", autoTitleApplied: true);
        return session;
    }

    private ChatTurnRequest Request(ChatSession session, ChatJobTurn? jobTurn)
    {
        var user = new AssistantMessage(ChatRole.User, "how far is it?");
        var assistant = new AssistantMessage(ChatRole.Assistant) { IsStreaming = true };
        session.Messages.Add(user);
        session.Messages.Add(assistant);
        return new ChatTurnRequest
        {
            UserMessage = user,
            AssistantMessage = assistant,
            Provider = new AiProvider { Name = "Test", Endpoint = "http://localhost", ProviderType = AiProviderType.OpenAI },
            TurnSetup = new AssistantTurnSetup("system", new List<AITool>(), SupportsTools: true, WebSearchActive: false),
            AtCommands = [],
            TokenizationEnabled = false,
            JobTurn = jobTurn,
            JobTools = _jobTools,
        };
    }

    private static async IAsyncEnumerable<ChatStreamItem> CallTool(ToolCallHandler? handler, string name, Action<object?> capture)
    {
        if (handler is not null)
            capture(await handler(new FunctionCallContent("c1", name, new Dictionary<string, object?>()), new ToolDispatchContext(1)));
        yield return new TextDelta("ok");
        await Task.Yield();
    }

    [Fact]
    public async Task TheJobBlock_ReachesTheModel_ButIsNeverStored()
    {
        IList<ChatMessage>? captured = null;
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci => { captured = (IList<ChatMessage>)ci[0]; return CallTool(null, "x", _ => { }); });
        var session = Session();
        var request = Request(session, new ChatJobTurn(Guid.NewGuid(), "--- Job block ---", "", []));

        await session.RunTurnAsync(request, CancellationToken.None);

        Assert.Contains("--- Job block ---", captured!.Single(m => m.Role == ChatRole.User).Text);
        Assert.Equal("how far is it?", request.UserMessage.Content);
        Assert.DoesNotContain(session.Messages, m => m.Content?.Contains("Job block") == true);
    }

    [Fact]
    public async Task AJobTool_IsHandledByTheJobService_WhenTheTurnHasAJob()
    {
        object? result = null;
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci => CallTool(ci.ArgAt<ToolCallHandler?>(3), JobTools.Pause, r => result = r));
        var session = Session();

        await session.RunTurnAsync(Request(session, new ChatJobTurn(Guid.NewGuid(), "b", "", [])), CancellationToken.None);

        Assert.Equal("Paused.", result);
        await _jobTools.Received(1).HandleAsync(_chatId, JobTools.Pause, Arg.Any<IDictionary<string, object?>?>(), Arg.Any<CancellationToken>());
        await _plugins.DidNotReceiveWithAnyArgs().RouteToolCallAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AJobToolName_WithoutAJob_IsAnUnknownTool()
    {
        object? result = null;
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci => CallTool(ci.ArgAt<ToolCallHandler?>(3), JobTools.Stop, r => result = r));
        var session = Session();

        await session.RunTurnAsync(Request(session, null), CancellationToken.None);

        Assert.Equal("Unknown tool.", result);
        await _jobTools.DidNotReceiveWithAnyArgs().HandleAsync(default, default!, default, TestContext.Current.CancellationToken);
    }
}
