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

/// <summary>
/// hermes #15 at the INTERACTIVE gate: the SESSION tier, end to end through the real
/// <c>ChatSession.HandleToolCall</c> and the real <c>ToolAutonomy</c> resolver.
/// <para>
/// THE GAP. Pia had "allow once" and a PERSISTED "always allow", so a user who did not want to answer the
/// same card forty times was pushed into a grant that outlives the session, the restart and the reason they
/// gave it — and for the tool they actually see forty times (<c>write_file</c>) there was no grant at all.
/// The middle tier is a process-scoped grant that authorizes the SECOND call and writes nothing durable.
/// </para>
/// <para>
/// The session store here is REAL (<see cref="SessionToolGrantStore"/>) and wired through the substituted
/// permission service, so the fact under test is the whole loop — the card's decision, the mint at the gate,
/// the store, and the gate's lookup on the next call — rather than a stubbed <c>true</c>.
/// </para>
/// <para>
/// Cards are pre-answered before the turn starts, the same trick <see cref="ChatSessionTimelineTests"/> uses:
/// the decision commands complete a <see cref="TaskCompletionSource{TResult}"/>, so
/// <c>WaitForUserDecisionAsync</c> returns immediately and no fact here needs wall-clock polling.
/// </para>
/// </summary>
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

        // The REAL store behind the substituted owner: GrantForSession writes it, IsGrantedForSession reads it.
        _permissions.When(p => p.GrantForSession(Arg.Any<Guid>(), Arg.Any<string>()))
            .Do(ci => _session.Grant(ci.ArgAt<Guid>(0), ci.ArgAt<string>(1)));
        _permissions.IsGrantedForSession(Arg.Any<Guid>(), Arg.Any<string>())
            .Returns(ci => _session.IsGranted(ci.ArgAt<Guid>(0), ci.ArgAt<string>(1)));
    }

    /// <summary>
    /// T-SESS-9, THE HEADLINE. Two identical <c>write_file</c> calls in one turn. The user answers the first
    /// with "Allow this session"; the second is not put to them at all — it bypasses on the session grant.
    /// <para>
    /// The DISCRIMINATOR is the audit decision of the second row. <c>AutoApprovedSessionGrant</c> is written on
    /// exactly one path — the AutoRun bypass, which builds a pre-resolved card and never awaits a decision — so
    /// it cannot be produced by a card that happened to already be answered. The <c>autoApprovedAs</c>
    /// argument captured off the builder says the same thing from the UI side.
    /// </para>
    /// <para>
    /// <b>Neutralize (the session-store LOOKUP only, not the feature):</b> in <c>ChatSession.HandleToolCall</c>
    /// replace <c>HasSessionGrant: _permissions.IsGrantedForSession(pluginId, tool)</c> with
    /// <c>HasSessionGrant: false</c>, leaving the mint, the store and the card intact → the second call is
    /// carded again, its row reads <c>ApprovedForSession</c> instead of <c>AutoApprovedSessionGrant</c> and no
    /// auto-approved card is built. Deterministic in both worlds: the shared card is already answered, so the
    /// neutralized run cannot hang.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SessionGrant_MakesTheSecondCallOfTheSameTool_RunWithoutACard()
    {
        var executions = 0;
        var pending = new PluginToolCall("write_file", FilesId, "files", "files: write_file", null,
            () => { executions++; return Task.FromResult<object?>("done"); });

        // write_file is NOT allowlisted and NOT standing-grantable — the tier under test is the only authority
        // that can carry the second call, so nothing here can pass on the persisted tier by accident.
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

        // The audit trail is the discriminator: minted on the first call, cited on the second.
        Assert.Equal(
            new[] { ToolGateDecision.ApprovedForSession, ToolGateDecision.AutoApprovedSessionGrant },
            _timeline.Rows.Select(r => r.Decision).ToArray());
        Assert.All(_timeline.Rows, r => Assert.Equal(AgentTimelineOutcome.Ok, r.Outcome));

        // …and from the UI side: exactly one card was raised for a decision, the second was a bypass render —
        // and the bypass render was told WHICH tier authorized it, not merely that something had.
        Assert.Equal([null, ToolGateDecision.AutoApprovedSessionGrant], autoApprovedFlags);

        // The grant was minted exactly once, by the tier's own path.
        _permissions.Received(1).GrantForSession(FilesId, "write_file");
    }

    /// <summary>
    /// REVIEW FIX (#15). The bypass card must not claim a PERMANENT grant for a SESSION one. The AutoRun path
    /// used to call <c>Build(..., autoApproved: true, ...)</c> — a bare bool — so the card could not tell
    /// <c>AutoApprovedSessionGrant</c> from <c>AutoApprovedStandingGrant</c> and rendered
    /// <c>ActionCard_AutoApproved</c> ("Auto-approved · you always allow {0}") either way. A user who clicked
    /// "Allow this session" was told, on the very next call, that they always allow it — and then had nowhere
    /// to revoke it, because <c>ToolPermissionService.List()</c> returns <c>AlwaysAllowedTools</c> only and the
    /// session tier writes nothing.
    /// <para>
    /// The bypass card here comes from the REAL <see cref="ActionCardBuilder"/> (the first, decision-bearing
    /// card stays a pre-answered stub, or nothing could pre-press it), so the string under assertion is the one
    /// production renders. The localization double echoes keys, so a KEY is what is asserted; the three
    /// translations are <c>LocalizationTests</c>' business.
    /// </para>
    /// <para>Neutralize: in <c>ActionCardBuilder</c> make <c>AutoApprovedStatusText</c> unconditionally
    /// <c>Format("ActionCard_AutoApproved", title)</c> again, or pass <c>true</c>-equivalent from
    /// <c>ChatSession</c> instead of <c>verdict.Decision</c> → red.</para>
    /// </summary>
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

        // Premise: the second call really did bypass on the session tier.
        Assert.Equal(
            new[] { ToolGateDecision.ApprovedForSession, ToolGateDecision.AutoApprovedSessionGrant },
            _timeline.Rows.Select(r => r.Decision).ToArray());
        Assert.NotNull(bypass);
        Assert.True(bypass.IsAutoApproved);

        Assert.Equal("ActionCard_AutoApprovedForSession", bypass.ResolvedStatusText);
        // The false sentence, named so the fix cannot regress into it silently.
        Assert.NotEqual("ActionCard_AutoApproved", bypass.ResolvedStatusText);

        // …and the PERMANENT tier keeps the permanent sentence: the fix distinguishes, it does not rename.
        var standing = real.Build(pending, false, ToolGateDecision.AutoApprovedStandingGrant);
        Assert.Equal("ActionCard_AutoApproved", standing.ResolvedStatusText);
    }

    /// <summary>
    /// T-SESS-10, THE NO-LEAK FACT at the gate. Choosing the session tier must never reach
    /// <c>AppSettings.AlwaysAllowedTools</c>: the standing-grant path is not called, so nothing is persisted
    /// and nothing appears in the Settings grant list to be revoked.
    /// <para>
    /// <b>Red demo (inject the defect):</b> add <c>await _permissions.GrantAsync(pluginId, tool);</c> to the
    /// <c>AllowForSession</c> arm → the <c>DidNotReceive</c> below reds. (The settings-level half of this fact,
    /// with a REAL <c>ToolPermissionService</c> over a real <c>AppSettings</c>, is
    /// <c>ToolPermissionServiceTests.GrantForSession_TouchesNeitherAppSettingsNorTheStandingTier</c>.)
    /// </para>
    /// </summary>
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
        // Control: the tier really was exercised — a turn that never reached the arm would also satisfy the
        // DidNotReceive above.
        Assert.Equal(ToolGateDecision.ApprovedForSession, Assert.Single(_timeline.Rows).Decision);
    }

    /// <summary>
    /// T-SESS-11, DENY-BY-DEFAULT. The card is a UI hint and the gate is the authority: a card that somehow
    /// surfaced "Allow this session" for a tool the rule excludes (here <c>delete_file</c>) executes ONCE and
    /// mints NOTHING — and the audit row says <c>ApprovedOnce</c>, because claiming a tier the user does not
    /// hold would misreport what happened.
    /// <para>
    /// <b>Neutralize:</b> drop the <c>if (sessionOfferable)</c> guard from the <c>AllowForSession</c> arm → the
    /// mint and the decision both red.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AllowForSession_OnANonOfferableTool_ExecutesOnce_AndMintsNothing()
    {
        var executions = 0;
        var pending = new PluginToolCall("delete_file", FilesId, "files", "files: delete_file", null,
            () => { executions++; return Task.FromResult<object?>("done"); });

        // A forged/stale card: the real builder would never offer the tier for a delete-like tool.
        var card = NewCard("delete_file", FilesId, sessionGrantable: true);
        card.AllowForSessionCommand.Execute(null);
        ArrangeRoutes(pending, card);
        ArrangeStream(["delete_file", "delete_file"]);

        await CreateSession().RunStepTurnAsync(
            Spec(), new RunContext("goal", RunProfile.Interactive), TestContext.Current.CancellationToken);

        // Both calls ran (the user did approve each one), but neither ran on a grant.
        Assert.Equal(2, executions);
        _permissions.DidNotReceive().GrantForSession(Arg.Any<Guid>(), Arg.Any<string>());
        await _permissions.DidNotReceive().GrantAsync(Arg.Any<Guid>(), Arg.Any<string>());
        Assert.False(_session.IsGranted(FilesId, "delete_file"));
        Assert.Equal(
            new[] { ToolGateDecision.ApprovedOnce, ToolGateDecision.ApprovedOnce },
            _timeline.Rows.Select(r => r.Decision).ToArray());
    }

    /// <summary>
    /// T-SESS-12. The tier is per (plugin, tool): a session grant for one tool does not carry another tool of
    /// the same plugin. Without this, "allow this session" would read as "allow this plugin this session".
    /// </summary>
    [Fact]
    public async Task SessionGrant_DoesNotCarryASiblingTool()
    {
        var writeCard = NewCard("write_file", FilesId, sessionGrantable: true);
        writeCard.AllowForSessionCommand.Execute(null);
        var moveCard = NewCard("move_file", FilesId, sessionGrantable: true);
        moveCard.DeclineCommand.Execute(null); // the sibling is still put to the user, and declined

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

    /// <summary>
    /// T-SESS-17, DENY-BY-DEFAULT, mechanized at the only place it can be. <see cref="ToolDecision"/> has
    /// exactly ONE consumer switch (<c>ChatSession.HandleToolCall</c>) and its <c>default:</c> arm IS the
    /// decline arm — it executes nothing, emits <c>DeclinedByUser</c>/<c>CardCancelled</c> and tells the model
    /// not to retry — so a member nobody handles is fail-closed on execution while producing a FALSE audit row
    /// and a false sentence to the model. Nothing in the language forces the next member to be handled, so this
    /// does: it asserts the enum's value space and the CARD's producers are in bijection, and lists what the
    /// gate does with each.
    /// <para>
    /// Adding a fifth member reds here with a diff naming it. The executable evidence that the fall-through
    /// really withholds execution is <c>ChatSessionTimelineTests</c>' decline rows (T-EMIT-1 and
    /// <c>ACancelledCardIsRecordedAsCancelled_NotAsAUserDenial</c>), which take that same arm.
    /// </para>
    /// </summary>
    [Fact]
    public async Task EveryToolDecision_HasAProducerAndAnAccountedGateArm()
    {
        // What the gate does with each member, walked off the switch rather than copied from the enum.
        var accounted = new Dictionary<ToolDecision, string>
        {
            [ToolDecision.AllowOnce] = "case AllowOnce → execute, ApprovedOnce",
            [ToolDecision.AlwaysAllow] = "case AlwaysAllow → GrantAsync when offerable, ApprovedAlways",
            [ToolDecision.AllowForSession] = "case AllowForSession → GrantForSession when offerable, ApprovedForSession",
            [ToolDecision.Decline] = "default: → nothing executes, DeclinedByUser/CardCancelled",
        };
        // Materialized before the assertion so the failure names the member (and so xUnit2029 has nothing to
        // say about a LINQ query handed to Assert.Empty) — the same shape AgentTimelineVocabularyTests uses.
        var unaccounted = Enum.GetValues<ToolDecision>().Where(d => !accounted.ContainsKey(d)).ToArray();
        Assert.Empty(unaccounted);

        // …and every one of them is REACHABLE from a button, so the list above is a statement about the whole
        // producible value space and not just about the declaration order.
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

    // ---- harness (the shape ChatSessionTimelineTests uses) ----

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

    /// <summary>
    /// One tool; returns the captured <c>autoApprovedAs</c> DECISION per Build call, in order — null on the
    /// prompted path. It used to capture a bare bool, which is exactly why the card could not tell a session
    /// grant from a permanent one.
    /// </summary>
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
                Arg.Any<Func<FunctionCallContent, Task<object?>>?>(), Arg.Any<string?>(),
                Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>(),
                contextBudget: Arg.Any<AgentContextBudget?>())
            .Returns(ci => Stream(ci.ArgAt<Func<FunctionCallContent, Task<object?>>?>(3), toolNames));
    }

    private static async IAsyncEnumerable<ChatStreamItem> Stream(
        Func<FunctionCallContent, Task<object?>>? handler, string[] toolNames)
    {
        if (handler is not null)
        {
            var i = 0;
            foreach (var name in toolNames)
                await handler(new FunctionCallContent($"call-{i++}", name, new Dictionary<string, object?>()));
        }

        yield return new TextDelta("Done.");
        await Task.Yield();
    }
}
