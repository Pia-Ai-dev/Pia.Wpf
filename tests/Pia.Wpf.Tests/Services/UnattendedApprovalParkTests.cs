using System.IO;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
/// hermes #16, THE UNATTENDED APPROVAL PARK. A headless run that hit a promptable capability it was not
/// granted used to HARD-DENY it — the model was told "Do not retry", the step carried on without the thing it
/// needed, and the run finished having quietly failed to do the job. It now stops and asks, reusing the Batch
/// 06 park verbatim: <c>WaitingForInput</c>, the same pause envelope, the same durable Flow Continue card and
/// the same <c>IAgentRunResumeService</c> claim.
/// <para>
/// THE BOUNDARY THIS SUITE DEFENDS. Parking is for calls a human could legitimately approve; everything else
/// still hard-denies, and each guard here is written so that making the park GREEDIER reds it:
/// <list type="bullet">
/// <item>a destructive EXTERNAL (MCP) tool — the unliftable floor (T-PARK-3)</item>
/// <item>a delete-like BUILT-IN, which the floor does NOT cover and a named grant would run (T-PARK-4)</item>
/// <item>a CHILD run, pinned to default-deny as a delegate (T-PARK-5)</item>
/// <item>an unrouted tool, which never reaches the gate at all (T-PARK-6)</item>
/// </list>
/// </para>
/// <para>
/// THE UNBOUNDED-PARK HAZARD, and how it is answered. A scheduled job has nobody at the keyboard, so "park
/// and wait" could in principle be forever. It is bounded not in TIME but in RESOURCES: a parked run holds
/// nothing at all (T-PARK-8) — the dispatch task has returned, the concurrency slot is released, the
/// executing-run bracket is closed and the ledger's work segment is shut, so parked time is not worked time
/// and a parked run costs exactly one database row. The question itself is durable (a Persistent Flow card
/// plus a row the startup sweep deliberately leaves alone), so the human is reached whenever they next open
/// the app rather than never. That is the same shape <c>D5PausePremiseTests</c> measured for the budget park,
/// which is the point of reusing it instead of inventing a second park.
/// </para>
/// <para>
/// Real everything below the AI client: real SQLite run + chat stores, real <c>HeadlessRunLauncher</c>, real
/// <c>AgentRunOrchestrator</c>, real <c>HeadlessTurnExecutor</c>, real <c>BackgroundAssistantTurnRunner</c>
/// and the real <c>ToolAutonomy</c> gate. Only the provider stream, the plugin route and the planner are
/// doubles — the park is a decision made between those three and nothing else would be exercised by faking
/// the layers in between.
/// </para>
/// </summary>
public sealed class UnattendedApprovalParkTests : IDisposable
{
    private readonly string _dir;
    private readonly string _runsBase;
    private readonly SqliteContext _ctx;
    private readonly AgentRunService _runs;
    private readonly AssistantChatService _chats;
    private readonly ExecutingRunStore _executing = new();
    private readonly RecordingTimelineService _timeline = new();

    public UnattendedApprovalParkTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PiaApprovalPark_" + Guid.NewGuid().ToString("N"));
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

    // ---------------------------------------------------------------- the discriminating facts

    /// <summary>
    /// T-PARK-1, the headline. A ROOT headless run whose step calls an ungranted, non-destructive, routed
    /// tool now PARKS: the tool does not run, the run sits at WaitingForInput with a <c>tool-approval</c>
    /// envelope naming it, and the step is back at Pending so a Continue re-runs exactly that step.
    /// <para>
    /// The run is NOT terminal and NOT failed — that distinction is the whole feature. Before #16 the same
    /// input produced a Completed run that had silently not done the work.
    /// </para>
    /// <para>
    /// <b>Neutralize:</b> comment out the Park arm in <c>ToolAutonomy.Resolve</c> (the mechanism, not the
    /// feature — the gate then falls through to <c>Refuse/DeniedNotGranted</c> exactly as it did before) →
    /// the run completes and every assertion below reds. Note that the model's text still flows in both
    /// worlds: the fake stream always yields a reply, so nothing here can pass on "the turn produced nothing".
    /// </para>
    /// </summary>
    [Fact]
    public async Task UngrantedPromptableTool_ParksTheRunForApproval_InsteadOfDenyingIt()
    {
        var probe = new ToolProbe("write_file");
        var (launcher, _) = Build(probe);

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("g", AgentRunTrigger.Schedule, GrantedWrites: []),
            TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        var run = await GetRunAsync(handle.RunId);
        Assert.Equal(AgentRunState.WaitingForInput, run.State);
        Assert.Equal("tool-approval", PauseMember(run, "reason"));
        Assert.Equal("write_file", PauseMember(run, "tool"));

        // The security half: parking is not running.
        Assert.False(probe.Executed);
        // The model was told, and told to stop — not "denied, do not retry".
        Assert.Contains("approval", probe.GateResult ?? string.Empty);

        // The step is resumable, not consumed: NextPendingStepAsync must still find it, or a Continue would
        // drain an empty plan and settle the run Completed with the work never done.
        var pending = await _runs.NextPendingStepAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(pending);
        Assert.Equal(AgentStepStatus.Pending, pending!.Status);

        // A park is NOT a completion (the state above) and NOT a failure — CompletedAt stays null, which is
        // what keeps the startup sweep and the scheduled-job striker from treating it as a finished run.
        Assert.Null(run.CompletedAt);

        // Audited as its own decision, not as a denial: the timeline is where a user goes to ask why a run
        // stopped, and "denied" for a call still awaiting their answer would be the wrong story.
        var row = Assert.Single(_timeline.Rows, r => r.ToolName == "write_file");
        Assert.Equal(ToolGateDecision.ParkedForApproval, row.Decision);
        Assert.Equal(AgentTimelineOutcome.NotExecuted, row.Outcome);
        Assert.Equal(ToolGateSurface.Unattended, row.Surface);

        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// T-PARK-2, the other half of the headline: <b>the human decision reaches the pending call</b>. Continue
    /// IS the approval, so a resume grants the one tool the envelope named and the re-run step's SAME call
    /// now executes.
    /// <para>
    /// The pending call itself cannot be replayed — a park outlives the process and a deferred action's
    /// delegate does not — so what is applied is the CAPABILITY, and the evidence that it reached the actual
    /// call is that the identical tool the run parked on is the one that runs.
    /// </para>
    /// <para>
    /// <b>Neutralize:</b> delete the <c>grants = widened;</c> line in <c>HeadlessRunLauncher.ResumeAsync</c>
    /// (leaving the read, the persist and the log) → the resumed step parks again on the same tool and
    /// <c>probe.Executed</c> reds.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Resume_AppliesTheApprovalToTheToolTheRunParkedOn()
    {
        var probe = new ToolProbe("write_file");
        var (launcher, _) = Build(probe);

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("g", AgentRunTrigger.Schedule, GrantedWrites: []),
            TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.False(probe.Executed); // the premise: it parked rather than ran

        // THE HUMAN'S DECISION. This is the exact call the panel's Continue button and the Flow card's
        // ContinueRunAction both make — no approval-specific entry point exists, by design.
        Assert.True(await launcher.ResumeAsync(handle.RunId, ct: TestContext.Current.CancellationToken));
        await AwaitSettledAsync(handle.RunId);

        Assert.True(probe.Executed);
        var run = await GetRunAsync(handle.RunId);
        Assert.Equal(AgentRunState.Completed, run.State);

        // DURABLE, not just applied to this dispatch. Without persisting it, a run needing two tools would
        // park on A, be granted A, park on B, be granted B but forget A, park on A again — a livelock paced
        // by a human pressing Continue.
        Assert.Contains("write_file", HeadlessRunLauncher.TryRestoreGrantEnvelope(run.PolicyJson)!);

        // And the re-run call is audited as a GRANT, not a second park: the question was answered once.
        Assert.Equal(
            ToolGateDecision.GrantedByName,
            _timeline.Rows.Last(r => r.ToolName == "write_file").Decision);

        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// T-PARK-3, <b>GUARD</b>. The unliftable floor: a destructive EXTERNAL (MCP) tool never runs unattended
    /// and never parks either. There is no human who can be shown enough to consent — an MCP tool's name and
    /// effect are server-defined and the Continue affordance carries no arguments — so it stays a hard denial.
    /// <para>
    /// <b>Greedy-park mutation that must red this:</b> move the Park arm ABOVE the floor in
    /// <c>ToolAutonomy.Resolve</c>. Proven by doing it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task DestructiveExternalTool_StillHardDenies_AndNeverParks()
    {
        var probe = new ToolProbe("delete_thing");
        var (launcher, _) = Build(probe, isMcpTool: true);

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("g", AgentRunTrigger.Schedule, GrantedWrites: []),
            TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        await AwaitSettledAsync(handle.RunId);

        await AssertHardDeniedNotParkedAsync(handle.RunId, probe, ToolGateDecision.DeniedDestructiveFloor);
        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// T-PARK-4, <b>GUARD</b>. A delete-like BUILT-IN is the sharper case, because the destructive-external
    /// floor does NOT cover it: <c>delete_file</c> runs unattended today when a grant list names it. It still
    /// must not PARK, and the reason is the asymmetry between the two approval surfaces — an action card
    /// shows the ARGUMENTS of the call it is asking about, the Continue button shows one sentence. Approving
    /// an irreversible action blind, for a step that will then re-run and pick its own path, is not consent.
    /// <para>
    /// <b>Greedy-park mutation that must red this:</b> drop <c>&amp;&amp; !isDeleteLike</c> from the Park arm.
    /// Proven by doing it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task DeleteLikeBuiltInTool_StillHardDenies_AndNeverParks()
    {
        var probe = new ToolProbe("delete_file");
        var (launcher, _) = Build(probe); // built-in: IsMcpTool false, so the FLOOR does not apply

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("g", AgentRunTrigger.Schedule, GrantedWrites: []),
            TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        await AwaitSettledAsync(handle.RunId);

        await AssertHardDeniedNotParkedAsync(handle.RunId, probe, ToolGateDecision.DeniedNotGranted);
        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// T-PARK-5, <b>GUARD</b>. A CHILD run is a delegate (hermes #8): it receives a strict subset of its
    /// parent's authority and may never acquire more. An approval park ACQUIRES authority, so a child parking
    /// would be the one path by which a delegate ends up wider than its delegator. It hard-denies instead —
    /// the same tool, the same empty grant set, the same everything except the parent id.
    /// <para>
    /// <b>Greedy-park mutation that must red this:</b> make <c>HeadlessRunLauncher.CanParkForApproval</c>
    /// return <c>true</c> unconditionally. Proven by doing it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ChildRun_StillHardDenies_AndNeverParks()
    {
        var probe = new ToolProbe("write_file"); // the very tool a ROOT run parks on in T-PARK-1
        var (launcher, _) = Build(probe);

        // The parent is a bare ROW, never launched: it exists only to make the run under test a child, and
        // launching it would park it too (it is a root run) — putting a second run's ParkedForApproval row in
        // the shared timeline and making this fact's "nothing parked" assertion ambiguous.
        var parent = await NewRunAsync();
        var child = await ParkedChildAsync(parent.Id);
        Assert.True(await launcher.ResumeAsync(child.Id, ct: TestContext.Current.CancellationToken));
        await AwaitSettledAsync(child.Id);

        await AssertHardDeniedNotParkedAsync(child.Id, probe, ToolGateDecision.DeniedNotGranted);
        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// T-PARK-6, <b>GUARD</b>. A tool with no route never reaches the gate at all — it dead-ends at
    /// "Unknown tool." before <c>Resolve</c> is called. Worth pinning because a park is the one gate outcome
    /// a model could try to PROVOKE: inventing a plausible tool name to make the run stop and put a Continue
    /// button in front of a human is a strictly better attack than being denied, and this is the arm that
    /// makes it impossible rather than merely unlikely.
    /// </summary>
    [Fact]
    public async Task UnroutedToolName_StillDeadEnds_AndNeverParks()
    {
        var probe = new ToolProbe("totally_invented_tool");
        var (launcher, _) = Build(probe, routed: false);

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("g", AgentRunTrigger.Schedule, GrantedWrites: []),
            TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        await AwaitSettledAsync(handle.RunId);

        var run = await GetRunAsync(handle.RunId);
        Assert.NotEqual(AgentRunState.WaitingForInput, run.State);
        Assert.Null(PauseMember(run, "tool"));
        Assert.Contains("Unknown tool", probe.GateResult ?? string.Empty);
        Assert.DoesNotContain(_timeline.Rows, r => r.Decision == ToolGateDecision.ParkedForApproval);

        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// T-PARK-7, <b>GUARD</b>. A run whose policy already covers the tool AUTO-RUNS it — it never reaches the
    /// park at all. The non-vacuity control for the whole suite: without it every guard above could be
    /// satisfied by a park that simply never fires, and the "greedy" mutations would be the only thing
    /// keeping the feature honest.
    /// </summary>
    [Fact]
    public async Task PolicyCoveredTool_StillAutoRuns_AndNeverParks()
    {
        var probe = new ToolProbe("write_file");
        var (launcher, _) = Build(probe, appSettings: new AppSettings { AgentRunAutoApproveBuiltInWrites = true });

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("g", AgentRunTrigger.Schedule, GrantedWrites: []),
            TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        await AwaitSettledAsync(handle.RunId);

        Assert.True(probe.Executed);
        Assert.Equal(AgentRunState.Completed, (await GetRunAsync(handle.RunId)).State);
        Assert.DoesNotContain(_timeline.Rows, r => r.Decision == ToolGateDecision.ParkedForApproval);

        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// T-PARK-8, <b>THE BOUND</b>. "Park and wait for a human" on a run nobody is watching is only safe if
    /// waiting is FREE, so this measures that a parked-for-approval run holds nothing:
    /// <list type="number">
    /// <item>the dispatch task has RETURNED — which is where the concurrency slot is released in its
    /// <c>finally</c>, so a parked run does not occupy one of the launcher's slots forever;</item>
    /// <item>the executing-run bracket is CLOSED, so the composer/session bookkeeping is not pinned;</item>
    /// <item>the ledger's work segment is SHUT, so parked wall-clock is not billed as worked time and does
    /// not eat the fresh budget the eventual Continue hands out;</item>
    /// <item>TWO runs park on the SAME launcher and a THIRD then still gets a permit from that same
    /// two-wide pool — the sharpest observable form of (1), and the only shape that catches a slot leak:
    /// the pool is a per-instance field, so a second launcher proves nothing about the first's permits,
    /// and a single parked run cannot exhaust a cap of two;</item>
    /// <item>and the question survives: the row is still claimable, which is what makes the wait an
    /// unanswered question rather than a lost one.</item>
    /// </list>
    /// A time bound is deliberately NOT the answer. The park is answered by a human, and a deadline that
    /// expired into a denial would silently un-do the very decision this feature exists to collect.
    /// </summary>
    [Fact]
    public async Task AParkedRunHoldsNothing_SoWaitingForAHumanIsBounded()
    {
        var probe = new ToolProbe("write_file");
        var (launcher, _) = Build(probe);

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("g", AgentRunTrigger.Schedule, GrantedWrites: []),
            TestContext.Current.CancellationToken);

        // (1) It RETURNS. A park that awaited a human here would hang this line until the timeout.
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        var run = await GetRunAsync(handle.RunId);
        Assert.Equal(AgentRunState.WaitingForInput, run.State); // the premise: it really is parked

        // (2) The A2 bracket is closed.
        Assert.False(_executing.IsExecuting(run.ChatId));

        // (3) Parked time is not worked time: no OPEN segment is left on the ledger clock.
        Assert.Null(OpenLedgerSegmentStart(run.Id));

        // (4) THE SLOT ITSELF, on the pool that actually holds it. This step used to build a SECOND
        // HeadlessRunLauncher — but `_slots` is a per-INSTANCE `new(2, 2)`, so that run drew on a fresh pool of
        // two permits and would have completed even if the parked launcher had leaked BOTH of its own. The
        // claim "would catch a slot leak the other three miss" was measured by nothing.
        //
        // Two things are needed to observe it. The pool must be the SAME instance that parked — so every run
        // below goes through `launcher`. And the pool must be EXHAUSTED, which takes TWO parked runs: with a
        // cap of two, one leaked permit still leaves one, and a queue that drains serially completes anyway.
        var secondPark = await launcher.LaunchAsync(
            new HeadlessRunRequest("g2", AgentRunTrigger.Schedule, GrantedWrites: []),
            TestContext.Current.CancellationToken);
        await secondPark.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.WaitingForInput, (await GetRunAsync(secondPark.RunId)).State);

        // Both permits are now held by parked runs unless a park releases them. GRANTING the tool this run
        // would otherwise park on is what lets it reach Completed at all, so the only thing its progress is
        // evidence about is the pool: if a park held its permit, this line hangs until the timeout.
        var third = await launcher.LaunchAsync(
            new HeadlessRunRequest("g3", AgentRunTrigger.Schedule, GrantedWrites: ["write_file"]),
            TestContext.Current.CancellationToken);
        await third.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        await AwaitSettledAsync(third.RunId);
        Assert.Equal(AgentRunState.Completed, (await GetRunAsync(third.RunId)).State);

        // …and BOTH parked runs are still parked, so the third run's permit did not come from one of them
        // being swept, resumed or failed out of the way.
        Assert.Equal(AgentRunState.WaitingForInput, (await GetRunAsync(handle.RunId)).State);
        Assert.Equal(AgentRunState.WaitingForInput, (await GetRunAsync(secondPark.RunId)).State);

        // (5) The question is still answerable — the row remains claimable by the ordinary resume CAS.
        Assert.True(await _runs.TryBeginResumeAsync(handle.RunId, TestContext.Current.CancellationToken));

        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// T-PARK-9, <b>REGRESSION</b>. The Flow card's body for an approval park must NOT be the budget wording
    /// and MUST name the tool. Both pause readers fall back to the budget copy rather than failing, so a test
    /// that only asked "is the body non-empty?" would pass on the fall-through — the precise way Batch 08's
    /// F19 defect shipped, and the third time on this branch that "the assertion observed the default".
    /// <para>
    /// Naming the tool is not polish either: Continue on an approval park IS the grant, so a card that does
    /// not say what it is granting asks the user to approve something blind.
    /// </para>
    /// </summary>
    [Fact]
    public void TheFlowContinueCard_NamesTheToolAndIsNotTheBudgetBody()
    {
        var loc = Substitute.For<ILocalizationService>();
        loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        loc.Format(Arg.Any<string>(), Arg.Any<object[]>())
            .Returns(ci => (string)ci[0] + "|" + string.Join(',', ((object[])ci[1]).Select(a => a?.ToString())));

        var parked = new AgentRun
        {
            Id = Guid.NewGuid(),
            ExtraJson = """{"paused":true,"reason":"tool-approval","tool":"write_file"}""",
        };

        Assert.NotEqual("Flow_Run_WaitingAtBudget", AgentRunNotificationSurface.PausedBodyKey("tool-approval"));
        Assert.Equal("Flow_Run_ToolApproval|write_file", AgentRunNotificationSurface.PausedBody(loc, parked));

        // The fall-through is untouched for every other token, which is what makes the arm above a mapping
        // rather than a blanket rewrite.
        Assert.Equal("Flow_Run_WaitingAtBudget", AgentRunNotificationSurface.PausedBodyKey("step-cap"));
        Assert.Equal(
            "Flow_Run_WaitingAtBudget",
            AgentRunNotificationSurface.PausedBody(loc, new AgentRun { ExtraJson = """{"paused":true,"reason":"step-cap"}""" }));
    }

    /// <summary>
    /// T-PARK-10. The envelope round-trip in isolation, including the two degrades a reader must survive:
    /// a park with no <c>tool</c> member (every other park) and a blank one (a corrupted row) both read as
    /// null rather than as an empty tool the Continue card would offer to grant.
    /// <para>
    /// The first row is also the byte-shape pin: <c>PauseAsync</c> with no approval tool must write the
    /// document it has always written, so no existing park's envelope changes shape.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ThePauseEnvelopeCarriesTheToolOnlyForAnApprovalPark()
    {
        var ct = TestContext.Current.CancellationToken;
        var budget = await NewRunAsync();
        await _runs.PauseAsync(budget.Id, "step-cap", ct);
        Assert.Equal("""{"paused":true,"reason":"step-cap"}""", (await GetRunAsync(budget.Id)).ExtraJson);

        var approval = await NewRunAsync();
        await _runs.PauseAsync(approval.Id, "tool-approval", ct, approvalTool: "write_file");
        var run = await GetRunAsync(approval.Id);
        Assert.Equal("tool-approval", PauseMember(run, "reason"));
        Assert.Equal("write_file", PauseMember(run, "tool"));

        // And the claim retires the whole envelope, which is why ResumeAsync has to read the tool BEFORE it
        // CASes — the regression that would make every approval silently grant nothing.
        Assert.True(await _runs.TryBeginResumeAsync(approval.Id, ct));
        Assert.Null((await GetRunAsync(approval.Id)).ExtraJson);
    }

    /// <summary>
    /// T-PARK-11, <b>GUARD</b>. A model that keeps calling tools after it was told the run is parking must not
    /// move the question. The envelope names ONE tool, that name is what the Continue card shows and what the
    /// resume grants, so it has to be the call that actually stopped the run — a later call is one the model
    /// made AFTER being told to stop, and approving THAT because it arrived last would grant a capability the
    /// human was never shown.
    /// <para>
    /// The second half is the audit: exactly ONE <c>ParkedForApproval</c> row. A row per attempt would imply
    /// several pending decisions when there is one, and the panel would show the user a queue that does not
    /// exist. Neither the first-wins rule nor the emit-once rule is observable with a single tool call, which
    /// is why this fact drives two.
    /// </para>
    /// <para><b>Neutralize:</b> in <c>ToolApprovalStore.Park</c>, drop the <c>PendingToolName is not null</c>
    /// early return so the LAST call wins → the envelope names <c>update_todo</c> and this reds.</para>
    /// </summary>
    [Fact]
    public async Task ASecondParkedCallInTheSameStep_DoesNotMoveTheQuestionOrDoubleTheAudit()
    {
        var probe = new ToolProbe("write_file");
        var (launcher, _) = Build(probe, secondToolName: "update_todo");

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("g", AgentRunTrigger.Schedule, GrantedWrites: []),
            TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        var run = await GetRunAsync(handle.RunId);
        Assert.Equal(AgentRunState.WaitingForInput, run.State);

        // FIRST WINS. Both calls were parkable, so a last-wins store would read "update_todo" here.
        Assert.Equal("write_file", PauseMember(run, "tool"));

        // Both calls were told to stop, and neither ran.
        Assert.Empty(probe.ExecutedNames);
        Assert.Equal(2, probe.Results.Count);
        Assert.All(probe.Results, r => Assert.Contains("approval", r ?? string.Empty));

        // ONE pending decision, one audit row — for the tool the envelope actually names.
        var parkRows = _timeline.Rows.Where(r => r.Decision == ToolGateDecision.ParkedForApproval).ToList();
        Assert.Equal("write_file", Assert.Single(parkRows).ToolName);

        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// T-PARK-12, <b>THE CONTAINMENT</b>. A park must STOP the exchange, not merely advise it — so a GRANTED
    /// tool the model calls after the run has already decided to park does not run, and the resumed step
    /// therefore does it exactly ONCE in the run's whole life.
    /// <para>
    /// This is the case T-PARK-11 looks like it covers and does not: both of its calls are ungranted, so both
    /// park and its <c>Assert.Empty(probe.ExecutedNames)</c> passes trivially. Every other fact in this file
    /// launches with <c>GrantedWrites: []</c>, so until this one no fact had ever put a granted tool and a
    /// parked tool in the same exchange — the park DECISION was measured eleven times and containment after it
    /// zero times.
    /// </para>
    /// <para>
    /// Why once matters more than the wasted call: the executor discards the parked step's whole attempt and
    /// the orchestrator puts the row back to <c>Pending</c>, so a side effect that happened after the park is
    /// replayed by the re-run with nothing in the transcript to tell the model it had already done it. One
    /// human Continue press therefore created the same todo twice. Pre-#16 the ungranted call was refused and
    /// the step ran to completion exactly once, so a park must not be able to lose that.
    /// </para>
    /// <para>
    /// <b>Neutralize:</b> delete the <c>approvals?.PendingToolName is { } parkedFor</c> containment guard in
    /// <c>BackgroundAssistantTurnRunner.HandleToolCallAsync</c> (leaving the Park arm itself, i.e. the park
    /// decision, untouched) → <c>update_todo</c> runs during the parked attempt and again on the resume, and
    /// both assertions below red.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AGrantedCallAfterThePark_DoesNotRun_AndIsNotReplayedByTheResume()
    {
        var ct = TestContext.Current.CancellationToken;
        var probe = new ToolProbe("write_file");
        var (launcher, _) = Build(probe, secondToolName: "update_todo");

        // GRANTED, unlike every other fact here — so nothing but the park can stop this second call.
        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("g", AgentRunTrigger.Schedule, GrantedWrites: ["update_todo"]), ct);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), ct);
        Assert.Equal(AgentRunState.WaitingForInput, (await GetRunAsync(handle.RunId)).State);

        // The premise, so this cannot pass on "the second call never happened": the gate answered it.
        Assert.Equal(2, probe.Results.Count);
        // CONTAINMENT: it was answered without being executed.
        Assert.DoesNotContain("update_todo", probe.ExecutedNames);

        Assert.True(await launcher.ResumeAsync(handle.RunId, ct: ct));
        await AwaitSettledAsync(handle.RunId);

        // AT MOST ONCE across the park: the re-run is the only time it happens.
        Assert.Equal(1, probe.ExecutedNames.Count(n => n == "update_todo"));
        // …and the park really was answered, so the once is the resumed step's and not the parked attempt's.
        Assert.Equal(AgentRunState.Completed, (await GetRunAsync(handle.RunId)).State);

        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// T-PARK-13, <b>REGRESSION</b>. A park SURVIVES a provider fault that happens later in the same exchange.
    /// The audit row is written the moment the gate parks, so if the fault then discarded the park the persisted
    /// state contradicted itself: the timeline showed <c>Run_Timeline_Decision_AwaitingApproval</c> — "Awaiting
    /// approval" — on a run that had settled terminally with no pause envelope, no <c>tool</c> member, no Flow
    /// Continue card and no panel Continue button. A user reading that is told the run is waiting for them to
    /// answer a question that no longer exists, which is the exact reporting failure #16 was built to remove.
    /// <para>
    /// It is also the safe direction on its own terms. The tool did not run, the step's text is discarded and
    /// the row goes back to <c>Pending</c> either way, so a fault and a park lead to the same place — except
    /// that parking keeps the QUESTION, and failing throws it away. Nor can it hide a persistent fault: the
    /// resume grants the tool, so the next attempt parks on nothing and the fault surfaces normally.
    /// </para>
    /// <para>
    /// The pairing is the assertion, not either half alone: the timeline row and the run state are read in the
    /// same fact, because each of them separately was already true in the broken world.
    /// </para>
    /// <para><b>Neutralize:</b> delete the <c>approvals.PendingToolName</c> re-check from the generic
    /// <c>catch (Exception ex)</c> arm in <c>HeadlessTurnExecutor.RunExchangeStepAsync</c> → the run settles
    /// terminally, the envelope is gone, and the state assertions below red while the timeline row stays.</para>
    /// </summary>
    [Fact]
    public async Task AParkIsNotDiscardedByAProviderFaultLaterInTheSameExchange()
    {
        var ct = TestContext.Current.CancellationToken;
        var probe = new ToolProbe("write_file");
        var (launcher, _) = Build(probe, faultAfterFirstCall: true);

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("g", AgentRunTrigger.Schedule, GrantedWrites: []), ct);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), ct);

        var run = await GetRunAsync(handle.RunId);

        // The audit already told the user a decision is pending…
        Assert.Equal(
            ToolGateDecision.ParkedForApproval,
            Assert.Single(_timeline.Rows, r => r.ToolName == "write_file").Decision);

        // …so the run has to actually BE parked, and the envelope has to name the tool the card will show.
        Assert.Equal(AgentRunState.WaitingForInput, run.State);
        Assert.Equal("tool-approval", PauseMember(run, "reason"));
        Assert.Equal("write_file", PauseMember(run, "tool"));
        Assert.Null(run.CompletedAt);

        // And the step is still there to re-run, so answering the question is not answering it into a void.
        Assert.NotNull(await _runs.NextPendingStepAsync(run.Id, ct));
        Assert.False(probe.Executed);

        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// T-PARK-14, <b>GUARD</b>. An approval must not make the resume FLOOR durable, and must not destroy an
    /// envelope it could not read.
    /// <para>
    /// The floor (<c>{write_file}</c>) is a deliberate per-dispatch degrade for a run whose launch envelope is
    /// missing, corrupt, foreign or of a version this build does not know — and it is explicitly WIDER than some
    /// launches, which is why <c>InteractiveEmptyEnvelopeJson</c> exists as its own documented shape. Writing a
    /// fresh <c>v:1</c> document built on top of that floor turned a transient degrade into the run's durable
    /// record of its own authority: a run that never held <c>write_file</c> came back holding it forever, and
    /// <c>ResumeAsync</c> re-reads the row and hands <c>PolicyJson</c> to <c>LaunchChildAsync</c>, so the next
    /// fan-out narrows its children from the widened envelope instead of the real one. It also overwrote a
    /// document a NEWER build wrote, which no build could then ever read back.
    /// </para>
    /// <para>
    /// The approval still reaches the pending call — that is asserted here too, on the same dispatch — so this
    /// is about the PERSISTED record, not about whether Continue works.
    /// </para>
    /// <para><b>Neutralize:</b> drop the <c>envelopeWasReadable</c> condition on the
    /// <c>UpdatePolicyJsonAsync</c> call in <c>HeadlessRunLauncher.ResumeAsync</c> → the row is rewritten to
    /// <c>v:1</c> carrying <c>write_file</c> and both assertions below red.</para>
    /// </summary>
    [Fact]
    public async Task Resume_DoesNotPersistTheFloorOverAnEnvelopeItCouldNotRead()
    {
        var ct = TestContext.Current.CancellationToken;
        // A FUTURE version: readable JSON this build must not act on, which is exactly what the floor is for.
        const string future = """{"v":2,"grantedWrites":["read_file"]}""";
        var run = await NewRunAsync(policyJson: future);
        Assert.Null(HeadlessRunLauncher.TryRestoreGrantEnvelope(future)); // the premise: unreadable here

        await _runs.ReplaceStepsAsync(run.Id, [new AgentStep
        {
            Id = Guid.NewGuid(), RunId = run.Id, Ordinal = 0, Title = "S1", Intent = "do it",
            Status = AgentStepStatus.Pending,
        }], ct);
        // NOT in the floor, so the widening block is the code under test.
        await _runs.PauseAsync(run.Id, "tool-approval", ct, approvalTool: "update_todo");

        var probe = new ToolProbe("update_todo");
        var (launcher, _) = Build(probe);
        Assert.True(await launcher.ResumeAsync(run.Id, ct: ct));

        var after = await GetRunAsync(run.Id);
        // The run's own document is still the run's own document.
        Assert.Equal(future, after.PolicyJson);
        // …and in particular the floor did not become the run's durable authority.
        Assert.DoesNotContain("write_file", after.PolicyJson!);

        // The human's decision still reached the call it was collected for — the grant is applied to THIS
        // dispatch, which is all the Continue card ever promised.
        await AwaitSettledAsync(run.Id);
        Assert.Contains("update_todo", probe.ExecutedNames);

        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// T-PARK-15, <b>GUARD</b>. A NON-destructive EXTERNAL (MCP) tool hard-denies too. The floor at the top of
    /// <c>Resolve</c> only catches the destructive ones, and <c>send_email</c> / <c>create_issue</c> are not
    /// delete-like — so this is the case that fell between the two guards and raised a Continue button naming a
    /// server-defined tool.
    /// <para>
    /// It is the same argument the suite already makes for <c>delete_file</c> (T-PARK-4), one step further out.
    /// A park's Continue affordance shows no ARGUMENTS, and here it also names a tool whose meaning and reach
    /// are defined by a third-party server rather than by this app — outside the run's workspace containment
    /// entirely. The destructive floor's own rationale is that a curated grant list authored days earlier is not
    /// informed consent for an MCP call; one unlabelled button is weaker evidence than that list, not stronger.
    /// </para>
    /// <para>
    /// It denies with <c>DeniedNotGranted</c> and NOT with the destructive floor, which is the discriminating
    /// half: routing a non-destructive external tool through the floor would also stop it parking, and would be
    /// the wrong reason recorded in the audit a user reads to find out why the run stopped.
    /// </para>
    /// <para><b>Greedy-park mutation that must red this:</b> drop
    /// <c>&amp;&amp; input.ToolClass != ToolClass.External</c> from the Park arm. Proven by doing it.</para>
    /// </summary>
    [Fact]
    public async Task NonDestructiveExternalTool_StillHardDenies_AndNeverParks()
    {
        var probe = new ToolProbe("send_email"); // routed, granted to nothing, and NOT delete-like
        var (launcher, _) = Build(probe, isMcpTool: true);

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("g", AgentRunTrigger.Schedule, GrantedWrites: []),
            TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        await AwaitSettledAsync(handle.RunId);

        await AssertHardDeniedNotParkedAsync(handle.RunId, probe, ToolGateDecision.DeniedNotGranted);
        await launcher.StopAsync(CancellationToken.None);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Assert the run finished WITHOUT parking, and that the gate really refused this tool.</summary>
    private async Task AssertHardDeniedNotParkedAsync(Guid runId, ToolProbe probe, ToolGateDecision expected)
    {
        var run = await GetRunAsync(runId);

        // The claim is "did not park", asserted on the STATE and on the envelope, not on "the tool did not
        // run" — a park does not run the tool either, so that alone would not tell the two apart.
        Assert.NotEqual(AgentRunState.WaitingForInput, run.State);
        Assert.Null(PauseMember(run, "tool"));
        Assert.DoesNotContain(_timeline.Rows, r => r.Decision == ToolGateDecision.ParkedForApproval);

        Assert.False(probe.Executed);
        Assert.Contains("Denied", probe.GateResult ?? string.Empty);
        Assert.Equal(expected, Assert.Single(_timeline.Rows, r => r.ToolName == probe.ToolName).Decision);
    }

    private async Task<AgentRun> GetRunAsync(Guid runId)
        => (await _runs.GetAsync(runId, TestContext.Current.CancellationToken))!;

    /// <summary>Poll to a terminal state. Approval parks are NOT terminal, so this also proves non-parking.</summary>
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

    /// <summary>A member of the pause envelope, read from the raw row (<c>RunPauseEnvelope</c> is src-internal).</summary>
    private static string? PauseMember(AgentRun run, string member)
    {
        if (string.IsNullOrEmpty(run.ExtraJson)) return null;
        using var doc = JsonDocument.Parse(run.ExtraJson);
        return doc.RootElement.TryGetProperty(member, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
    }

    /// <summary>
    /// The ledger's OPEN work segment start, or null when the clock is shut (G1 shape).
    /// <para>
    /// The member is <c>segmentStartedAt</c>: <c>AgentRunService</c> serializes its <c>Ledger</c> with
    /// <c>PropertyNamingPolicy = CamelCase</c> over a property named <c>SegmentStartedAt</c>. It used to be
    /// read here as <c>openSegmentStartedAt</c>, which no ledger document has ever carried — so this helper
    /// returned null for EVERY possible state and T-PARK-8's parked-time claim was measured by nothing. The
    /// spelling is asserted, not just used, because a silent rename is exactly how the read died the first
    /// time: a helper that cannot find its member must fail loudly rather than report "shut".
    /// </para>
    /// <para>
    /// A CLOSED clock writes <c>"segmentStartedAt":null</c> (the options set no ignore condition), so
    /// "present" and "open" are different questions and this reads both.
    /// </para>
    /// </summary>
    private string? OpenLedgerSegmentStart(Guid runId)
    {
        using var cmd = _ctx.GetConnection().CreateCommand();
        cmd.CommandText = "SELECT LedgerJson FROM AgentRuns WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", runId.ToString());
        if (cmd.ExecuteScalar() is not string json) return null;
        using var doc = JsonDocument.Parse(json);
        Assert.True(
            doc.RootElement.TryGetProperty("segmentStartedAt", out var v),
            "the ledger document carries no 'segmentStartedAt' member — this helper is reading a name that "
            + "does not exist and would report every run's clock as shut");
        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
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

    /// <summary>Records what the unattended gate did with the tool call(s) a run makes.</summary>
    private sealed class ToolProbe
    {
        public ToolProbe(string toolName) => ToolName = toolName;

        /// <summary>The FIRST tool the run calls — the one the park's envelope must name.</summary>
        public string ToolName { get; }

        /// <summary>Every name that actually reached <c>Execute()</c>, in order.</summary>
        public List<string> ExecutedNames { get; } = [];

        /// <summary>Every string the gate handed back, in call order.</summary>
        public List<string?> Results { get; } = [];

        public bool Executed => ExecutedNames.Count > 0;

        /// <summary>The first call's gate result — the only one, for every fact that makes a single call.</summary>
        public string? GateResult => Results.Count > 0 ? Results[0] : null;

        public void MarkExecuted(string name) => ExecutedNames.Add(name);
        public void Record(object? gateResult) => Results.Add(gateResult as string);
    }

    /// <summary>
    /// Plans exactly ONE real step. Deliberately not <see cref="PlanResult.Fallback"/>: the single-turn
    /// degrade turn creates no AgentStep row, so it is not offered the park at all — a run has to reach the
    /// drain loop before there is anything to put back to Pending.
    /// </summary>
    private sealed class OneStepPlanner : IAgentPlanner
    {
        public Task<PlanResult> PlanAsync(string goal, RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct)
            => Task.FromResult(new PlanResult(
                [new AgentStep { Id = Guid.NewGuid(), Ordinal = 0, Title = "S0", Intent = "do it", Status = AgentStepStatus.Pending }],
                FallBackToSingleTurn: false));

        // A parked step must never reach the replanner — a park is not a failure. If it did, this returning
        // Fallback would settle the run terminally and the park facts above would red loudly.
        public Task<PlanResult> ReplanAsync(RunContext ctx, string? failure, Persona persona, AiProvider provider, CancellationToken ct)
            => Task.FromResult(PlanResult.Fallback);
    }

    /// <param name="routed">False ⇒ the plugin service resolves no route for the call (the "Unknown tool." path).</param>
    /// <param name="isMcpTool">True ⇒ the gate derives ToolClass.External, which is what arms the floor.</param>
    /// <param name="secondToolName">A SECOND call the same step turn makes, after the first has already
    /// parked. Omitted ⇒ one call, which is every other fact here.</param>
    /// <param name="faultAfterFirstCall">The provider FAULTS on a later round of the same exchange, after the
    /// first call has already parked — a timeout, a truncation or a transport error, all of which surface here
    /// as an exception out of the stream.</param>
    private (HeadlessRunLauncher Launcher, OneStepPlanner Planner) Build(
        ToolProbe probe, bool routed = true, bool isMcpTool = false, AppSettings? appSettings = null,
        string? secondToolName = null, bool faultAfterFirstCall = false)
    {
        var provider = new AiProvider { Id = Guid.NewGuid(), Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI };
        var persona = new Persona { Name = "Pia", SystemPrompt = "sys" };
        var planner = new OneStepPlanner();

        var ai = Substitute.For<IAiClientService>();
        ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<Func<FunctionCallContent, Task<object?>>?>(), Arg.Any<string?>(), Arg.Any<Guid?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci => DriveWithToolCall(
                ci.ArgAt<Func<FunctionCallContent, Task<object?>>?>(3), probe, secondToolName,
                faultAfterFirstCall));

        var plugins = Substitute.For<IPluginService>();
        plugins.IsMcpTool(Arg.Any<string>()).Returns(isMcpTool);
        plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            // Echoes the INCOMING call's name rather than the probe's, so a turn that makes two different
            // calls really presents two different tools to the gate.
            .Returns(ci =>
            {
                var name = ci.Arg<FunctionCallContent>().Name;
                return routed
                    // A DEFERRED write (a pending action) — the only shape that reaches the gate at all; a
                    // read returns its Result and short-circuits above it.
                    ? ((object? Result, PluginToolCall? PendingAction)?)(null, new PluginToolCall(
                        name, Guid.NewGuid(), isMcpTool ? "some-mcp-server" : "files", "desc", null,
                        () => { probe.MarkExecuted(name); return Task.FromResult<object?>("did it"); }))
                    // NULL, not a tuple of nulls: `route is null` is what the handler tests for, and a
                    // (null, null) tuple would fall out at "Tool call handled." instead — a different path.
                    : null;
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
        settings.GetSettingsAsync().Returns(appSettings ?? new AppSettings());

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
        // The audit sink is REAL wiring here (the launcher suite omits it): the park's own timeline row is
        // one of the facts, and a decision nobody records is a decision nobody can be shown.
        services.AddSingleton<IAgentTimelineService>(_timeline);
        services.AddTransient<BackgroundAssistantTurnRunner>();
        services.AddTransient<HeadlessTurnExecutor>();
        services.AddTransient<AgentRunOrchestrator>();
        var sp = services.BuildServiceProvider();

        var launcher = new HeadlessRunLauncher(
            sp.GetRequiredService<IServiceScopeFactory>(), _chats, _runs, settings, providers, personas,
            _executing, NullLogger<HeadlessRunLauncher>.Instance, runsBaseDirOverride: _runsBase);
        return (launcher, planner);
    }

    private static async IAsyncEnumerable<ChatStreamItem> DriveWithToolCall(
        Func<FunctionCallContent, Task<object?>>? handler, ToolProbe probe, string? secondToolName = null,
        bool faultAfterFirstCall = false)
    {
        await Task.Yield();
        if (handler is not null)
        {
            probe.Record(await handler(new FunctionCallContent("call-1", probe.ToolName, new Dictionary<string, object?>())));

            // The exchange dies on a LATER round than the one that parked. Thrown from inside the stream
            // because that is where a real transport error, a timeout and a truncation all surface.
            if (faultAfterFirstCall)
                throw new InvalidOperationException("provider faulted after the park");

            // A model that keeps going after being told the run is parking. Round-tripped through the SAME
            // handler, because that is the only way the store's first-wins rule is observable.
            if (secondToolName is not null)
                probe.Record(await handler(new FunctionCallContent("call-2", secondToolName, new Dictionary<string, object?>())));
        }

        // TEXT STILL FLOWS. Every park fact in this file therefore discriminates on the RUN's state, never on
        // "the step produced nothing" — neutralising the park leaves this reply exactly where it was.
        yield return new TextDelta("reply");
        yield return new Finished(null, "test-model");
    }
}
