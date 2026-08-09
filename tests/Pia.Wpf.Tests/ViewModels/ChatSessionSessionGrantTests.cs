using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Plugins;
using Pia.Tests.Services;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>Cards are pre-answered before the turn starts, so no fact here needs wall-clock polling.</summary>
public sealed class ChatSessionSessionGrantTests
{
    private readonly IAiClientService _ai = Substitute.For<IAiClientService>();
    private readonly IPluginService _plugins = Substitute.For<IPluginService>();
    private readonly IActionCardBuilder _cards = Substitute.For<IActionCardBuilder>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly ITokenMapService _tokenMap = Substitute.For<ITokenMapService>();
    private readonly IToolPermissionService _permissions = Substitute.For<IToolPermissionService>();
    private readonly SessionToolGrantStore _session = new();
    private readonly RecordingTimelineService _timeline = new();

    private readonly Guid _runId = Guid.NewGuid();
    private readonly Guid _stepId = Guid.NewGuid();

    private static readonly Guid FilesId = BuiltInPluginDefaults.FilesPluginId;

    public ChatSessionSessionGrantTests()
    {
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        _loc.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => (string)ci[0]);
        _cards.ResolveStatusText(Arg.Any<string>()).Returns("running");
        _cards.ResolveSuccessTitle(Arg.Any<string>()).Returns("Done");

        _permissions.When(p => p.GrantForSession(Arg.Any<Guid>(), Arg.Any<string>()))
            .Do(ci => _session.Grant(ci.ArgAt<Guid>(0), ci.ArgAt<string>(1)));
        _permissions.IsGrantedForSession(Arg.Any<Guid>(), Arg.Any<string>())
            .Returns(ci => _session.IsGranted(ci.ArgAt<Guid>(0), ci.ArgAt<string>(1)));
    }

    [Fact]
    public async Task SessionGrant_MakesTheSecondCallOfTheSameTool_RunWithoutACard()
    {
        var executions = 0;
        var pending = new PluginToolCall("write_file", FilesId, "files", "files: write_file", null,
            () => { executions++; return Task.FromResult<object?>("done"); });

        // Neither allowlisted nor standing-grantable, so only the session tier can carry the second call.
        _permissions.IsAutoApproveEligible("write_file").Returns(false);
        _permissions.IsGranted(FilesId, "write_file").Returns(false);

        var card = NewCard("write_file", FilesId, sessionGrantable: true);
        card.AllowForSessionCommand.Execute(null);

        var autoApprovedFlags = ArrangeRoutes(pending, card);
        ArrangeStream(["write_file", "write_file"]);

        var result = await CreateSession().RunStepTurnAsync(
            Spec(), new RunContext("goal", RunProfile.Interactive), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(2, executions);

        Assert.Equal(
            new[] { ToolGateDecision.ApprovedForSession, ToolGateDecision.AutoApprovedSessionGrant },
            _timeline.Rows.Select(r => r.Decision).ToArray());
        Assert.All(_timeline.Rows, r => Assert.Equal(AgentTimelineOutcome.Ok, r.Outcome));

        Assert.Equal([null, ToolGateDecision.AutoApprovedSessionGrant], autoApprovedFlags);
        _permissions.Received(1).GrantForSession(FilesId, "write_file");
    }

    /// <summary>The localization double echoes keys, so a KEY is what is asserted here, not a translation.</summary>
    [Fact]
    public async Task SessionGrant_TheBypassCard_SaysForThisSession_NotAlwaysAllow()
    {
        var pending = Pending("write_file", "files", FilesId);
        var answered = NewCard("write_file", FilesId, sessionGrantable: true);
        answered.AllowForSessionCommand.Execute(null);
        ArrangeRoutes(pending, answered);

        var real = new ActionCardBuilder(_loc, _tokenMap, _permissions);
        ActionCardInfo? bypass = null;
        _cards.Build(Arg.Any<PluginToolCall>(), Arg.Any<bool>(), Arg.Any<ToolGateDecision?>(), Arg.Any<ToolClass?>())
            .Returns(ci => ci.ArgAt<ToolGateDecision?>(2) is { } tier
                ? bypass = real.Build(ci.ArgAt<PluginToolCall>(0), false, tier, ci.ArgAt<ToolClass?>(3))
                : answered);
        ArrangeStream(["write_file", "write_file"]);

        await CreateSession().RunStepTurnAsync(
            Spec(), new RunContext("goal", RunProfile.Interactive), TestContext.Current.CancellationToken);

        Assert.Equal(
            new[] { ToolGateDecision.ApprovedForSession, ToolGateDecision.AutoApprovedSessionGrant },
            _timeline.Rows.Select(r => r.Decision).ToArray());
        Assert.NotNull(bypass);
        Assert.True(bypass.IsAutoApproved);

        Assert.Equal("ActionCard_AutoApprovedForSession", bypass.ResolvedStatusText);
        // Named so a regression back to the permanent sentence cannot pass silently.
        Assert.NotEqual("ActionCard_AutoApproved", bypass.ResolvedStatusText);

        var standing = real.Build(pending, false, ToolGateDecision.AutoApprovedStandingGrant);
        Assert.Equal("ActionCard_AutoApproved", standing.ResolvedStatusText);
    }

    [Fact]
    public async Task SessionGrant_NeverPersistsAStandingGrant()
    {
        var pending = Pending("write_file", "files", FilesId);
        var card = NewCard("write_file", FilesId, sessionGrantable: true);
        card.AllowForSessionCommand.Execute(null);
        ArrangeRoutes(pending, card);
        ArrangeStream(["write_file"]);

        await CreateSession().RunStepTurnAsync(
            Spec(), new RunContext("goal", RunProfile.Interactive), TestContext.Current.CancellationToken);

        await _permissions.DidNotReceive().GrantAsync(Arg.Any<Guid>(), Arg.Any<string>());
        _permissions.Received(1).GrantForSession(FilesId, "write_file");
        // Non-vacuity: a turn that never reached the arm would also satisfy the DidNotReceive above.
        Assert.Equal(ToolGateDecision.ApprovedForSession, Assert.Single(_timeline.Rows).Decision);
    }

    /// <summary>The card is only a UI hint; the gate is the authority.</summary>
    [Fact]
    public async Task AllowForSession_OnANonOfferableTool_ExecutesOnce_AndMintsNothing()
    {
        var executions = 0;
        var pending = new PluginToolCall("delete_file", FilesId, "files", "files: delete_file", null,
            () => { executions++; return Task.FromResult<object?>("done"); });

        // A forged card: the real builder never offers the tier for a delete-like tool.
        var card = NewCard("delete_file", FilesId, sessionGrantable: true);
        card.AllowForSessionCommand.Execute(null);
        ArrangeRoutes(pending, card);
        ArrangeStream(["delete_file", "delete_file"]);

        await CreateSession().RunStepTurnAsync(
            Spec(), new RunContext("goal", RunProfile.Interactive), TestContext.Current.CancellationToken);

        Assert.Equal(2, executions);
        _permissions.DidNotReceive().GrantForSession(Arg.Any<Guid>(), Arg.Any<string>());
        await _permissions.DidNotReceive().GrantAsync(Arg.Any<Guid>(), Arg.Any<string>());
        Assert.False(_session.IsGranted(FilesId, "delete_file"));
        Assert.Equal(
            new[] { ToolGateDecision.ApprovedOnce, ToolGateDecision.ApprovedOnce },
            _timeline.Rows.Select(r => r.Decision).ToArray());
    }

    [Fact]
    public async Task SessionGrant_DoesNotCarryASiblingTool()
    {
        var writeCard = NewCard("write_file", FilesId, sessionGrantable: true);
        writeCard.AllowForSessionCommand.Execute(null);
        var moveCard = NewCard("move_file", FilesId, sessionGrantable: true);
        moveCard.DeclineCommand.Execute(null);

        var pendings = new Dictionary<string, PluginToolCall>(StringComparer.Ordinal)
        {
            ["write_file"] = Pending("write_file", "files", FilesId),
            ["move_file"] = Pending("move_file", "files", FilesId),
        };
        var cards = new Dictionary<string, ActionCardInfo>(StringComparer.Ordinal)
        {
            ["write_file"] = writeCard,
            ["move_file"] = moveCard,
        };
        ArrangeRoutes(pendings, cards);
        ArrangeStream(["write_file", "move_file"]);

        await CreateSession().RunStepTurnAsync(
            Spec(), new RunContext("goal", RunProfile.Interactive), TestContext.Current.CancellationToken);

        Assert.Equal(
            new[] { ToolGateDecision.ApprovedForSession, ToolGateDecision.DeclinedByUser },
            _timeline.Rows.Select(r => r.Decision).ToArray());
        Assert.True(_session.IsGranted(FilesId, "write_file"));
        Assert.False(_session.IsGranted(FilesId, "move_file"));
    }

    /// <summary>The gate's <c>default:</c> arm declines, so an unhandled member emits a false audit row instead of failing.</summary>
    [Fact]
    public async Task EveryToolDecision_HasAProducerAndAnAccountedGateArm()
    {
        // Walked off the gate's switch by hand, not copied from the enum.
        var accounted = new Dictionary<ToolDecision, string>
        {
            [ToolDecision.AllowOnce] = "case AllowOnce → execute, ApprovedOnce",
            [ToolDecision.AlwaysAllow] = "case AlwaysAllow → GrantAsync when offerable, ApprovedAlways",
            [ToolDecision.AllowForSession] = "case AllowForSession → GrantForSession when offerable, ApprovedForSession",
            [ToolDecision.Decline] = "default: → nothing executes, DeclinedByUser/CardCancelled",
        };
        // Materialized so a failure names the member, and so xUnit2029 has no query to complain about.
        var unaccounted = Enum.GetValues<ToolDecision>().Where(d => !accounted.ContainsKey(d)).ToArray();
        Assert.Empty(unaccounted);

        var presses = new Action<ActionCardInfo>[]
        {
            c => c.AllowOnceCommand.Execute(null),
            c => c.AlwaysAllowCommand.Execute(null),
            c => c.AllowForSessionCommand.Execute(null),
            c => c.DeclineCommand.Execute(null),
        };

        var produced = new List<ToolDecision>();
        foreach (var press in presses)
        {
            var card = NewCard("write_file", FilesId, sessionGrantable: true);
            var wait = card.WaitForUserDecisionAsync();
            press(card);
            produced.Add(await wait);
        }

        Assert.Equal(
            Enum.GetValues<ToolDecision>().Order().ToArray(),
            produced.Order().ToArray());
    }

    // ---- harness ----

    private ChatSession CreateSession() => new(
        _tokenMap, _ai, _plugins, _cards, _permissions, _loc, NullLogger.Instance, _ => true);

    private StepTurnSpec Spec() => new(
        RunId: _runId,
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
        Timeline: new AgentTimelineScope(_timeline, _runId, _stepId));

    private static PluginToolCall Pending(string toolName, string pluginName, Guid pluginId) =>
        new(toolName, pluginId, pluginName, $"{pluginName}: {toolName}", null,
            () => Task.FromResult<object?>("done"));

    private static ActionCardInfo NewCard(string toolName, Guid pluginId, bool sessionGrantable) => new()
    {
        Title = toolName,
        Summary = toolName,
        Category = ActionCardCategory.Files,
        ToolName = toolName,
        PluginId = pluginId,
        IsSessionGrantable = sessionGrantable,
    };

    /// <summary>Returns the captured <c>autoApprovedAs</c> decision per Build call, in order — null on the prompted path.</summary>
    private List<ToolGateDecision?> ArrangeRoutes(PluginToolCall pending, ActionCardInfo card)
    {
        var flags = new List<ToolGateDecision?>();
        ArrangeRoutes(
            new Dictionary<string, PluginToolCall>(StringComparer.Ordinal) { [pending.ToolName] = pending },
            new Dictionary<string, ActionCardInfo>(StringComparer.Ordinal) { [pending.ToolName] = card },
            flags);
        return flags;
    }

    private void ArrangeRoutes(
        Dictionary<string, PluginToolCall> pendings, Dictionary<string, ActionCardInfo> cards,
        List<ToolGateDecision?>? autoApprovedFlags = null)
    {
        _plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var name = ci.ArgAt<FunctionCallContent>(0).Name;
                return pendings.TryGetValue(name, out var p)
                    ? ((object?)null, (PluginToolCall?)p)
                    : null;
            });
        _cards.Build(Arg.Any<PluginToolCall>(), Arg.Any<bool>(), Arg.Any<ToolGateDecision?>(), Arg.Any<ToolClass?>())
            .Returns(ci =>
            {
                autoApprovedFlags?.Add(ci.ArgAt<ToolGateDecision?>(2));
                return cards[ci.ArgAt<PluginToolCall>(0).ToolName];
            });
    }

    private void ArrangeStream(string[] toolNames)
    {
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(),
                Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>(),
                contextBudget: Arg.Any<AgentContextBudget?>())
            .Returns(ci => Stream(ci.ArgAt<ToolCallHandler?>(3), toolNames));
    }

    private static async IAsyncEnumerable<ChatStreamItem> Stream(
        ToolCallHandler? handler, string[] toolNames)
    {
        if (handler is not null)
        {
            var i = 0;
            foreach (var name in toolNames)
                await handler(new FunctionCallContent($"call-{i++}", name, new Dictionary<string, object?>()), new ToolDispatchContext(1));
        }

        yield return new TextDelta("Done.");
        await Task.Yield();
    }
}
