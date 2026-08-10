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

/// <summary>
/// The autonomy policy at the INTERACTIVE gate. It arrives on <c>StepTurnSpec.Policy</c>, so these facts drive
/// <c>RunStepTurnAsync</c> — the Planned-run path — rather than the ordinary <c>RunTurnAsync</c> turn, which
/// carries no run and therefore no policy. That is why every fact in
/// <see cref="ChatSessionStateMachineTests"/> still holds unchanged.
/// </summary>
public sealed class ChatSessionPolicyGateTests
{
    private readonly IAiClientService _ai = Substitute.For<IAiClientService>();
    private readonly IPluginService _plugins = Substitute.For<IPluginService>();
    private readonly IActionCardBuilder _cards = Substitute.For<IActionCardBuilder>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly ITokenMapService _tokenMap = Substitute.For<ITokenMapService>();
    private readonly IToolPermissionService _permissions = Substitute.For<IToolPermissionService>();

    public ChatSessionPolicyGateTests()
    {
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        _loc.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => (string)ci[0]);
        _cards.ResolveStatusText(Arg.Any<string>()).Returns("running");
        _cards.ResolveSuccessTitle(Arg.Any<string>()).Returns("Done");
    }

    private ChatSession CreateSession() => new(
        _tokenMap, _ai, _plugins, _cards, _permissions, _loc, NullLogger.Instance, _ => true);

    private static StepTurnSpec Spec(RunAutonomyPolicy? policy) => new(
        RunId: Guid.NewGuid(),
        Ordinal: 0,
        Intent: "do the thing",
        ExpectedArtifact: null,
        SystemPrompt: "system",
        Persona: new PersonaAttribution(Guid.NewGuid(), "Pia", "🤖"),
        Provider: new AiProvider { Name = "Test", Endpoint = "http://localhost", ProviderType = AiProviderType.OpenAI },
        Tools: new List<AITool>(),
        SupportsTools: true,
        WebSearchActive: false,
        TokenizationEnabled: false,
        Policy: policy);

    private static ActionCardInfo NewCard(string toolName, Guid pluginId) => new()
    {
        Title = toolName,
        Summary = toolName,
        Category = ActionCardCategory.Todo,
        ToolName = toolName,
        PluginId = pluginId,
    };

    private static async IAsyncEnumerable<ChatStreamItem> StreamWithToolCall(
        ToolCallHandler? handler, string toolName, Action<object?> capture)
    {
        if (handler is not null)
            capture(await handler(new FunctionCallContent("call-1", toolName, new Dictionary<string, object?>()), new ToolDispatchContext(1)));

        yield return new TextDelta("Done.");
        await Task.Yield();
    }

    private void ArrangeToolCall(string toolName, PluginToolCall pending, ActionCardInfo card, Action<object?> capture)
    {
        _plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?)null, (PluginToolCall?)pending));
        _cards.Build(Arg.Any<PluginToolCall>(), Arg.Any<bool>(), Arg.Any<ToolGateDecision?>(), Arg.Any<ToolClass?>()).Returns(card);
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci => StreamWithToolCall(
                ci.ArgAt<ToolCallHandler?>(3), toolName, capture));
    }

    private static PluginToolCall Pending(string toolName, string pluginName, Guid pluginId, Action onExecute) =>
        new(toolName, pluginId, pluginName, $"{pluginName}: {toolName}", null, () =>
        {
            onExecute();
            return Task.FromResult<object?>("done");
        });

    [Fact]
    public async Task PolicyCoveredClass_AutoApproves_WithoutAStandingGrant_CardStillAddedFirst()
    {
        var pluginId = BuiltInPluginDefaults.TodoPluginId;
        var card = NewCard("create_todo", pluginId);
        ChatSession? sessionRef = null;
        var cardAddedBeforeExecute = false;
        var executed = false;

        var pending = Pending("create_todo", "todo", pluginId, () =>
        {
            executed = true;
            // Read the LIVE transcript from inside Execute: the card must already be there.
            cardAddedBeforeExecute = sessionRef is not null
                && sessionRef.Messages.Any(m => m.ActionCards.Contains(card));
        });

        // No allowlist entry, NO standing grant: the policy is the only authority.
        _permissions.IsAutoApproveEligible("create_todo").Returns(false);
        _permissions.IsGranted(pluginId, "create_todo").Returns(false);
        ArrangeToolCall("create_todo", pending, card, _ => { });

        var session = CreateSession();
        sessionRef = session;
        var states = new List<ChatState>();
        session.StateChanged += (_, e) => states.Add(e.NewState);

        var result = await session.RunStepTurnAsync(
            Spec(new RunAutonomyPolicy([ToolClass.Todo])),
            new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        Assert.True(executed, "the policy-covered write must have executed");
        // Never prompted…
        Assert.DoesNotContain(ChatState.WaitingForTool, states);
        // …but the pre-resolved card IS in the transcript, and it was there BEFORE the execute (audit trace,
        // never silent).
        Assert.Contains(card, session.Messages.Last(m => !m.IsUser).ActionCards);
        Assert.True(cardAddedBeforeExecute, "the card must be added before Execute runs");
        // The policy did not persist a standing grant — it is per-run authority, not a stored one.
        await _permissions.DidNotReceive().GrantAsync(Arg.Any<Guid>(), Arg.Any<string>());
    }

    [Fact]
    public async Task PolicyCoveredClass_StillPromptsForAnUncoveredClass()
    {
        var pluginId = BuiltInPluginDefaults.FilesPluginId;
        var card = NewCard("write_file", pluginId);
        var executed = false;
        var pending = Pending("write_file", "files", pluginId, () => executed = true);

        _permissions.IsAutoApproveEligible("write_file").Returns(false);
        _permissions.IsGranted(pluginId, "write_file").Returns(false);
        ArrangeToolCall("write_file", pending, card, _ => { });

        var session = CreateSession();
        var states = new List<ChatState>();
        session.StateChanged += (_, e) => states.Add(e.NewState);

        // A Todo-only policy leaves the Files class exactly where it was.
        var turn = session.RunStepTurnAsync(
            Spec(new RunAutonomyPolicy([ToolClass.Todo])),
            new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        await WaitUntilAsync(() => card.IsPending);
        Assert.False(executed);
        Assert.Equal(ChatState.WaitingForTool, session.State);

        card.DeclineCommand.Execute(null);
        await turn;

        Assert.False(executed);
        Assert.Contains(ChatState.WaitingForTool, states);
    }

    /// <summary>The policy is a class switch and never covers a delete, so a destructive MCP tool with no
    /// grant of its own still shows a card — and "Always allow" on it now persists a grant.</summary>
    [Fact]
    public async Task PolicyOverEveryClass_CanNotAutoApproveADestructiveMcpTool()
    {
        var pluginId = Guid.NewGuid();
        var card = NewCard("delete_issue", pluginId);
        var executed = false;
        var pending = Pending("delete_issue", "linear", pluginId, () => executed = true);

        _plugins.IsMcpTool("delete_issue").Returns(true);
        _permissions.IsAutoApproveEligible("delete_issue").Returns(false);
        _permissions.IsGranted(pluginId, "delete_issue").Returns(false);
        ArrangeToolCall("delete_issue", pending, card, _ => { });

        var session = CreateSession();
        var turn = session.RunStepTurnAsync(
            Spec(new RunAutonomyPolicy(Enum.GetValues<ToolClass>())),
            new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        await WaitUntilAsync(() => card.IsPending);
        Assert.False(executed);
        Assert.Equal(ChatState.WaitingForTool, session.State);

        card.AlwaysAllowCommand.Execute(null);
        await turn;

        Assert.True(executed);
        await _permissions.Received(1).GrantAsync(pluginId, "delete_issue");
    }

    [Fact]
    public async Task PolicyDoesNotCoverGit_SoAGitWriteStillPrompts()
    {
        // git_switch/git_restore/git_stash shed uncommitted work but are NOT delete-like by name, so the
        // policy's own !isDeleteLike exclusion would not stop them — only the preset's Git omission does.
        var pluginId = Guid.NewGuid();
        var card = NewCard("git_switch", pluginId);
        var executed = false;
        var pending = Pending("git_switch", "git", pluginId, () => executed = true);

        ArrangeToolCall("git_switch", pending, card, _ => { });

        var session = CreateSession();
        var turn = session.RunStepTurnAsync(
            Spec(RunAutonomyPolicy.FromSettings(new AppSettings { AgentRunAutoApproveBuiltInWrites = true })),
            new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        await WaitUntilAsync(() => card.IsPending);
        Assert.False(executed);

        card.DeclineCommand.Execute(null);
        await turn;
        Assert.False(executed);
    }

    [Theory]
    [InlineData("create_todo", "todo", false, false)]   // allowlisted-but-ungranted still prompts
    [InlineData("write_file", "files", false, false)]
    [InlineData("create_todo", "todo", true, true)]     // allowlisted AND granted auto-runs
    public async Task NullPolicy_IsByteIdenticalToTodaysBehaviour(
        string toolName, string pluginName, bool granted, bool expectAutoRun)
    {
        var pluginId = Guid.NewGuid();
        var card = NewCard(toolName, pluginId);
        var executed = false;
        var pending = Pending(toolName, pluginName, pluginId, () => executed = true);

        _permissions.IsAutoApproveEligible(toolName).Returns(toolName == "create_todo");
        _permissions.IsGranted(pluginId, toolName).Returns(granted);
        ArrangeToolCall(toolName, pending, card, _ => { });

        var session = CreateSession();
        var states = new List<ChatState>();
        session.StateChanged += (_, e) => states.Add(e.NewState);

        var turn = session.RunStepTurnAsync(
            Spec(policy: null), new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        if (expectAutoRun)
        {
            await turn;
            Assert.True(executed);
            Assert.DoesNotContain(ChatState.WaitingForTool, states);
        }
        else
        {
            await WaitUntilAsync(() => card.IsPending);
            Assert.False(executed);
            card.DeclineCommand.Execute(null);
            await turn;
            Assert.False(executed);
        }
    }

    /// <summary>The gate-resolution arm: what it resolved reaches the bypass. The auto-run card is built from
    /// the gate's OWN decision and class — not a bare `true` and not the card builder's guess — because the
    /// resolved card line has to say which authority ran the call.</summary>
    [Fact]
    public async Task AutoRun_BuildsItsCardFromTheGatesOwnDecisionAndClass()
    {
        var pluginId = BuiltInPluginDefaults.TodoPluginId;
        var card = NewCard("create_todo", pluginId);
        var pending = Pending("create_todo", "todo", pluginId, () => { });

        _permissions.IsAutoApproveEligible("create_todo").Returns(false);
        _permissions.IsGranted(pluginId, "create_todo").Returns(false);
        ArrangeToolCall("create_todo", pending, card, _ => { });

        var session = CreateSession();
        var result = await session.RunStepTurnAsync(
            Spec(new RunAutonomyPolicy([ToolClass.Todo])),
            new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        _cards.Received(1).Build(pending, false, ToolGateDecision.AutoApprovedPolicy, ToolClass.Todo);
    }

    /// <summary>The card-confirmation arm restores the state it borrowed: WaitingForTool is entered before the
    /// wait and left in a finally, so the turn is Running again for the next tool of the same step.</summary>
    [Fact]
    public async Task AfterTheCardIsAnswered_TheSessionIsRunningAgain()
    {
        var pluginId = BuiltInPluginDefaults.FilesPluginId;
        var card = NewCard("write_file", pluginId);
        var pending = Pending("write_file", "files", pluginId, () => { });

        ArrangeToolCall("write_file", pending, card, _ => { });

        var session = CreateSession();
        var turn = session.RunStepTurnAsync(
            Spec(policy: null), new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        await WaitUntilAsync(() => card.IsPending);
        Assert.Equal(ChatState.WaitingForTool, session.State);

        card.AllowOnceCommand.Execute(null);
        await turn;

        Assert.NotEqual(ChatState.WaitingForTool, session.State);
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
