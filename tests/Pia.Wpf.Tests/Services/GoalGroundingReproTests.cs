using System.IO;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Providers;
using Pia.Shared.Models;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Batch 18's repro, as the acceptance fact spec §8.1 asks for: <i>"A run launched with a goal the model cannot
/// ground reaches a parked state with the model's question attached, and CREATES NO STEPS. The current behaviour
/// — a fabricated multi-step plan executing to completion — is the negative half, and it must be a FAILING
/// assertion before G2, or the test does not check what it claims."</i>
/// <para>
/// <b>It landed TEST-ONLY, ahead of 18 G2</b> — this branch's precedent for pinning a premise before the code
/// that relies on it (Batch 08's G1 was test-only and landed first; see <see cref="D5PausePremiseTests"/>).
/// <b>G2 has since landed, so nothing here is expected red any more</b>, and the one fact that used to assert the
/// DEFECT (a decline mistaken for silence, degrading to the R10 single-turn fallback) was inverted in that same
/// commit into spec §8.2's positive fact, as its own doc said it must be. §8.3 was added there too.
/// </para>
/// <para>
/// <b>Why the REAL planner and not an <c>IAgentPlanner</c> double.</b> The whole subject is what happens when a
/// <i>plan turn</i> cannot ground the goal, and today the only channel that can carry "I cannot ground this" is
/// the provider wire (spec §1.2: <c>PlanStepArg</c>'s five members are all about how to do the work, none about
/// whether the work is understood). A fake planner would let the test choose the outcome it is supposed to be
/// measuring. So the doubles stop at <see cref="IAiClientService"/> — real <see cref="AgentPlanner"/>, real
/// <see cref="AgentRunOrchestrator"/>, real SQLite <see cref="AgentRunService"/>, real step rows — and the facts
/// below differ ONLY in what the model said on that one turn. Same harness shape as
/// <see cref="AgentPlannerGroundingTests"/> below the planner and <c>AgentRunOrchestratorTests</c> above it.
/// </para>
/// <para>
/// <b>These runs are created through <see cref="AgentRunService.CreateAsync"/>, not through
/// <c>HeadlessRunLauncher</c>, so 18 D1's LAYER 1 (G1's local pre-flight) is deliberately not in the path.</b>
/// That is the point: layer 2 has to hold for the goals layer 1 lets through, and a layer-1 refusal of
/// <c>"ggg"</c> must not be what makes this file pass.
/// </para>
/// </summary>
public sealed class GoalGroundingReproTests : IDisposable
{
    /// <summary>
    /// The observed repro's goal, verbatim (spec §0: typed <c>ggg</c>, clicked Run in background).
    /// </summary>
    private const string ThinGoal = "ggg";

    /// <summary>
    /// The model's question from the observed repro, verbatim. USER-DERIVED PAYLOAD: it is a test literal here
    /// and nothing in this file logs it. Under CLAUDE.md's privacy rule (and spec §4.6's closing note) the
    /// production path may only put text like this through <c>SensitiveDebug</c>.
    /// </summary>
    private const string ModelQuestion = "what do u mean with ggg?";

    /// <summary>
    /// The pause reason token for a PLAN-TIME decline (owner Q4: two tokens, <c>needs-goal</c> at plan time and
    /// <c>needs-input</c> mid-plan, because they are different resume behaviours). 18 G2 introduced it as
    /// <c>AgentRunOrchestrator.NeedsGoalReason</c>; 18 G3 extends the panel and Flow maps that read it. A literal
    /// here for the same reason <c>UnattendedApprovalParkTests</c> writes <c>"tool-approval"</c> as a literal:
    /// the test asserts the WIRE value a parked row carries, which is what the panel and the Flow surface read,
    /// and a test that referenced the constant could not catch the constant being changed.
    /// </summary>
    private const string NeedsGoalReason = "needs-goal";

    // ---- the emit_plan members 18 G2 added ------------------------------------------------------------
    //
    // The WIRE NAMES of the members that make declining sayable (implementer decision 1: the decline rides
    // emit_plan as an ADDED MEMBER — not prose, because prose is indistinguishable from the no-call case and
    // hits the firm retry; and not a second tool, because the plan prompt's own text demands "Call the emit_plan
    // tool exactly once").
    //
    // "Not a second tool" is about what ONE TURN OFFERS. A plan turn still offers exactly one tool named
    // emit_plan; the REPLAN turn offers a variant of that same tool with these two members absent
    // (AgentPlanner.EmitRevisedPlanTool), because layer 2 is a plan-time contract and the schema — not just the
    // prompt — is what the model reads. Per-turn scoping of one tool, never two tools on one turn.
    //
    // Written as STRINGS, not read off AgentPlanner's capture record, and that is deliberate rather than
    // laziness: these are what a provider actually sends, so a renamed schema member has to break here. G2
    // shipped with exactly these two names. Named as a pair because the question and the flag are one statement:
    // "I cannot ground this, and here is what I need to know".
    private const string DeclineMember = "cannotGround";
    private const string QuestionMember = "question";

    private readonly IAiClientService _ai = Substitute.For<IAiClientService>();
    private readonly ISettingsService _settingsService = Substitute.For<ISettingsService>();
    private readonly AppSettings _settings = new();

    private readonly string _dir;
    private readonly SqliteContext _ctx;
    private readonly AgentRunService _runs;
    private readonly AssistantChatService _chats;

    /// <summary>Plan turns the stub served. Non-vacuity: a fact that never reached the planner proves nothing.</summary>
    private int _planTurns;

    public GoalGroundingReproTests()
    {
        _settingsService.GetSettingsAsync().Returns(_ => Task.FromResult(_settings));
        // AssistantFilesFolder stays null so T2-17a's grounding digest is absent and the plan turn carries the
        // goal verbatim — this file is about the goal, and a directory listing in the prompt is noise here.
        _dir = Path.Combine(Path.GetTempPath(), "PiaGoalGrounding_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _ctx = new SqliteContext(Path.Combine(_dir, "history.db"));
        _runs = new AgentRunService(_ctx, NullLogger<AgentRunService>.Instance);
        _chats = new AssistantChatService(_ctx, _runs);
    }

    public void Dispose()
    {
        _runs.Dispose();
        _ctx.Dispose();
        try { Directory.Delete(_dir, true); } catch { /* temp dir */ }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Persona Persona() => new() { Id = Guid.NewGuid(), Name = "Pia", SystemPrompt = "you are Pia" };

    private static AiProvider Provider() => new()
    {
        Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI, SupportsToolCalling = true,
    };

    // ---- the harness ----------------------------------------------------------------------------------

    /// <summary>
    /// Executor double. Records what was dispatched and whether the R10 single-turn fallback was taken —
    /// the second is spec §8.2's subject and the reason a decline must not be routed through
    /// <c>PlanResult.Fallback</c> (§4.2: for an ungroundable goal the degrade is the WORST available branch,
    /// because it sends the thin goal as one ordinary chat turn and calls whatever comes back the result).
    /// </summary>
    private sealed class RecordingExecutor : IAgentTurnExecutor
    {
        public List<string> Executed { get; } = new();
        public bool FallbackCalled { get; private set; }
        public bool EndCalled { get; private set; }
        public bool PausedCalled { get; private set; }

        private static StepTurnResult Ok(string text) =>
            new(true, false, null, text, null, Guid.NewGuid(), Guid.NewGuid());

        public Task BeginRunAsync(AgentRun run, RunContext ctx, CancellationToken ct) => Task.CompletedTask;

        public Task<StepTurnResult> ExecuteStepAsync(AgentRun run, AgentStep step, RunContext ctx, CancellationToken ct)
        {
            Executed.Add(step.Intent ?? step.Title);
            return Task.FromResult(Ok("done"));
        }

        public Task<StepTurnResult> RunSingleTurnFallbackAsync(AgentRun run, RunContext ctx, CancellationToken ct)
        {
            FallbackCalled = true;
            return Task.FromResult(Ok("fallback"));
        }

        public Task EndRunAsync(AgentRun run, RunContext ctx, bool cancelled, bool failed, CancellationToken ct)
        {
            EndCalled = true;
            return Task.CompletedTask;
        }

        public Task OnPausedAsync(AgentRun run, RunContext ctx, CancellationToken ct)
        {
            PausedCalled = true; // the non-terminal park hook (guardrail 5) — NOT EndRunAsync
            return Task.CompletedTask;
        }
    }

    private async Task<AgentRun> NewPlannedRunAsync(string goal)
    {
        var chatId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await _chats.SaveAsync(new SyncAssistantChat
        {
            Id = chatId,
            SchemaVersion = 1,
            Title = "t",
            CreatedAt = now,
            UpdatedAt = now,
            LastAccessedAt = now,
            WindowMode = WindowMode.Assistant.ToString(),
            Messages = [],
        });
        // Planned + a null ParentRunId: the ROOT interactive/background shape the repro was observed in, and
        // the only shape a plan-time park is defined for (owner Q1 refuses a mid-plan ask for a delegated
        // child; a plan-time decline of a child's goal is 18 G5's territory, not this file's).
        return await _runs.CreateAsync(
            new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.User, Goal: goal), Ct);
    }

    private AgentPlanner Planner()
    {
        var handler = Substitute.For<IAiProviderHandler>();
        handler.ProviderType.Returns(AiProviderType.OpenAI);
        handler.DropsReasoningEffortWithTools.Returns(false);
        return new AgentPlanner(_ai, new AiProviderHandlerResolver([handler]), _settingsService,
            NullLogger<AgentPlanner>.Instance);
    }

    // chats: _chats (18 G3) — the REAL AssistantChatService, so SafePostClarificationQuestionAsync's
    // GetAsync/SaveMergedAsync round-trip is exercised for real, not stubbed away. Every other fact in this
    // file that never reaches the decline branch is unaffected; the ones that do now also post a chat
    // message, which is exactly what the two facts below this file's §8.1 test assert.
    private AgentRunOrchestrator Orchestrator() =>
        new(_runs, Planner(), new FakeVerifier(), NullLogger<AgentRunOrchestrator>.Instance, chats: _chats);

    /// <summary>
    /// One plan turn, as the provider would serve it: dispatch an <c>emit_plan</c> call with
    /// <paramref name="emitArgs"/> (null ⇒ the model called nothing), stream any visible prose, then finish.
    /// Same shape as <see cref="AgentPlannerGroundingTests"/>'s stream.
    /// </summary>
    private static async IAsyncEnumerable<ChatStreamItem> PlanStream(
        ToolCallHandler? handler, Dictionary<string, object?>? emitArgs, string? visible, UsageDetails? usage)
    {
        if (handler is not null && emitArgs is not null)
            await handler(new FunctionCallContent(Guid.NewGuid().ToString(), "emit_plan", emitArgs),
                new ToolDispatchContext(1));
        if (!string.IsNullOrEmpty(visible))
            yield return new TextDelta(visible);
        await Task.Yield();
        // The usage rides the Finished item, which is the ONLY place a provider reports it (I1) and therefore the
        // only way §8.3 below can observe the decline path billing anything at all.
        yield return new Finished(usage, "test-model");
    }

    /// <summary>Every plan turn (the first AND the firm retry) answers the same way.</summary>
    private void ProviderAlwaysAnswers(
        Dictionary<string, object?>? emitArgs, string? visible, UsageDetails? usage = null)
    {
        _ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _planTurns++;
                return PlanStream(ci.ArgAt<ToolCallHandler?>(3), emitArgs, visible, usage);
            });
    }

    /// <summary>
    /// The DECLINE turn: the model calls <c>emit_plan</c> exactly once, as the prompt demands, and uses it to
    /// say it cannot ground the goal instead of filling <c>steps</c>.
    /// <para>
    /// Before 18 G2 the two decline members were unknown to <c>EmitPlanArgs</c> and <c>PlanJson</c> is
    /// <c>JsonSerializerDefaults.Web</c>, which SKIPS unmapped members — so this deserialized to
    /// <c>Steps: null</c>, i.e. exactly the "no usable plan" the R10 degrade was written for. That equivalence
    /// was the defect; G2 broke it by binding the members, and the facts below are what hold the two apart.
    /// </para>
    /// </summary>
    private static Dictionary<string, object?> DeclineArgs() => new()
    {
        [DeclineMember] = true,
        [QuestionMember] = ModelQuestion,
        ["steps"] = null,
    };

    /// <summary>
    /// The FABRICATION turn: the model complies with a schema in which declining is unsayable (spec §1.2 —
    /// "the model faced with ggg is not misbehaving when it fabricates four steps. It is complying") and emits
    /// the four-step plan the repro observed.
    /// </summary>
    private static Dictionary<string, object?> FourStepArgs() => new()
    {
        ["steps"] = new object[]
        {
            Step("Clarify the request", "work out what ggg refers to"),
            Step("Gather context", "collect anything related to ggg"),
            Step("Draft the deliverable", "produce a first pass at ggg"),
            Step("Review and finish", "check the ggg deliverable over"),
        },
    };

    private static Dictionary<string, object?> Step(string title, string intent) => new()
    {
        ["title"] = title, ["intent"] = intent, ["expectedArtifact"] = null,
        ["personaKey"] = null, ["parallelGroup"] = null,
    };

    private async Task<AgentRun> RunToSettlementAsync(RecordingExecutor exec, string goal)
    {
        var run = await NewPlannedRunAsync(goal);
        await Orchestrator().RunAsync(run, exec, Persona(), Provider(), RunProfile.Interactive, Ct);
        var settled = await _runs.GetAsync(run.Id, Ct);
        // Asserted rather than dereferenced with `!`: a null row here is a harness fault (the FK, the chat
        // row, the create) and it must read as that, not as a NullReferenceException inside a fact about
        // planning. Spec §8.1's forward fact had to fail INFORMATIVELY before G2 landed, not blow up.
        Assert.NotNull(settled);
        return settled!;
    }

    // ---- §8.1, the forward assertion -------------------------------------------------------------------

    /// <summary>
    /// <b>SPEC §8.1 — the fact 18 G2 makes true.</b>
    /// <para>
    /// A run whose plan turn DECLINES to ground the goal settles PARKED at
    /// <see cref="AgentRunState.WaitingForInput"/> carrying the <c>needs-goal</c> reason token, with ZERO
    /// persisted step rows. Before G2 there was no decline path at all: the declined turn was indistinguishable
    /// from a turn that produced no plan, so <c>PlanResult.Fallback</c> came back, the R10 single-turn degrade
    /// ran the thin goal as one ordinary chat turn, and the run settled <c>Completed</c> — this assertion read
    /// <c>Expected: WaitingForInput / Actual: Completed</c>, which was the §8.1 negative stated as a diff.
    /// </para>
    /// <para>
    /// <b>Assertion ORDER is deliberate.</b> The state comes first because it is the assertion whose failure
    /// message names the defect. Zero step rows is asserted after it, and it passed even BEFORE G2 — the R10
    /// degrade records no steps either — so leading with it would produce a green-looking prefix and a confusing
    /// failure.
    /// </para>
    /// <para>
    /// <b>18 G3 landed "with the model's question attached" as the CHAT half of D5</b> — see
    /// <see cref="UngroundableGoal_PostsTheQuestionIntoTheRunsOwnChat"/> just below this fact, which is the
    /// dedicated test for it (the question is asserted there, not folded into this one, for the same reason
    /// §8.2 got its own dedicated fact: a failure here should still name the §8.1 defect, not the chat write).
    /// </para>
    /// <para>
    /// <b>18 G4 landed the OTHER half, and it is deliberately not asserted here.</b> What persists is the user's
    /// ANSWER, not the question: <c>AgentRuns.ClarificationsJson</c> now exists (forced, because
    /// <c>AgentRunService.TryBeginResumeAsync</c> and its sibling both <c>SET ExtraJson=NULL</c> on the resume
    /// claim, so anything kept in the pause envelope is destroyed by the very resume that carries the answer),
    /// and it is what a re-plan reads back. The QUESTION deliberately has no durable home outside the chat row
    /// above — implementer decision 4 kept a <c>ReadPauseQuestion</c> sibling off <c>RunPauseEnvelope</c> so every
    /// envelope member stays app-owned and loggable. The persistence and re-plan facts therefore live in
    /// <c>AgentRunClarificationResumeTests</c> (§8.4), not in this file, which is about the PARK. Note also that
    /// §8.6's sibling fact ("the question is never plain-logged") is NOT assertable from an END-STATE test,
    /// because <c>SensitiveDebug</c> is <c>[Conditional("DEBUG")]</c> and the suite runs Debug; it is owned by an
    /// Architecture source-scan test instead (owner Q7). <c>AgentPlannerTests</c> pins the LEVEL half of it —
    /// nothing at Information or above carries the question — which is the most a sink can honestly say.
    /// </para>
    /// </summary>
    [Fact]
    public async Task UngroundableGoal_ParksAtWaitingForInput_WithNeedsGoal_AndCreatesNoSteps()
    {
        ProviderAlwaysAnswers(DeclineArgs(), ModelQuestion);
        var exec = new RecordingExecutor();

        var settled = await RunToSettlementAsync(exec, ThinGoal);

        // Non-vacuity first: the plan turn really happened. Without this, a harness that never reached the
        // planner would make every assertion below a statement about nothing.
        Assert.True(_planTurns >= 1, $"the plan turn never ran (planTurns={_planTurns})");

        // THE FACT (§8.1).
        Assert.Equal(AgentRunState.WaitingForInput, settled.State);
        Assert.Equal(NeedsGoalReason, RunPauseEnvelope.ReadReason(settled));

        // "…and CREATES NO STEPS" — the persisted half, read back out of SQLite rather than off the plan the
        // planner returned, because a fabricated plan is only harmful once it is a row the drain loop will run.
        Assert.Empty(settled.Plan);
        Assert.Empty(exec.Executed);
        Assert.Null(await _runs.NextPendingStepAsync(settled.Id, Ct));

        // §8.2 restated on the end state, because this is the branch the decline used to be mistaken for and
        // therefore the most likely wrong fix: a decline must NOT be routed through PlanResult.Fallback into the
        // R10 degrade (§4.2 — for an ungroundable goal that is the worst available branch). The DEDICATED §8.2
        // fact below asserts the absence of the call itself, which is the assertion the spec asks for.
        Assert.False(exec.FallbackCalled);

        // A park is not a completion and not a failure: CompletedAt stays null (so the startup sweep's
        // `State < WaitingForInput` band leaves the row alone — owner Q6), the terminal EndRun bracket does
        // not fire, and the non-terminal OnPaused release hook does (guardrail 5).
        Assert.Null(settled.CompletedAt);
        Assert.False(exec.EndCalled);
        Assert.True(exec.PausedCalled);
    }

    // ---- 18 G3, D5's CHAT half: the question posted into the run's own chat ----------------------------

    /// <summary>
    /// <b>18 D5 — the chat half of "both surfaces".</b> The parked run's own chat (the stub row
    /// <c>NewPlannedRunAsync</c> creates, mirroring <c>HeadlessRunLauncher.cs:332,350-353</c>) gains exactly ONE
    /// new message once the plan turn declines: the model's own question, verbatim, posted as an
    /// <c>"assistant"</c> role message — never the card, per §4.4 (see
    /// <see cref="AgentRunNotificationSurfaceTests"/> for the card-side half of that rule).
    /// </summary>
    [Fact]
    public async Task UngroundableGoal_PostsTheQuestionIntoTheRunsOwnChat()
    {
        ProviderAlwaysAnswers(DeclineArgs(), ModelQuestion);
        var exec = new RecordingExecutor();

        var settled = await RunToSettlementAsync(exec, ThinGoal);

        var chat = await _chats.GetAsync(settled.ChatId, Ct);
        Assert.NotNull(chat);
        var posted = Assert.Single(chat!.Messages);
        Assert.Equal("assistant", posted.Role);
        Assert.Equal(ModelQuestion, posted.Content);
    }

    /// <summary>
    /// <b>The false-positive guard for the chat post.</b> <c>PlanResult.Decline</c> documents that a model may
    /// declare <c>cannotGround</c> true while wording no question at all, and that is STILL a decline (spec
    /// §4.2's whole point: the FLAG is the discriminator, not the text). This method's own doc says a blank
    /// question is a no-op rather than a fabricated placeholder — asserted here as the negative half, the same
    /// way <see cref="TodaysBehaviour_AFabricatedPlanForAThinGoal_IsPersistedAndExecutedToCompletion"/> is the
    /// negative half of §8.1: the park still happens (asserted first, so a harness fault reads as itself and
    /// not as a false green on the chat assertion), but nothing is invented for the chat to say.
    /// </summary>
    [Fact]
    public async Task UngroundableGoal_DeclinedWithNoQuestionWorded_PostsNothingToTheChat()
    {
        var unwordedDecline = new Dictionary<string, object?> { [DeclineMember] = true, ["steps"] = null };
        ProviderAlwaysAnswers(unwordedDecline, visible: null);
        var exec = new RecordingExecutor();

        var settled = await RunToSettlementAsync(exec, ThinGoal);

        Assert.Equal(AgentRunState.WaitingForInput, settled.State); // non-vacuity: it really parked
        Assert.Equal(NeedsGoalReason, RunPauseEnvelope.ReadReason(settled));
        var chat = await _chats.GetAsync(settled.ChatId, Ct);
        Assert.NotNull(chat);
        Assert.Empty(chat!.Messages);
    }

    // ---- the negative half, made concrete --------------------------------------------------------------

    /// <summary>
    /// <b>GREEN before 18 G2 and still green after it.</b> Spec §8.1's negative half: the thin goal
    /// <c>ggg</c> plus a model that fabricates a four-step plan ⇒ four persisted step rows, every one of them
    /// dispatched, the run <c>Completed</c>. This is the behaviour the repro observed, written down so the
    /// change G2 makes is visible in the diff rather than only in prose.
    /// <para>
    /// It has a second job, and now that G2 has landed that is its main one: it is the FALSE-POSITIVE guard for
    /// layer 2. G2
    /// makes declining SAYABLE; it must not make it automatic. A model that emits a usable plan still gets one,
    /// however thin the goal was — which is the same shape as G1's own false-positive fact (§7: "a layer 1 that
    /// refuses real goals is worse than no layer 1, because the user has no recourse"). If this fact ever reds,
    /// the gate started refusing goals the model was willing to plan.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TodaysBehaviour_AFabricatedPlanForAThinGoal_IsPersistedAndExecutedToCompletion()
    {
        ProviderAlwaysAnswers(FourStepArgs(), visible: null);
        var exec = new RecordingExecutor();

        var settled = await RunToSettlementAsync(exec, ThinGoal);

        Assert.Equal(1, _planTurns); // one turn: the model called emit_plan, so no firm retry
        Assert.Equal(AgentRunState.Completed, settled.State);

        // The steps are REAL rows, in order, and every one of them ran.
        Assert.Equal(4, settled.Plan.Count);
        Assert.All(settled.Plan, s => Assert.Equal(AgentStepStatus.Done, s.Status));
        Assert.Equal(
            new[] { "Clarify the request", "Gather context", "Draft the deliverable", "Review and finish" },
            settled.Plan.OrderBy(s => s.Ordinal).Select(s => s.Title).ToArray());
        Assert.Equal(4, exec.Executed.Count);

        // Not the degrade path either — this run genuinely planned and genuinely executed a plan.
        Assert.False(exec.FallbackCalled);
        Assert.Null(RunPauseEnvelope.ReadReason(settled)); // never parked
    }

    // ---- §8.2 and §8.3, the two facts 18 G2 owns outright ----------------------------------------------

    /// <summary>
    /// <b>SPEC §8.2 — the single most important NEGATIVE fact in the group.</b> <i>"A decline never calls
    /// RunSingleTurnFallbackAsync. Assert on the absence of that call, not on the run's end state — the two
    /// branches can produce similar-looking end states (§4.2)."</i>
    /// <para>
    /// This is the INVERSION of the fact that used to sit here pinning the defect (a decline mistaken for
    /// silence, degrading to the R10 chat turn), inverted in the commit that landed G2 exactly as its own doc
    /// said it must be. The three assertions are what changed, and each is one implementer decision:
    /// </para>
    /// <list type="bullet">
    /// <item>ONE plan turn, not two — implementer decision 2: a decline short-circuits the firm retry, because
    /// that retry's text ("You did not call emit_plan…") exists for SILENCE and a declining model DID call the
    /// tool. Two turns here would mean the retry is being used to re-ask a model that already answered, which is
    /// §4.2's "bully a declining model into fabricating".</item>
    /// <item><c>FallbackCalled</c> false — the §8.2 assertion proper, on the CALL and not on the state.</item>
    /// <item>parked with the token — the decline reached the park instead of a terminal settle.</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task ADeclinedPlanTurn_TakesOneTurn_AndNeverCallsTheSingleTurnFallback()
    {
        ProviderAlwaysAnswers(DeclineArgs(), ModelQuestion);
        var exec = new RecordingExecutor();

        var settled = await RunToSettlementAsync(exec, ThinGoal);

        Assert.Equal(1, _planTurns);       // no firm retry was burned on a model that HAD called emit_plan
        Assert.False(exec.FallbackCalled); // §8.2: the ABSENCE of the call, which is the fact the spec asks for
        Assert.Empty(settled.Plan);
        Assert.Equal(AgentRunState.WaitingForInput, settled.State);
        Assert.Equal(NeedsGoalReason, RunPauseEnvelope.ReadReason(settled));
    }

    /// <summary>
    /// <b>The POSITIVE CONTROL for §8.2, in the same file and on the same double.</b> A negative assertion is
    /// worth exactly what its control is worth: <c>Assert.False(exec.FallbackCalled)</c> above would pass just as
    /// happily on a <c>RecordingExecutor</c> whose <c>RunSingleTurnFallbackAsync</c> stopped setting the flag, or
    /// on a harness that never reached the orchestrator at all. So this drives the turn the R10 degrade was
    /// actually written for — the model called NOTHING — and asserts the same flag on the same executor becomes
    /// TRUE. Without it, §8.2's fact lives in this file and its non-vacuity lives in another one
    /// (<c>AgentRunOrchestratorTests.Run_PlannerFallback_RunsSingleTurn_Completed</c>).
    /// <para>
    /// It also states the contrast that makes implementer decision 2 legible: SILENCE costs two plan turns —
    /// the firm retry is precisely what it exists for — where a decline costs one. The two runs differ in
    /// nothing but what the plan turn said.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ASilentPlanTurn_StillDegrades_AndDoesCallTheSingleTurnFallback()
    {
        ProviderAlwaysAnswers(emitArgs: null, visible: "I am not sure what you want here."); // called nothing
        var exec = new RecordingExecutor();

        var settled = await RunToSettlementAsync(exec, ThinGoal);

        Assert.Equal(2, _planTurns);      // the firm retry — the turn a decline must NOT burn
        Assert.True(exec.FallbackCalled); // the control: this double can record the call it is asked about above
        Assert.Empty(settled.Plan);       // the degrade records no steps either, which is why §8.2 is not a
                                          // statement about step rows
        // And silence is not a decline: no park, no token. The FLAG holds the two branches apart, not the end
        // state and not the empty plan (§4.2 — "the two branches can produce similar-looking end states").
        Assert.Equal(AgentRunState.Completed, settled.State);
        Assert.Null(RunPauseEnvelope.ReadReason(settled));
        Assert.False(exec.PausedCalled);
    }

    /// <summary>
    /// <b>SPEC §8.3 — plan-turn usage is accrued on the decline path</b>, matching I1's treatment of every other
    /// plan outcome. Read off the PERSISTED run ledger, not off <c>PlanResult.Usage</c>: the planner-level half is
    /// pinned in <c>AgentPlannerTests</c>, and what this file adds is that the orchestrator's decline branch sits
    /// AFTER <c>SafeAddUsage</c> (the I1 accrual at <c>AgentRunOrchestrator.cs:196-198</c>) rather than returning
    /// in front of it. A branch placed one line earlier would pass every other fact in this file and bill the
    /// decline as zero tokens.
    /// </summary>
    [Fact]
    public async Task DeclinePath_AccruesThePlanTurnUsage_ToTheRunLedger()
    {
        ProviderAlwaysAnswers(DeclineArgs(), ModelQuestion,
            new UsageDetails { InputTokenCount = 31, OutputTokenCount = 7 });
        var exec = new RecordingExecutor();

        var settled = await RunToSettlementAsync(exec, ThinGoal);

        Assert.Equal(AgentRunState.WaitingForInput, settled.State); // non-vacuity: it really took the decline
        Assert.NotNull(settled.LedgerJson);
        using var doc = JsonDocument.Parse(settled.LedgerJson!);
        var root = doc.RootElement;
        Assert.Equal(31, root.GetProperty("inputTokens").GetInt64());
        Assert.Equal(7, root.GetProperty("outputTokens").GetInt64());
        // Planning is RUN-level spend (stepId: null), so it never opens a per-step entry — and on this path
        // there is no step row it could have opened one against.
        Assert.Equal(0, root.GetProperty("perStep").GetArrayLength());
    }
}
