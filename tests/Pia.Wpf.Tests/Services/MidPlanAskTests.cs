using System.IO;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// <b>Batch 18 G5 — THE MID-PLAN ASK (18 D3/D4/D6, owner Q1/Q5), driven end to end.</b> A step that is genuinely
/// blocked on something only a person can settle now has a channel: <c>request_user_input</c> parks the run at
/// <c>WaitingForInput</c> with the <c>needs-input</c> token and puts the question in the run's own chat.
/// <para>
/// <b>Why a new tool and not a third <c>emit_step_result</c> outcome (18 D6).</b> Spec §2: the tool's own
/// description ALREADY tells the model that explaining a failure in prose is not a failure report, and the model
/// in the repro wrote prose anyway. The failure mode is the ABSENCE of a call, so another enum member on a tool
/// nobody called would have changed nothing. Every fact below is about a DIFFERENTLY SHAPED channel; none of them
/// touches the outcome bool, and <c>StepOutcomeSignalTests</c> / <c>HeadlessStepOutcomeSignalTests</c> stay green
/// unchanged, which is the negative half of D6.
/// </para>
/// <para>
/// <b>NO CAP is asserted anywhere, deliberately.</b> 18 D4 is "model declares, no cap" — the owner was shown the
/// stall risk (spec §5: an unattended run can be stalled indefinitely, one question at a time) and chose it. What
/// this file pins instead is that the tool's DESCRIPTION carries the weight and that repeat asks are COUNTED, so a
/// cap can later be a measured follow-up. Counting is not capping.
/// </para>
/// <para>
/// Real everything below the AI client — real SQLite run + chat stores, real <c>HeadlessRunLauncher</c>,
/// <c>AgentRunOrchestrator</c>, <c>HeadlessTurnExecutor</c> and <c>BackgroundAssistantTurnRunner</c>. Only the
/// provider stream, the plugin route and the planner are doubles; the park is a decision made between those and
/// nothing else would be exercised by faking the layers in between. The harness is
/// <c>UnattendedApprovalParkTests</c>' verbatim, because this park reuses that park's machinery and a divergent
/// fixture would be measuring a different thing.
/// </para>
/// <para>
/// net10.0-windows cannot execute on macOS — these tests are written, not run; execution is deferred to
/// Windows/CI.
/// </para>
/// </summary>
public sealed class MidPlanAskTests : IDisposable
{
    /// <summary>The mid-plan park token as a LITERAL, matching <c>AgentRunClarificationResumeTests</c>'
    /// discipline: this fact is about the WIRE value a parked row carries and a resume reads back off it, so a
    /// test that referenced <c>AgentRunOrchestrator.NeedsInputReason</c> could not catch the constant changing.</summary>
    private const string NeedsInputReason = "needs-input";

    /// <summary>The model's question. USER-DERIVED PAYLOAD: a literal here, and nothing in this file logs it —
    /// production may only put text like this through <c>SensitiveDebug</c> (CLAUDE.md).</summary>
    private const string TheQuestion = "Which cluster should I deploy to — staging or production?";

    private readonly string _dir;
    private readonly string _runsBase;
    private readonly SqliteContext _ctx;
    private readonly AgentRunService _runs;
    private readonly AssistantChatService _chats;
    private readonly ExecutingRunStore _executing = new();
    private readonly RecordingTimelineService _timeline = new();

    public MidPlanAskTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PiaMidPlanAsk_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _ctx = new SqliteContext(Path.Combine(_dir, "history.db"));
        _runs = new AgentRunService(_ctx, NullLogger<AgentRunService>.Instance);
        _chats = new AssistantChatService(_ctx, _runs);
        _runsBase = Path.Combine(_dir, "runs");
        Directory.CreateDirectory(_runsBase);
    }

    public void Dispose()
    {
        _runs.Dispose();
        _ctx.Dispose();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    // ================================================================= acceptance fact 1: the park

    /// <summary>
    /// <b>THE HEADLINE (spec §8, fact 1).</b> A step turn that calls <c>request_user_input</c> parks the run at
    /// <c>WaitingForInput</c> with the <c>needs-input</c> token, and the QUESTION reaches the run's own chat.
    /// <para>
    /// The step is back at <c>Pending</c>, not recorded Failed — that is the difference between "blocked, waiting"
    /// and "failed, replan", and getting it wrong would burn a replan on a step that is only waiting. The run is
    /// NOT terminal (<c>CompletedAt</c> stays null), which is what keeps the startup sweep and the scheduled-job
    /// striker from booking it as a finished run.
    /// </para>
    /// <para>
    /// <b>Neutralize:</b> delete the <c>r.UserInputQuestion</c> branch in <c>AgentRunOrchestrator</c>'s drain loop
    /// → the step records normally and the run settles Completed, and every assertion here reds. Note that the
    /// model's text still flows in both worlds (the fake stream always yields a reply), so nothing here can pass
    /// on "the step produced nothing".
    /// </para>
    /// </summary>
    [Fact]
    public async Task AStepThatCallsRequestUserInput_ParksTheRunNeedsInput_AndTheQuestionReachesTheChat()
    {
        var probe = new AskProbe();
        var launcher = Build(probe);

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("ship it", AgentRunTrigger.Schedule, GrantedWrites: []),
            TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        var run = await GetRunAsync(handle.RunId);
        Assert.Equal(AgentRunState.WaitingForInput, run.State);
        Assert.Equal(NeedsInputReason, PauseMember(run, "reason"));
        // The envelope carries the TOKEN ONLY (implementer decision 4): no question member, so every member it
        // holds stays app-owned and loggable, exactly as RunPauseEnvelope's own doc licenses.
        Assert.Null(PauseMember(run, "question"));
        Assert.Null(PauseMember(run, "tool"));
        Assert.Null(run.CompletedAt);

        // The step is resumable, not consumed: a Continue must find it again, or the resume would drain an empty
        // remainder and settle the run Completed with the work never done.
        var pending = await _runs.NextPendingStepAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(pending);
        Assert.Equal(AgentStepStatus.Pending, pending!.Status);

        // 18 D5's CHAT half. §4.4 forbids the question on the Flow card, so the chat is the only surface that may
        // carry it — and a headless run already owns a real chat row for exactly this.
        var chat = await _chats.GetAsync(run.ChatId, TestContext.Current.CancellationToken);
        Assert.NotNull(chat);
        Assert.Contains(chat!.Messages, m => m.Content == TheQuestion);

        // The model was told, and told to stop — not left to guess whether the ask landed.
        Assert.Equal(UserInputRequestStore.Accepted, probe.AskResults.Single());
        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// The interception is PRE-ROUTE (owner Q5): <c>RouteToolCallAsync</c> never sees <c>request_user_input</c>,
    /// so no <c>ToolGateDecision.UnknownTool</c> audit row is written for it. Without the short-circuit the model
    /// would get "Unknown tool.", the run would never park, AND the timeline would carry a row telling the user
    /// their run called a tool that does not exist — the same three-part failure the <c>emit_step_result</c> seam
    /// exists to avoid, which is why that seam's argument is cited rather than re-derived.
    /// </summary>
    [Fact]
    public async Task TheAskIsInterceptedBeforeRouting_SoNoUnknownToolRowIsWritten()
    {
        var probe = new AskProbe();
        var launcher = Build(probe);

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("ship it", AgentRunTrigger.Schedule, GrantedWrites: []),
            TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        Assert.DoesNotContain(AgentStepTools.RequestUserInputToolName, probe.RoutedNames);
        Assert.DoesNotContain(_timeline.Rows, r => r.ToolName == AgentStepTools.RequestUserInputToolName);
        Assert.DoesNotContain(_timeline.Rows, r => r.Decision == ToolGateDecision.UnknownTool);
        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// The step turn really is OFFERED the tool, on the same list that carries <c>emit_step_result</c> — the
    /// per-executor choke point owner Q5 chose over <c>AssistantPromptComposer.PrepareTurn</c>. Non-vacuous
    /// against the fact above: a run could park because the fake stream calls the name regardless of whether the
    /// tool was offered, so "the model parked" does not prove "the model was allowed to".
    /// </summary>
    [Fact]
    public async Task AStepTurnIsOfferedBothStepTools()
    {
        var probe = new AskProbe();
        var launcher = Build(probe);

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("ship it", AgentRunTrigger.Schedule, GrantedWrites: []),
            TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        Assert.True(AgentStepTools.OffersRequestUserInputTool(probe.OfferedTools));
        Assert.True(AgentStepTools.OffersStepResultTool(probe.OfferedTools),
            "18 D6: the ask is an ADDITIONAL channel, never a replacement for the declaration tool");
        await launcher.StopAsync(CancellationToken.None);
    }

    // ================================================================= acceptance fact 3: the delegated refusal

    /// <summary>
    /// <b>OWNER Q1 (spec §8, fact 3): the tool is REFUSED on a DELEGATED step</b> — a run with a non-null
    /// <c>ParentRunId</c>. It is not offered, the call is still intercepted (so no "Unknown tool." dead end), the
    /// model is handed a redirect, and the child does NOT park.
    /// <para>
    /// <b>Why refusal is the right answer here and not merely convenient.</b>
    /// <c>AgentRunNotificationSurface.cs:170-171</c> filters child runs out of the Flow publish, because a
    /// Continue card carrying the CHILD's run id is "a transition nothing supports". A child that asked would sit
    /// at <c>WaitingForInput</c> with no card, and its parent would re-park behind it under
    /// <c>ChildrenParkedReason</c> — a run stuck on a question nobody was asked. Note this is the PRECEDENT'S
    /// SUPPORTING reason: <c>HeadlessRunLauncher.CanParkForApproval</c>'s primary one (a park ACQUIRES authority,
    /// so a delegate would end up wider than its delegator) does not transfer, because a question grants nothing.
    /// </para>
    /// <para>
    /// <b>Neutralize:</b> make <c>AgentStepTools.CanRequestUserInput</c> return true unconditionally → the child
    /// parks <c>needs-input</c> and the state assertions red.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ADelegatedStep_IsRefusedTheAsk_AndTheRunDoesNotPark()
    {
        var probe = new AskProbe();
        var launcher = Build(probe);

        var parent = await NewRunAsync();
        var child = await ParkedChildAsync(parent.Id);
        Assert.True(await launcher.ResumeAsync(child.Id, ct: TestContext.Current.CancellationToken));
        await AwaitSettledAsync(child.Id);

        var run = await GetRunAsync(child.Id);
        // The claim is "did not park", asserted on the STATE and the envelope rather than on "no question in the
        // chat" — a park that failed to post would also leave the chat empty, and the two are different bugs.
        Assert.NotEqual(AgentRunState.WaitingForInput, run.State);
        Assert.Null(PauseMember(run, "reason"));

        // Not offered…
        Assert.False(AgentStepTools.OffersRequestUserInputTool(probe.OfferedTools));
        // …but still INTERCEPTED, so the model gets a usable answer instead of "Unknown tool." and no
        // UnknownTool audit row is written for a tool this build knows perfectly well.
        Assert.Equal(UserInputRequestStore.RefusedForDelegatedStep, probe.AskResults.Single());
        Assert.DoesNotContain(_timeline.Rows, r => r.Decision == ToolGateDecision.UnknownTool);
        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// <b>The other half of fact 3: refusing the ask must not SWALLOW the block.</b> The same delegated step
    /// declares <c>succeeded=false</c> through <c>emit_step_result</c> — which is exactly what
    /// <see cref="UserInputRequestStore.RefusedForDelegatedStep"/> tells it to do — and that lands as a real
    /// declared step failure carrying the model's own reason.
    /// <para>
    /// This is deliberately measured on the CHILD's own row rather than by standing up a fan-out: what a parent
    /// does with a failed child is <c>AgentRunOrchestratorFanOutTests.AFailedChildFeedsTheOrdinaryReplanPath</c>'s
    /// fact and has been tested since Batch 07. Duplicating it here would re-measure someone else's path; what G5
    /// owes is proof that its refusal leaves that path REACHABLE, which is the step status and the failure reason
    /// below.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ARefusedDelegatedStep_StillSurfacesItsBlockAsADeclaredStepFailure()
    {
        var probe = new AskProbe { DeclareFailureAfterAsk = "blocked: I need the target cluster and cannot ask" };
        var launcher = Build(probe);

        var parent = await NewRunAsync();
        var child = await ParkedChildAsync(parent.Id);
        Assert.True(await launcher.ResumeAsync(child.Id, ct: TestContext.Current.CancellationToken));
        await AwaitSettledAsync(child.Id);

        var run = await GetRunAsync(child.Id);
        Assert.Equal(AgentRunState.Failed, run.State);
        // The model's OWN words reached the run's failure reason — the channel the refusal pointed it at really
        // carries the block, rather than the block dying inside a tool result nobody reads. Read off the raw
        // envelope, where AgentRunService.FailAsync serializes it as `{"error":…}`.
        Assert.Contains("target cluster", PauseMember(run, "error") ?? string.Empty, StringComparison.Ordinal);
        await launcher.StopAsync(CancellationToken.None);
    }

    // ================================================================= containment and repeat asks

    /// <summary>
    /// <b>AN ASK STOPS THE EXCHANGE, it does not merely advise it.</b> A granted, side-effecting call the model
    /// makes AFTER the ask does not run. hermes #16 wrote this guard for the approval park and its argument
    /// transfers verbatim: <c>AiClientService</c> walks the remaining calls of the same round and then continues
    /// to the next round, so a string asking the model to stop is not a control-flow construct.
    /// <para>
    /// It is an AT-MOST-ONCE fact, not a tidiness one. The asking step is abandoned and re-runs from the top on
    /// resume, so a write executed after the ask would be performed TWICE for one planned step.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AGrantedWriteAfterTheAsk_DoesNotRun()
    {
        var probe = new AskProbe { FollowUpTool = "write_file" };
        var launcher = Build(probe);

        var handle = await launcher.LaunchAsync(
            // GRANTED, so nothing but the containment guard can stop it: without the guard this call auto-runs.
            new HeadlessRunRequest("ship it", AgentRunTrigger.Schedule, GrantedWrites: ["write_file"]),
            TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        Assert.Empty(probe.ExecutedNames);
        Assert.Equal(AgentRunState.WaitingForInput, (await GetRunAsync(handle.RunId)).State);
        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// A SECOND ask in the same step does not move the question. The park carries one question and that question
    /// is what the person reads, so it must be the one that actually stopped the run — a later call is one the
    /// model made after being told the run was parking. (Same first-wins rule, and the same reasoning,
    /// <c>ToolApprovalStore.PendingToolName</c> uses.)
    /// </summary>
    [Fact]
    public async Task ASecondAskInTheSameStep_DoesNotMoveTheQuestion()
    {
        var probe = new AskProbe { SecondQuestion = "actually, which region?" };
        var launcher = Build(probe);

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("ship it", AgentRunTrigger.Schedule, GrantedWrites: []),
            TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        var run = await GetRunAsync(handle.RunId);
        var chat = await _chats.GetAsync(run.ChatId, TestContext.Current.CancellationToken);
        Assert.Contains(chat!.Messages, m => m.Content == TheQuestion);
        Assert.DoesNotContain(chat.Messages, m => m.Content == "actually, which region?");
        Assert.Equal(UserInputRequestStore.AlreadyAsked, probe.AskResults[1]);
        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// <b>18 D4 as a POSITIVE fact, in the one place it could be misread as a defect.</b> A run that already
    /// parked once, was answered, and asks AGAIN parks again — there is no per-run limit anywhere, by owner
    /// decision. The second question reaches the chat beside the first, and the user's first answer is preserved
    /// (18 G4's <c>ClarificationsJson</c>), so a second park is a continuation rather than a reset.
    /// <para>
    /// Written so that ADDING a cap would red it. Spec §5 records the stall risk this accepts; it is the
    /// decision, not an oversight, and this test is where a future implementer will find that out.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ARunMayParkToAskMoreThanOnce_ThereIsNoCap()
    {
        var probe = new AskProbe();
        var launcher = Build(probe);

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("ship it", AgentRunTrigger.Schedule, GrantedWrites: []),
            TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.WaitingForInput, (await GetRunAsync(handle.RunId)).State);

        // The user answers; the step re-runs and the model asks a different question.
        probe.NextQuestion = "and which branch?";
        Assert.True(await launcher.ResumeAsync(
            handle.RunId, "the staging cluster", TestContext.Current.CancellationToken));
        await AwaitParkedAsync(handle.RunId);

        var run = await GetRunAsync(handle.RunId);
        Assert.Equal(AgentRunState.WaitingForInput, run.State);
        Assert.Equal(NeedsInputReason, PauseMember(run, "reason"));

        // Polled rather than read once: the park writes the ROW before it posts the question (SafePause →
        // SafeOnPaused → the chat write), and a resume hands back no completion task to await, so reading the
        // chat the instant the row parks is a real race.
        await AwaitChatMessageAsync(run.ChatId, "and which branch?");
        var chat = await _chats.GetAsync(run.ChatId, TestContext.Current.CancellationToken);
        Assert.Contains(chat!.Messages, m => m.Content == TheQuestion);

        // The FIRST answer survived the second park — 18 G4's dedicated column, not ExtraJson (which the resume
        // claim NULLs). Without it the model would be re-grounded from scratch on every park.
        Assert.Contains("staging cluster", ClarificationsJson(run.Id) ?? string.Empty, StringComparison.Ordinal);
        await launcher.StopAsync(CancellationToken.None);
    }

    // ================================================================= the fault pair (both directions)

    /// <summary>
    /// <b>AN ASK SURVIVES A FAULT THAT HAPPENS AFTER IT</b> (<c>HeadlessTurnExecutor</c>'s catch arm). The step
    /// asks and the provider stream then throws later in the same exchange: the run PARKS with the question
    /// rather than failing, because the attempt is discarded either way and the question is the only thing it
    /// durably produced. hermes #16 built this containment for the approval park; G5 gives the ask the same one.
    /// <para>
    /// The fault is not laundered into a success and it is not lost: the run is non-terminal
    /// (<c>CompletedAt</c> null), the pause envelope carries the token and nothing else, and the exception
    /// itself is on the executor's <c>LogError</c> one line above the arm — this park has no <c>error</c> member
    /// precisely because it is a park, and the orchestrator's ask branch returns before anything reads
    /// <c>StepTurnResult.Error</c>.
    /// </para>
    /// <para>
    /// <b>Neutralize:</b> delete the arm (the reviewer's "one <c>if</c> to delete") → the step returns
    /// <c>ex.Message</c>, the run fails, and the person never sees the question their run stopped on. The next
    /// test is the other half of the pair and is what stops "keep the ask" from meaning "bury the fault".
    /// </para>
    /// </summary>
    [Fact]
    public async Task AFaultAfterTheAsk_KeepsTheQuestion_AndParksInsteadOfFailing()
    {
        var probe = new AskProbe { FaultMessage = "provider connection reset" };
        var launcher = Build(probe);

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("ship it", AgentRunTrigger.Schedule, GrantedWrites: []),
            TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        var run = await GetRunAsync(handle.RunId);
        Assert.Equal(AgentRunState.WaitingForInput, run.State);
        Assert.Equal(NeedsInputReason, PauseMember(run, "reason"));
        Assert.Null(run.CompletedAt);
        Assert.Null(PauseMember(run, "error")); // a park, not a failure — FailAsync is what writes that member

        var chat = await _chats.GetAsync(run.ChatId, TestContext.Current.CancellationToken);
        Assert.Contains(chat!.Messages, m => m.Content == TheQuestion);
        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// <b>THE ESCAPE HATCH the arm above rests on, pinned so nobody has to take it on trust.</b> A persistent
    /// post-ask fault cannot loop forever, because the arm fires only when THIS attempt asked — and the resumed
    /// attempt is not the same attempt: <c>ResumeAsync</c> passes the user's typed answer down as the dispatch
    /// nudge and <c>ExecuteStepAsync</c> fences it into the step instruction, so a model that has been answered
    /// has no reason to ask again. This fixture models exactly that turn (answered, silent, same fault) and the
    /// run FAILS with the provider's own message.
    /// <para>
    /// It is the difference from hermes #16, whose hatch is structural (the resume GRANTS the tool, so the next
    /// attempt cannot park on it). An answer grants nothing, so the hatch here is the arm's condition instead —
    /// which is why it is worth a test rather than a comment. What remains uncovered by design is a model that
    /// re-asks a question it was already answered; that stalls a run identically WITH NO FAULT AT ALL and is 18
    /// D4's accepted risk (spec §5), not a defect of this arm.
    /// </para>
    /// <para>
    /// <b>Neutralize:</b> make the fault arm unconditional (drop <c>userInput?.Question is not null</c>, keeping
    /// the last question the store ever held) → the resumed step parks again instead of failing and this reds.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AnAnsweredStepThatFaultsWithoutAskingAgain_FailsWithTheProviderError()
    {
        var probe = new AskProbe { FaultMessage = "provider connection reset" };
        var launcher = Build(probe);

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("ship it", AgentRunTrigger.Schedule, GrantedWrites: []),
            TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.WaitingForInput, (await GetRunAsync(handle.RunId)).State);

        // The user answers. The re-run step now carries that answer in its instruction, so the model stops
        // asking — while the fault stays exactly as deterministic as it was.
        probe.Ask = false;
        Assert.True(await launcher.ResumeAsync(
            handle.RunId, "the staging cluster", TestContext.Current.CancellationToken));
        await AwaitSettledAsync(handle.RunId);

        var run = await GetRunAsync(handle.RunId);
        Assert.Equal(AgentRunState.Failed, run.State);
        // Verbatim, off the raw envelope: `ex.Message` reached the step result, the replan degraded, and
        // SafeFail wrote it. The fault is reported to the user rather than buried under a repeated question.
        Assert.Equal("provider connection reset", PauseMember(run, "error"));
        await launcher.StopAsync(CancellationToken.None);
    }

    // ---------------------------------------------------------------- helpers

    private async Task<AgentRun> GetRunAsync(Guid runId)
        => (await _runs.GetAsync(runId, TestContext.Current.CancellationToken))!;

    /// <summary>Poll to a terminal state. A mid-plan ask park is NOT terminal, so this also proves non-parking.</summary>
    private async Task AwaitSettledAsync(Guid runId)
    {
        var ct = TestContext.Current.CancellationToken;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var s = (await _runs.GetAsync(runId, ct))!.State;
            if (s is AgentRunState.Completed or AgentRunState.Failed or AgentRunState.Cancelled)
                return;
            await Task.Delay(20, ct);
        }

        Assert.Fail($"Run {runId} never settled (state {(await _runs.GetAsync(runId, ct))!.State}).");
    }

    /// <summary>Poll to a PARK. A resume returns as soon as the dispatch is attached, not when it re-parks.</summary>
    private async Task AwaitParkedAsync(Guid runId)
    {
        var ct = TestContext.Current.CancellationToken;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var run = (await _runs.GetAsync(runId, ct))!;
            if (run.State == AgentRunState.WaitingForInput && PauseMember(run, "reason") == NeedsInputReason)
                return;
            if (run.State is AgentRunState.Completed or AgentRunState.Failed or AgentRunState.Cancelled)
                Assert.Fail($"Run {runId} settled {run.State} instead of parking to ask again.");
            await Task.Delay(20, ct);
        }

        Assert.Fail($"Run {runId} never re-parked (state {(await _runs.GetAsync(runId, ct))!.State}).");
    }

    /// <summary>Poll until the run's chat carries <paramref name="content"/>. Needed only where no completion
    /// task is available to await (a resume returns a bool), because the park writes the run row BEFORE it posts
    /// the question.</summary>
    private async Task AwaitChatMessageAsync(Guid chatId, string content)
    {
        var ct = TestContext.Current.CancellationToken;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var chat = await _chats.GetAsync(chatId, ct);
            if (chat is not null && chat.Messages.Any(m => m.Content == content))
                return;
            await Task.Delay(20, ct);
        }

        Assert.Fail($"Chat {chatId} never received the expected message.");
    }

    /// <summary>A member of the pause envelope, read from the raw row (<c>RunPauseEnvelope</c> is src-internal).</summary>
    private static string? PauseMember(AgentRun run, string member)
    {
        if (string.IsNullOrEmpty(run.ExtraJson)) return null;
        using var doc = JsonDocument.Parse(run.ExtraJson);
        return doc.RootElement.TryGetProperty(member, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
    }

    /// <summary>The run's accumulated clarification answers, straight off the column (18 G4).</summary>
    private string? ClarificationsJson(Guid runId)
    {
        using var cmd = _ctx.GetConnection().CreateCommand();
        cmd.CommandText = "SELECT ClarificationsJson FROM AgentRuns WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", runId.ToString());
        return cmd.ExecuteScalar() as string;
    }

    private async Task<AgentRun> NewRunAsync(Guid? parentRunId = null, string? policyJson = null)
    {
        var ct = TestContext.Current.CancellationToken;
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
        }, ct);

        return await _runs.CreateAsync(
            new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.Schedule, Goal: "g",
                PolicyJson: policyJson, ParentRunId: parentRunId), ct);
    }

    /// <summary>A parked CHILD run with one Pending step and an EMPTY grant envelope, ready to be resumed.</summary>
    private async Task<AgentRun> ParkedChildAsync(Guid parentRunId)
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await NewRunAsync(
            parentRunId, HeadlessRunLauncher.SerializeGrantEnvelope([], AgentRunTrigger.Schedule));
        await _runs.ReplaceStepsAsync(run.Id, [new AgentStep
        {
            Id = Guid.NewGuid(),
            RunId = run.Id,
            Ordinal = 0,
            Title = "S1",
            Intent = "do it",
            Status = AgentStepStatus.Pending,
        }], ct);
        await _runs.PauseAsync(run.Id, "step-cap", ct);
        return run;
    }

    // ---------------------------------------------------------------- doubles

    /// <summary>
    /// Drives one step turn's tool calls and records what came back. Every knob is off by default, so the
    /// headline fact runs the simplest possible shape: one <c>request_user_input</c> call and a reply.
    /// </summary>
    private sealed class AskProbe
    {
        /// <summary>The question the NEXT step turn asks. Settable so a second park can ask something different
        /// and the two can be told apart in the chat.</summary>
        public string NextQuestion { get; set; } = TheQuestion;

        /// <summary>A SECOND ask in the same turn — the first-wins fact. Null ⇒ one ask.</summary>
        public string? SecondQuestion { get; set; }

        /// <summary>Whether the turn calls <c>request_user_input</c> at all. False models the attempt AFTER the
        /// question was answered — the turn the fault arm's escape hatch depends on.</summary>
        public bool Ask { get; set; } = true;

        /// <summary>When set, the provider stream THROWS with this message once the turn's tool calls are done —
        /// "a fault later in the same exchange". Null ⇒ the turn completes normally.</summary>
        public string? FaultMessage { get; set; }

        /// <summary>A tool the model calls AFTER the ask — the containment fact. Null ⇒ no follow-up call.</summary>
        public string? FollowUpTool { get; set; }

        /// <summary>When set, the turn also declares <c>emit_step_result{succeeded:false}</c> with this summary —
        /// the channel a refused delegated step is redirected to.</summary>
        public string? DeclareFailureAfterAsk { get; set; }

        /// <summary>What the ask interception handed back, in call order.</summary>
        public List<string?> AskResults { get; } = [];

        /// <summary>The tool list this step turn was OFFERED — the scoping half.</summary>
        public IList<AITool>? OfferedTools { get; set; }

        /// <summary>Every name that reached <c>RouteToolCallAsync</c> — a pre-route interception must appear in
        /// neither this nor the timeline.</summary>
        public List<string> RoutedNames { get; } = [];

        /// <summary>Every name that actually reached <c>Execute()</c>.</summary>
        public List<string> ExecutedNames { get; } = [];
    }

    /// <summary>
    /// Plans exactly ONE real step. Deliberately not <c>PlanResult.Fallback</c>: the R10 degrade turn creates no
    /// <c>AgentStep</c> row, so it is not offered either step tool at all — a run has to reach the drain loop
    /// before there is anything to put back to Pending.
    /// </summary>
    private sealed class OneStepPlanner : IAgentPlanner
    {
        public Task<PlanResult> PlanAsync(string goal, RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct)
            => Task.FromResult(new PlanResult(
                [new AgentStep { Id = Guid.NewGuid(), Ordinal = 0, Title = "S0", Intent = "do it", Status = AgentStepStatus.Pending }],
                FallBackToSingleTurn: false));

        // A step that ASKED must never reach the replanner — an ask is not a failure. If it did, this returning
        // Fallback would settle the run terminally and the park facts above would red loudly.
        public Task<PlanResult> ReplanAsync(RunContext ctx, string? failure, Persona persona, AiProvider provider, CancellationToken ct)
            => Task.FromResult(PlanResult.Fallback);
    }

    private HeadlessRunLauncher Build(AskProbe probe)
    {
        var provider = new AiProvider { Id = Guid.NewGuid(), Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI };
        var persona = new Persona { Name = "Pia", SystemPrompt = "sys" };
        var planner = new OneStepPlanner();

        var ai = Substitute.For<IAiClientService>();
        ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                probe.OfferedTools = ci.ArgAt<IList<AITool>?>(2);
                return Drive(ci.ArgAt<ToolCallHandler?>(3), probe);
            });

        var plugins = Substitute.For<IPluginService>();
        plugins.IsMcpTool(Arg.Any<string>()).Returns(false);
        plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var name = ci.Arg<FunctionCallContent>().Name;
                probe.RoutedNames.Add(name);
                // A DEFERRED write (a pending action) — the only shape that reaches the gate at all.
                return ((object? Result, PluginToolCall? PendingAction)?)(null, new PluginToolCall(
                    name, Guid.NewGuid(), "files", "desc", null,
                    () => { probe.ExecutedNames.Add(name); return Task.FromResult<object?>("did it"); }));
            });

        var composer = Substitute.For<IAssistantPromptComposer>();
        composer.PrepareTurn(Arg.Any<Persona>(), Arg.Any<AiProvider>(), Arg.Any<IReadOnlyList<AtCommand>>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(new AssistantTurnSetup("system", new List<AITool>(), SupportsTools: true, WebSearchActive: false));
        var personas = Substitute.For<IPersonaService>();
        personas.ResolveActiveAsync(Arg.Any<WindowMode>(), Arg.Any<UserOperatingMode>()).Returns(persona);
        var providers = Substitute.For<IProviderService>();
        providers.GetDefaultProviderForModeAsync(Arg.Any<WindowMode>()).Returns(provider);
        var titles = Substitute.For<IChatTitleService>();
        titles.GenerateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAiClientService>(ai);
        services.AddSingleton<IPluginService>(plugins);
        services.AddSingleton<IAssistantPromptComposer>(composer);
        services.AddSingleton<IPersonaService>(personas);
        services.AddSingleton<IProviderService>(providers);
        services.AddSingleton<IChatTitleService>(titles);
        services.AddSingleton<ISettingsService>(settings);
        services.AddSingleton<IAgentRunService>(_runs);
        services.AddSingleton<IAssistantChatService>(_chats);
        services.AddSingleton<IAgentPlanner>(planner);
        services.AddSingleton<IAgentVerifier>(new FakeVerifier());
        services.AddSingleton<Func<ITokenMapService>>(_ => () => Substitute.For<ITokenMapService>());
        services.AddSingleton<IExecutingRunStore>(_executing);
        // REAL audit wiring: "no UnknownTool row for the ask" is one of the facts, and a decision nobody records
        // is a decision nobody can check.
        services.AddSingleton<IAgentTimelineService>(_timeline);
        services.AddTransient<BackgroundAssistantTurnRunner>();
        services.AddTransient<HeadlessTurnExecutor>();
        services.AddTransient<AgentRunOrchestrator>();
        var sp = services.BuildServiceProvider();

        return new HeadlessRunLauncher(
            sp.GetRequiredService<IServiceScopeFactory>(), _chats, _runs, settings, providers, personas,
            _executing, NullLogger<HeadlessRunLauncher>.Instance, runsBaseDirOverride: _runsBase);
    }

    private static async IAsyncEnumerable<ChatStreamItem> Drive(ToolCallHandler? handler, AskProbe probe)
    {
        await Task.Yield();
        if (handler is not null)
        {
            if (probe.Ask)
            {
                probe.AskResults.Add(await handler(
                    new FunctionCallContent("call-1", AgentStepTools.RequestUserInputToolName,
                        new Dictionary<string, object?> { ["question"] = probe.NextQuestion }),
                    new ToolDispatchContext(1)) as string);
            }

            // A model that keeps going after being told the run is parking. Round-tripped through the SAME
            // handler, because that is the only way first-wins and the containment guard are observable.
            if (probe.SecondQuestion is not null)
            {
                probe.AskResults.Add(await handler(
                    new FunctionCallContent("call-2", AgentStepTools.RequestUserInputToolName,
                        new Dictionary<string, object?> { ["question"] = probe.SecondQuestion }),
                    new ToolDispatchContext(1)) as string);
            }

            if (probe.FollowUpTool is not null)
            {
                await handler(
                    new FunctionCallContent("call-3", probe.FollowUpTool, new Dictionary<string, object?>()),
                    new ToolDispatchContext(2));
            }

            if (probe.DeclareFailureAfterAsk is not null)
            {
                await handler(
                    new FunctionCallContent("call-4", AgentStepTools.EmitStepResultToolName,
                        new Dictionary<string, object?>
                        {
                            ["succeeded"] = false,
                            ["summary"] = probe.DeclareFailureAfterAsk,
                        }),
                    new ToolDispatchContext(3));
            }
        }

        // THE FAULT, thrown out of the enumeration itself: RunExchangeAsync does not catch, so it lands in
        // HeadlessTurnExecutor's catch exactly as a dropped provider connection does — after whatever the turn
        // already did, which is the whole point of the pair of tests that use this.
        if (probe.FaultMessage is not null)
            throw new InvalidOperationException(probe.FaultMessage);

        // TEXT STILL FLOWS. Every fact here therefore discriminates on the RUN's state, never on "the step
        // produced nothing" — neutralising the ask leaves this reply exactly where it was.
        yield return new TextDelta("reply");
        yield return new Finished(null, "test-model");
    }
}
