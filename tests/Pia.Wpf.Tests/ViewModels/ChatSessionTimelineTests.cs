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
/// Batch 03 at the INTERACTIVE gate. The audit sink arrives on <c>StepTurnSpec.Timeline</c>, so these facts
/// drive <c>RunStepTurnAsync</c> — the Planned-run path — rather than the ordinary <c>RunTurnAsync</c> turn,
/// which has no run and therefore records nothing. That is why every fact in
/// <see cref="ChatSessionStateMachineTests"/> still holds unchanged.
/// <para>
/// Cards are answered BEFORE the turn starts. <c>ActionCardInfo</c>'s decision commands complete a
/// <see cref="TaskCompletionSource{TResult}"/> and are idempotent on a non-pending card, so a pre-resolved
/// card makes <c>WaitForUserDecisionAsync</c> return immediately — which keeps these facts free of wall-clock
/// polling.
/// </para>
/// </summary>
public sealed class ChatSessionTimelineTests
{
    private readonly IAiClientService _ai = Substitute.For<IAiClientService>();
    private readonly IPluginService _plugins = Substitute.For<IPluginService>();
    private readonly IActionCardBuilder _cards = Substitute.For<IActionCardBuilder>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly ITokenMapService _tokenMap = Substitute.For<ITokenMapService>();
    private readonly IToolPermissionService _permissions = Substitute.For<IToolPermissionService>();
    private readonly RecordingTimelineService _timeline = new();

    private readonly Guid _runId = Guid.NewGuid();
    private readonly Guid _stepId = Guid.NewGuid();

    public ChatSessionTimelineTests()
    {
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        _loc.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => (string)ci[0]);
        _cards.ResolveStatusText(Arg.Any<string>()).Returns("running");
        _cards.ResolveSuccessTitle(Arg.Any<string>()).Returns("Done");
    }

    // ---- T-EMIT-1: the batch's acceptance test, restated for GATED calls ----

    [Fact]
    public async Task NGatedToolCalls_SomeDenied_ProduceNOrderedEvents_WithTheRightDecisions()
    {
        var todoId = BuiltInPluginDefaults.TodoPluginId;
        var reminderId = BuiltInPluginDefaults.ReminderPluginId;
        var filesId = BuiltInPluginDefaults.FilesPluginId;

        // 1) allowlisted AND granted → auto-approved on the standing grant, no card interaction.
        _permissions.IsAutoApproveEligible("create_todo").Returns(true);
        _permissions.IsGranted(todoId, "create_todo").Returns(true);
        // 2) allowlisted, not granted → card; the user clicks "Allow once".
        _permissions.IsAutoApproveEligible("append_to_list").Returns(true);
        // 3) allowlisted, not granted → card; the user clicks "Always allow".
        _permissions.IsAutoApproveEligible("create_reminder").Returns(true);
        // 4) not allowlisted, not granted → card; the user declines.
        _permissions.IsAutoApproveEligible("write_file").Returns(false);

        var pendings = new Dictionary<string, PluginToolCall>(StringComparer.Ordinal)
        {
            ["create_todo"] = Pending("create_todo", "todo", todoId),
            ["append_to_list"] = Pending("append_to_list", "todo", todoId),
            ["create_reminder"] = Pending("create_reminder", "reminder", reminderId),
            ["write_file"] = Pending("write_file", "files", filesId),
        };
        var cards = pendings.ToDictionary(p => p.Key, p => NewCard(p.Key, p.Value.PluginId), StringComparer.Ordinal);

        // Pre-answer the three cards that need an answer.
        cards["append_to_list"].AllowOnceCommand.Execute(null);
        cards["create_reminder"].AlwaysAllowCommand.Execute(null);
        cards["write_file"].DeclineCommand.Execute(null);

        ArrangeRoutes(pendings, cards);
        ArrangeStream(["create_todo", "append_to_list", "create_reminder", "write_file"]);

        var result = await CreateSession().RunStepTurnAsync(
            Spec(), new RunContext("goal", RunProfile.Interactive), CancellationToken.None);
        Assert.True(result.Succeeded, result.Error);

        var rows = _timeline.Rows;
        Assert.Equal(4, rows.Count);
        Assert.Equal(new long[] { 1, 2, 3, 4 }, rows.Select(r => r.Seq).ToArray());
        Assert.Equal(
            new[] { "create_todo", "append_to_list", "create_reminder", "write_file" },
            rows.Select(r => r.ToolName).ToArray());
        Assert.Equal(
            new[]
            {
                ToolGateDecision.AutoApprovedStandingGrant,
                ToolGateDecision.ApprovedOnce,
                ToolGateDecision.ApprovedAlways,
                ToolGateDecision.DeclinedByUser,
            },
            rows.Select(r => r.Decision).ToArray());
        Assert.Equal(
            new[]
            {
                AgentTimelineOutcome.Ok, AgentTimelineOutcome.Ok, AgentTimelineOutcome.Ok,
                AgentTimelineOutcome.NotExecuted,
            },
            rows.Select(r => r.Outcome).ToArray());
        Assert.All(rows, r =>
        {
            Assert.Equal(ToolGateSurface.Interactive, r.Surface);
            Assert.Equal(AgentTimelineEventKind.ToolCall, r.Kind);
            Assert.Equal(_runId, r.RunId);
        });
        // The class comes from the same classifier the gate resolved with.
        Assert.Equal(ToolClass.Todo, rows[0].ToolClass);
        Assert.Equal(ToolClass.Reminder, rows[2].ToolClass);
        Assert.Equal(ToolClass.Files, rows[3].ToolClass);
    }

    // ---- T-EMIT-2: reads emit nothing. Paired with the fact above, which proves the same path DOES emit. ----

    [Fact]
    public async Task ReadsEmitNothing()
    {
        _plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?)"here are your files", (PluginToolCall?)null));
        ArrangeStream(["list_files"]);

        var result = await CreateSession().RunStepTurnAsync(
            Spec(), new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        // The read path really ran — otherwise "no rows" would be true of a turn where nothing happened.
        await _plugins.Received().RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>());
        Assert.Empty(_timeline.Rows);
    }

    // ---- T-EMIT-3 ----

    [Fact]
    public async Task EveryEventCarriesTheStepId()
    {
        ArrangeAutoApproved("create_todo", BuiltInPluginDefaults.TodoPluginId, "todo");
        ArrangeStream(["create_todo", "create_todo"]);

        await CreateSession().RunStepTurnAsync(
            Spec(), new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        Assert.Equal(2, _timeline.Rows.Count);
        Assert.All(_timeline.Rows, r => Assert.Equal(_stepId, r.StepId));
    }

    [Fact]
    public async Task AnOrdinaryChatTurnRecordsNothing()
    {
        // Control for the fact above: the identical arrangement through RunTurnAsync — no spec, no scope — is
        // silent, which is the whole opt-in mechanism. The "it ran" probe is asserted so that a stub that
        // stops matching RunTurnAsync's call shape cannot make this pass for free.
        var ran = ArrangeAutoApproved("create_todo", BuiltInPluginDefaults.TodoPluginId, "todo");
        ArrangeStream(["create_todo"]);

        var session = CreateSession();
        await session.RunTurnAsync(ToolRequest(session), CancellationToken.None);

        Assert.True(ran(), "the gated tool must have run on the ordinary turn path");
        Assert.Empty(_timeline.Rows);
    }

    // ---- T-EMIT-4 ----

    [Fact]
    public async Task ACancelledCardIsRecordedAsCancelled_NotAsAUserDenial()
    {
        var filesId = BuiltInPluginDefaults.FilesPluginId;
        _permissions.IsAutoApproveEligible("write_file").Returns(false);
        var pending = Pending("write_file", "files", filesId);
        var card = NewCard("write_file", filesId);
        // Cancel (new chat / retry / scope dispose) — the gate maps this to ToolDecision.Decline internally,
        // and recording THAT as "the user declined" would be a false audit statement.
        card.CancelCommand.Execute(null);

        ArrangeRoutes(new() { ["write_file"] = pending }, new() { ["write_file"] = card });
        ArrangeStream(["write_file"]);

        await CreateSession().RunStepTurnAsync(
            Spec(), new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        var row = Assert.Single(_timeline.Rows);
        Assert.Equal(ToolGateDecision.CardCancelled, row.Decision);
        Assert.NotEqual(ToolGateDecision.DeclinedByUser, row.Decision);
        Assert.Equal(AgentTimelineOutcome.NotExecuted, row.Outcome);

        // T2-14: BOTH stamps land on the cancelled path, which is what the `finally` around
        // WaitForUserDecisionAsync is for. The cancel arrives as a TaskCanceledException, so a DecidedAt
        // assigned after a SUCCESSFUL await would be null here and the row would read "still pending" for a
        // question that is definitively over. This is the whole of "including timeout": there is no approval
        // timer in this tree, so a card that ends without an answer ends as a cancellation, and this row is
        // what says how long the question had been open when the turn died.
        Assert.NotNull(row.RequestedAt);
        Assert.NotNull(row.DecidedAt);
        Assert.True(row.DecidedAt >= row.RequestedAt);
        Assert.True(row.CreatedAt >= row.DecidedAt);
    }

    /// <summary>
    /// The PROMPTED accept arm's stamps. Separate from the cancelled fact above because it runs through
    /// <c>ExecuteAndReport</c>, which is shared with the AutoRun bypass: the two authorities carry DIFFERENT
    /// pairs (the card's instants vs. the policy resolver's), and only driving both proves the prompted arm is
    /// not silently reading the policy pair.
    /// <para>
    /// Ordering is asserted with <c>&lt;=</c>, never <c>&lt;</c>. The card here is pre-resolved so
    /// <c>WaitForUserDecisionAsync</c> returns immediately, and <c>DateTime.UtcNow</c> has ~1 ms resolution on
    /// Windows — the two instants are normally EQUAL, and a strict comparison would be a wall-clock flake
    /// rather than a fact. What is asserted is the state: both stamps present, correctly ordered.
    /// </para>
    /// </summary>
    [Fact]
    public async Task APromptedCardRecordsWhenItWasShownAndWhenItWasAnswered()
    {
        var filesId = BuiltInPluginDefaults.FilesPluginId;
        _permissions.IsAutoApproveEligible("write_file").Returns(false);
        var pending = Pending("write_file", "files", filesId);
        var card = NewCard("write_file", filesId);
        card.AllowOnceCommand.Execute(null);

        ArrangeRoutes(new() { ["write_file"] = pending }, new() { ["write_file"] = card });
        ArrangeStream(["write_file"]);

        await CreateSession().RunStepTurnAsync(
            Spec(), new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        var row = Assert.Single(_timeline.Rows);
        Assert.Equal(ToolGateDecision.ApprovedOnce, row.Decision); // the prompted arm, not the bypass
        Assert.NotNull(row.RequestedAt);
        Assert.NotNull(row.DecidedAt);
        Assert.True(row.DecidedAt >= row.RequestedAt);
        Assert.True(row.CreatedAt >= row.DecidedAt);
        // The correlation pair rides along on this arm too.
        Assert.Equal("call-0", row.ToolCallId);
        Assert.Equal(1, row.Round);
    }

    /// <summary>
    /// THE DISCRIMINATING FACT for the prompted arm: its two instants must straddle the HUMAN's answer, not
    /// the policy resolver's.
    /// <para>
    /// <b>Why the two facts above cannot prove this.</b> Both gates take a policy pair
    /// (<c>askedAt</c>/<c>resolvedAt</c>) around <c>ToolAutonomy.Resolve</c>, and the card arms take their own
    /// (<c>cardShownAt</c>/<c>cardDecidedAt</c>). Substituting the policy pair into a card arm — an easy
    /// copy-paste from the AutoRun line a few arms up — leaves both pairs non-null and correctly ordered, so
    /// <c>NotNull</c> and <c>&gt;=</c> assertions pass either way. Verified by mutation: swapping the decline
    /// arm to the policy pair left the ENTIRE suite green before this test existed.
    /// </para>
    /// <para>
    /// <b>The anchor, and why comparing the pair to ITSELF is not enough.</b> The obvious assertion —
    /// <c>DecidedAt &gt; RequestedAt</c> — does NOT discriminate, and that was verified by mutation rather
    /// than assumed: the policy pair brackets a <c>ToolAutonomy.Resolve</c> whose input expression makes
    /// several substitute calls, which is sometimes enough to tick <c>DateTime.UtcNow</c>, so the substituted
    /// arm passed that assertion intermittently. The reliable discriminator is a THIRD instant the test owns:
    /// <c>afterResolve</c>, captured when the gate builds the card, which is provably after <c>resolvedAt</c>
    /// and before <c>cardShownAt</c>.
    /// </para>
    /// <para>
    /// <b>Why a strict comparison is deterministic here</b>, when it would be a coin flip anywhere else in
    /// this batch: the hook then blocks for <see cref="GapMs"/> ms before returning, so <c>cardShownAt</c> is
    /// forced tens of milliseconds past <c>afterResolve</c> — far beyond <c>DateTime.UtcNow</c>'s ~1 ms
    /// Windows resolution. The policy pair is taken entirely BEFORE the card is built, so it can never be
    /// after <c>afterResolve</c>. The gap CAUSES the ordering; it is not raced against, and nothing here
    /// compares an elapsed time to a threshold.
    /// </para>
    /// </summary>
    [Fact]
    public async Task APromptedCardsInstantsStraddleTheHumansAnswer_NotThePolicyResolver()
    {
        var filesId = BuiltInPluginDefaults.FilesPluginId;
        _permissions.IsAutoApproveEligible("write_file").Returns(false);
        var pending = Pending("write_file", "files", filesId);
        var card = NewCard("write_file", filesId);

        var afterResolve = ArrangeLateAnsweredCard(pending, card, c => c.DeclineCommand.Execute(null));
        ArrangeStream(["write_file"]);

        await CreateSession().RunStepTurnAsync(
            Spec(), new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        var row = Assert.Single(_timeline.Rows);
        Assert.Equal(ToolGateDecision.DeclinedByUser, row.Decision); // the `default:` arm, prompted and answered
        Assert.NotNull(row.RequestedAt);
        Assert.NotNull(row.DecidedAt);

        // THE assertions. Both instants are after the resolver had already answered, which is only true of the
        // CARD's pair — substitute the policy pair in and both go red.
        Assert.True(
            row.RequestedAt > afterResolve(),
            $"RequestedAt must be the instant the CARD was shown, not the policy resolver's; got {row.RequestedAt:O} vs anchor {afterResolve():O}");
        Assert.True(
            row.DecidedAt > afterResolve(),
            $"DecidedAt must be the instant the HUMAN answered; got {row.DecidedAt:O} vs anchor {afterResolve():O}");
        Assert.True(row.DecidedAt >= row.RequestedAt);
        Assert.True(row.CreatedAt >= row.DecidedAt);
    }

    /// <summary>Milliseconds the card build is held open, to force the ordering the two facts below assert.</summary>
    private const int GapMs = 60;

    /// <summary>
    /// Arranges a prompted card that is answered only AFTER the gate has shown it, and returns a getter for
    /// the anchor instant — captured as the gate builds the card, i.e. strictly after the policy resolver
    /// answered and strictly before <c>cardShownAt</c> is taken.
    /// </summary>
    private Func<DateTime> ArrangeLateAnsweredCard(
        PluginToolCall pending, ActionCardInfo card, Action<ActionCardInfo> answer)
    {
        var anchor = DateTime.MinValue;
        ArrangeRoutes(
            new Dictionary<string, PluginToolCall>(StringComparer.Ordinal) { [pending.ToolName] = pending },
            new Dictionary<string, ActionCardInfo>(StringComparer.Ordinal) { [pending.ToolName] = card },
            onCardBuilt: built =>
            {
                anchor = DateTime.UtcNow;
                // Held SYNCHRONOUSLY, so cardShownAt (taken right after Build returns) is forced past the
                // anchor. Answered after the same gap again, so DecidedAt is forced past cardShownAt. The gate
                // blocks on WaitForUserDecisionAsync until the command fires, so the turn cannot finish early
                // and there is nothing to race.
                Thread.Sleep(GapMs);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(GapMs);
                    answer(built);
                });
            });
        return () => anchor;
    }

    /// <summary>
    /// The same discriminator on the prompted ACCEPT path, which reaches the emit through
    /// <c>ExecuteAndReport</c> — the local function the AutoRun bypass ALSO calls, with the other pair. That
    /// sharing is why the two instants are explicit parameters there rather than captured locals, and this is
    /// what holds the two authorities apart.
    /// </summary>
    [Fact]
    public async Task APromptedAcceptStampsTheCardsInstants_NotThePolicyResolvers()
    {
        var filesId = BuiltInPluginDefaults.FilesPluginId;
        _permissions.IsAutoApproveEligible("write_file").Returns(false);
        var pending = Pending("write_file", "files", filesId);
        var card = NewCard("write_file", filesId);

        var afterResolve = ArrangeLateAnsweredCard(pending, card, c => c.AllowOnceCommand.Execute(null));
        ArrangeStream(["write_file"]);

        await CreateSession().RunStepTurnAsync(
            Spec(), new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        var row = Assert.Single(_timeline.Rows);
        Assert.Equal(ToolGateDecision.ApprovedOnce, row.Decision);
        Assert.Equal(AgentTimelineOutcome.Ok, row.Outcome);
        Assert.True(
            row.RequestedAt > afterResolve(),
            $"ExecuteAndReport must receive the CARD's pair on this arm, not the resolver's; got RequestedAt={row.RequestedAt:O} vs anchor {afterResolve():O}");
        Assert.True(
            row.DecidedAt > afterResolve(),
            $"ExecuteAndReport must receive the CARD's pair on this arm, not the resolver's; got DecidedAt={row.DecidedAt:O} vs anchor {afterResolve():O}");
        Assert.True(row.DecidedAt >= row.RequestedAt);
    }

    /// <summary>
    /// The control for the two facts above, and the guard in the OTHER direction: the AutoRun bypass must
    /// carry the POLICY's pair. Nobody was asked, so its interval is the resolver's, and it must NOT pick up a
    /// card instant — the bypass renders a resolved card too, so a naive "use cardShownAt everywhere" would
    /// still compile and still produce plausible timestamps.
    /// </summary>
    [Fact]
    public async Task TheAutoRunBypassStampsThePolicyResolversInstants()
    {
        var todoId = BuiltInPluginDefaults.TodoPluginId;
        var ran = ArrangeAutoApproved("create_todo", todoId, "todo");
        ArrangeStream(["create_todo"]);

        await CreateSession().RunStepTurnAsync(
            Spec(), new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        Assert.True(ran());
        var row = Assert.Single(_timeline.Rows);
        Assert.Equal(ToolGateDecision.AutoApprovedStandingGrant, row.Decision);
        Assert.NotNull(row.RequestedAt);
        Assert.NotNull(row.DecidedAt);
        // >=, not >: this pair brackets a few comparisons and is normally EQUAL. Nothing here forces a gap,
        // and asserting one would be the wall-clock flake the batch avoids.
        Assert.True(row.DecidedAt >= row.RequestedAt);
        Assert.True(row.CreatedAt >= row.DecidedAt);
    }

    /// <summary>
    /// The run-level turn (no step) still gets a row, and it carries NO <c>StepOrdinal</c> — an ordinal without
    /// a step would invent one.
    /// <para>
    /// This half alone would be vacuous: the gate hardcodes <c>StepOrdinal: null</c> (the column is
    /// service-assigned, like <c>Seq</c>), so the null asserted here is also what a sink that never assigned an
    /// ordinal at all would produce. Its control is
    /// <see cref="AStepTurnsRowsCarryAPerStepOrdinal"/>, which drives the same tool through a step turn and
    /// reads a NON-null ordinal off the same sink. The pair is what makes either one a fact.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ARunLevelTurnRecordsNoStepOrdinal()
    {
        var ran = ArrangeAutoApproved("create_todo", BuiltInPluginDefaults.TodoPluginId, "todo");
        ArrangeStream(["create_todo"]);

        await CreateSession().RunStepTurnAsync(
            Spec(runLevel: true), new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        Assert.True(ran());
        var runLevel = Assert.Single(_timeline.Rows);
        Assert.Null(runLevel.StepId);
        Assert.Null(runLevel.StepOrdinal);
    }

    /// <summary>
    /// The control for <see cref="ARunLevelTurnRecordsNoStepOrdinal"/>: a STEP turn's rows do get an ordinal,
    /// and it counts within the step. Asserted as the exact sequence, so a sink that handed out one shared
    /// counter — or the same value twice — goes red rather than merely non-null.
    /// <para>
    /// The mechanism under test here is <see cref="RecordingTimelineService"/>'s allocator MIRRORING the real
    /// one, not the real one itself (<c>AgentTimelineServiceTests.Emit_AllocatesStepOrdinal_PerStepNotPerRun</c>
    /// owns that against SQLite). It is worth pinning because every gate assertion about this column in this
    /// suite reads it through the fake: a fake that quietly stopped assigning ordinals would make a future
    /// "the gate lost the step ordinal" bug indistinguishable from correct behaviour.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AStepTurnsRowsCarryAPerStepOrdinal()
    {
        var ran = ArrangeAutoApproved("create_todo", BuiltInPluginDefaults.TodoPluginId, "todo");
        ArrangeStream(["create_todo", "create_todo"]);

        await CreateSession().RunStepTurnAsync(
            Spec(), new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        Assert.True(ran());
        var rows = _timeline.Rows;
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(_stepId, r.StepId));
        Assert.Equal(new long?[] { 1, 2 }, rows.Select(r => r.StepOrdinal).ToArray());
    }

    // ---- T-EMIT-5 ----

    [Fact]
    public async Task AThrowingToolIsRecordedAsError_AndTheExceptionStillPropagates()
    {
        var todoId = BuiltInPluginDefaults.TodoPluginId;
        _permissions.IsAutoApproveEligible("create_todo").Returns(true);
        _permissions.IsGranted(todoId, "create_todo").Returns(true);

        var pending = new PluginToolCall("create_todo", todoId, "todo", "Create a todo", null,
            () => throw new InvalidOperationException("the tool blew up"));
        var card = NewCard("create_todo", todoId);
        ArrangeRoutes(new() { ["create_todo"] = pending }, new() { ["create_todo"] = card });
        ArrangeStream(["create_todo"]);

        var result = await CreateSession().RunStepTurnAsync(
            Spec(), new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        var row = Assert.Single(_timeline.Rows);
        Assert.Equal(AgentTimelineOutcome.Error, row.Outcome);
        Assert.Equal(ToolGateDecision.AutoApprovedStandingGrant, row.Decision); // still says WHY it was allowed
        Assert.Null(row.ResultChars);
        // …and the throw still reaches the step, unchanged by this batch: a swallow here would make the step
        // succeed.
        Assert.False(result.Succeeded);
        Assert.Contains("the tool blew up", result.Error);
    }

    // ---- T-EMIT-6 ----

    [Fact]
    public async Task AnUnknownToolIsRecorded()
    {
        _plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(((object?, PluginToolCall?)?)null);
        ArrangeStream(["no_such_tool"]);

        await CreateSession().RunStepTurnAsync(
            Spec(), new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        var row = Assert.Single(_timeline.Rows);
        Assert.Equal(ToolGateDecision.UnknownTool, row.Decision);
        Assert.Equal(AgentTimelineOutcome.NotExecuted, row.Outcome);
        Assert.Equal("no_such_tool", row.ToolName);
        Assert.Equal(ToolClass.Unknown, row.ToolClass);
        Assert.Null(row.PluginId);
    }

    [Fact]
    public async Task TheAgentModeSuggestionShortCircuitIsNotATimelineEvent()
    {
        // suggest_agent_mode returns before RouteToolCallAsync, so it is neither a gated call nor an unknown
        // tool. Pinned so a future "emit for every unrouted name" does not start recording it.
        ArrangeStream(["suggest_agent_mode"]);

        await CreateSession().RunStepTurnAsync(
            Spec(), new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        Assert.Empty(_timeline.Rows);
        await _plugins.DidNotReceive().RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>());
    }

    // ---- T-EMIT-7: the failure-isolation guardrail, executable ----

    [Fact]
    public async Task AFailingTimelineServiceDoesNotFailTheStep()
    {
        _timeline.ThrowOnEmit = true;
        var executed = ArrangeAutoApproved("create_todo", BuiltInPluginDefaults.TodoPluginId, "todo");
        ArrangeStream(["create_todo"]);

        var result = await CreateSession().RunStepTurnAsync(
            Spec(), new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        Assert.True(executed(), "the tool must still have run");
        Assert.Empty(_timeline.Rows);
    }

    // ---- T-PRIV-3 (the half this suite can prove) ----

    [Fact]
    public async Task ArgsAndResultCharsAreLengthsOfWhatTheHandlerSaw()
    {
        var todoId = BuiltInPluginDefaults.TodoPluginId;
        _permissions.IsAutoApproveEligible("create_todo").Returns(true);
        _permissions.IsGranted(todoId, "create_todo").Returns(true);

        const string resultText = "created todo 42 in the salary folder";
        var pending = new PluginToolCall("create_todo", todoId, "todo", "Create a todo", null,
            () => Task.FromResult<object?>(resultText));
        ArrangeRoutes(new() { ["create_todo"] = pending }, new() { ["create_todo"] = NewCard("create_todo", todoId) });

        var args = new Dictionary<string, object?> { ["title"] = "CANARY-9f3a1c" };
        ArrangeStream(["create_todo"], args);

        await CreateSession().RunStepTurnAsync(
            Spec(), new RunContext("goal", RunProfile.Interactive), CancellationToken.None);

        var row = Assert.Single(_timeline.Rows);
        // The emit sits INSIDE the handler that TokenizingAiClientService.WrapToolHandler decorates, so these
        // are the pre-tokenization lengths (the wrapper's detokenize-in / tokenize-out ordering is pinned by
        // TokenizingAiClientServiceTests). Lengths only — never the text.
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(args).Length, row.ArgsChars);
        Assert.Equal(resultText.Length, row.ResultChars);
        Assert.NotNull(row.DurationMs);
    }

    // ---- harness ----

    private ChatSession CreateSession() => new(
        _tokenMap, _ai, _plugins, _cards, _permissions, _loc, NullLogger.Instance, _ => true);

    /// <param name="runLevel">Drives the planner-degrade RUN-LEVEL turn — a scope with no step id, whose rows
    /// carry no <c>StepOrdinal</c>. The default is the ordinary step turn every other fact here uses.</param>
    private StepTurnSpec Spec(bool runLevel = false) => new(
        RunId: _runId,
        Ordinal: 0,
        Intent: "do the thing",
        ExpectedArtifact: null,
        SystemPrompt: "system",
        Persona: new PersonaAttribution(Guid.NewGuid(), "Pia", "🤖"),
        Provider: TestProvider,
        Tools: new List<AITool>(),
        SupportsTools: true,
        WebSearchActive: false,
        TokenizationEnabled: false,
        // The SCOPE carries the step id; the record has no StepId field of its own (it was written by one
        // executor and read by nobody, so a spec-level value could silently disagree with the scope's).
        Timeline: new AgentTimelineScope(_timeline, _runId, runLevel ? null : _stepId));

    private static AiProvider TestProvider => new()
    {
        Name = "Test",
        Endpoint = "http://localhost",
        ProviderType = AiProviderType.OpenAI,
    };

    private ChatTurnRequest ToolRequest(ChatSession session)
    {
        var user = new AssistantMessage(ChatRole.User, "hi");
        var assistant = new AssistantMessage(ChatRole.Assistant) { IsStreaming = true };
        session.Messages.Add(user);
        session.Messages.Add(assistant);
        return new ChatTurnRequest
        {
            UserMessage = user,
            AssistantMessage = assistant,
            Provider = TestProvider,
            TurnSetup = new AssistantTurnSetup("system", new List<AITool>(), SupportsTools: true, WebSearchActive: false),
            AtCommands = [],
            TokenizationEnabled = false,
        };
    }

    private static PluginToolCall Pending(string toolName, string pluginName, Guid pluginId) =>
        new(toolName, pluginId, pluginName, $"{pluginName}: {toolName}", null,
            () => Task.FromResult<object?>("done"));

    private static ActionCardInfo NewCard(string toolName, Guid pluginId) => new()
    {
        Title = toolName,
        Summary = toolName,
        Category = ActionCardCategory.Todo,
        ToolName = toolName,
        PluginId = pluginId,
    };

    /// <summary>Arranges one auto-approved (allowlisted + granted) tool and returns an "it ran" probe.</summary>
    private Func<bool> ArrangeAutoApproved(string toolName, Guid pluginId, string pluginName)
    {
        var ran = false;
        _permissions.IsAutoApproveEligible(toolName).Returns(true);
        _permissions.IsGranted(pluginId, toolName).Returns(true);
        var pending = new PluginToolCall(toolName, pluginId, pluginName, $"{pluginName}: {toolName}", null,
            () => { ran = true; return Task.FromResult<object?>("done"); });
        ArrangeRoutes(
            new Dictionary<string, PluginToolCall>(StringComparer.Ordinal) { [toolName] = pending },
            new Dictionary<string, ActionCardInfo>(StringComparer.Ordinal) { [toolName] = NewCard(toolName, pluginId) });
        return () => ran;
    }

    /// <param name="onCardBuilt">Invoked as the gate BUILDS a card, i.e. after the policy resolver has already
    /// answered and immediately before the card is shown. It is the only hook in this suite that can act
    /// between those two instants, which is what
    /// <see cref="APromptedCardsInstantsStraddleTheHumansAnswer_NotThePolicyResolver"/> needs.</param>
    private void ArrangeRoutes(
        Dictionary<string, PluginToolCall> pendings, Dictionary<string, ActionCardInfo> cards,
        Action<ActionCardInfo>? onCardBuilt = null)
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
                var built = cards[ci.ArgAt<PluginToolCall>(0).ToolName];
                onCardBuilt?.Invoke(built);
                return built;
            });
    }

    /// <summary>Drives the handler once per tool name, in order, then closes the stream.</summary>
    private void ArrangeStream(string[] toolNames, IDictionary<string, object?>? arguments = null)
    {
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(),
                Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>(), contextBudget: Arg.Any<AgentContextBudget?>())
            .Returns(ci => Stream(ci.ArgAt<ToolCallHandler?>(3), toolNames, arguments));
    }

    private static async IAsyncEnumerable<ChatStreamItem> Stream(
        ToolCallHandler? handler, string[] toolNames, IDictionary<string, object?>? arguments)
    {
        if (handler is not null)
        {
            var i = 0;
            foreach (var name in toolNames)
            {
                await handler(new FunctionCallContent(
                    $"call-{i++}", name, arguments ?? new Dictionary<string, object?>()), new ToolDispatchContext(1));
            }
        }

        yield return new TextDelta("Done.");
        await Task.Yield();
    }
}
