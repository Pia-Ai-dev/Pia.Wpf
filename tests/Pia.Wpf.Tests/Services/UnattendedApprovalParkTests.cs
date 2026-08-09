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

/// <summary>Parking an ungranted call is for tools a human could legitimately approve, so every guard here is written to red if
/// the park gets greedier. Only the provider stream, the plugin route and the planner are doubles.</summary>
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

    /// <summary>The run is left neither terminal nor failed, which is the whole feature: the earlier behaviour completed a run
    /// that had silently not done the work.</summary>
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

        Assert.False(probe.Executed);
        Assert.Contains("approval", probe.GateResult ?? string.Empty);

        // The step must stay findable, or a Continue would drain an empty plan and settle the run Completed.
        var pending = await _runs.NextPendingStepAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(pending);
        Assert.Equal(AgentStepStatus.Pending, pending!.Status);

        // A null CompletedAt is what keeps the startup sweep and the job striker off a parked run.
        Assert.Null(run.CompletedAt);

        // Audited as its own decision: "denied" for a call still awaiting an answer would be the wrong story.
        var row = Assert.Single(_timeline.Rows, r => r.ToolName == "write_file");
        Assert.Equal(ToolGateDecision.ParkedForApproval, row.Decision);
        Assert.Equal(AgentTimelineOutcome.NotExecuted, row.Outcome);
        Assert.Equal(ToolGateSurface.Unattended, row.Surface);

        // The one arm that writes a null DecidedAt: asked, unanswered. A reflexive `decidedAt: UtcNow` would
        // claim a decision was made at the instant the run stopped to ask for one.
        Assert.NotNull(row.RequestedAt);
        Assert.Null(row.DecidedAt);

        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>A park outlives the process but a deferred action's delegate does not, so what a resume applies is the
    /// capability; the evidence it reached the call is that the same tool then runs.</summary>
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

        // The exact call the Continue button and the Flow card make: there is no approval-specific entry point.
        Assert.True(await launcher.ResumeAsync(handle.RunId, ct: TestContext.Current.CancellationToken));
        await AwaitSettledAsync(handle.RunId);

        Assert.True(probe.Executed);
        var run = await GetRunAsync(handle.RunId);
        Assert.Equal(AgentRunState.Completed, run.State);

        // Must be durable: without persisting it, a run needing two tools would park on A, be granted A, park
        // on B, be granted B but forget A, and park on A again — a livelock paced by a human pressing Continue.
        Assert.Contains("write_file", HeadlessRunLauncher.TryRestoreGrantEnvelope(run.PolicyJson)!);

        Assert.Equal(
            ToolGateDecision.GrantedByName,
            _timeline.Rows.Last(r => r.ToolName == "write_file").Decision);

        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>An MCP tool's name and effect are server-defined and the Continue affordance carries no arguments, so no human
    /// can be shown enough to consent.</summary>
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

    /// <summary>An action card shows the call's arguments; the Continue button shows one sentence. Approving an irreversible
    /// action blind, for a step that will then re-run and pick its own path, is not consent.</summary>
    [Fact]
    public async Task DeleteLikeBuiltInTool_StillHardDenies_AndNeverParks()
    {
        var probe = new ToolProbe("delete_file");
        var (launcher, _) = Build(probe); // built-in: IsMcpTool false, so the floor does not apply

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("g", AgentRunTrigger.Schedule, GrantedWrites: []),
            TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        await AwaitSettledAsync(handle.RunId);

        await AssertHardDeniedNotParkedAsync(handle.RunId, probe, ToolGateDecision.DeniedNotGranted);
        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>A child run holds a strict subset of its parent's authority, and a park ACQUIRES authority, so a parking child
    /// would be the one path by which a delegate ends up wider than its delegator.</summary>
    [Fact]
    public async Task ChildRun_StillHardDenies_AndNeverParks()
    {
        var probe = new ToolProbe("write_file"); // the very tool a root run parks on
        var (launcher, _) = Build(probe);

        // A bare row, never launched: launching it would park it too and put a second ParkedForApproval row in
        // the shared timeline, making this fact's "nothing parked" assertion ambiguous.
        var parent = await NewRunAsync();
        var child = await ParkedChildAsync(parent.Id);
        Assert.True(await launcher.ResumeAsync(child.Id, ct: TestContext.Current.CancellationToken));
        await AwaitSettledAsync(child.Id);

        await AssertHardDeniedNotParkedAsync(child.Id, probe, ToolGateDecision.DeniedNotGranted);
        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>A park is the one gate outcome a model could try to provoke: inventing a tool name to put a Continue button in
    /// front of a human beats being denied. An unrouted name dead-ends before <c>Resolve</c> is reached.</summary>
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

    /// <summary>The control for the whole suite: without it, every guard above could be satisfied by a park that never fires.</summary>
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

    /// <summary>Waiting for a human on an unwatched run is only safe if waiting is free; a time bound is deliberately not the
    /// answer, since a deadline expiring into a denial would un-do the decision this exists to collect.</summary>
    [Fact]
    public async Task AParkedRunHoldsNothing_SoWaitingForAHumanIsBounded()
    {
        var probe = new ToolProbe("write_file");
        var (launcher, _) = Build(probe);

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("g", AgentRunTrigger.Schedule, GrantedWrites: []),
            TestContext.Current.CancellationToken);

        // A park that awaited a human here would hang this line until the timeout.
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        var run = await GetRunAsync(handle.RunId);
        Assert.Equal(AgentRunState.WaitingForInput, run.State);

        Assert.False(_executing.IsExecuting(run.ChatId));

        // Parked time is not worked time: no open segment is left on the ledger clock.
        Assert.Null(OpenLedgerSegmentStart(run.Id));

        // The slot pool is a per-instance field, so every run below must go through the SAME launcher, and it
        // takes TWO parked runs to exhaust a cap of two — one leaked permit still leaves one.
        var secondPark = await launcher.LaunchAsync(
            new HeadlessRunRequest("g2", AgentRunTrigger.Schedule, GrantedWrites: []),
            TestContext.Current.CancellationToken);
        await secondPark.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.WaitingForInput, (await GetRunAsync(secondPark.RunId)).State);

        // Granted the tool up front, so its progress is evidence about the pool and nothing else: if a park
        // held its permit, this line hangs until the timeout.
        var third = await launcher.LaunchAsync(
            new HeadlessRunRequest("g3", AgentRunTrigger.Schedule, GrantedWrites: ["write_file"]),
            TestContext.Current.CancellationToken);
        await third.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        await AwaitSettledAsync(third.RunId);
        Assert.Equal(AgentRunState.Completed, (await GetRunAsync(third.RunId)).State);

        // Both are still parked, so the third run's permit did not come from one of them being swept out of
        // the way.
        Assert.Equal(AgentRunState.WaitingForInput, (await GetRunAsync(handle.RunId)).State);
        Assert.Equal(AgentRunState.WaitingForInput, (await GetRunAsync(secondPark.RunId)).State);

        // The question is still answerable: the row remains claimable by the ordinary resume CAS.
        Assert.True(await _runs.TryBeginResumeAsync(handle.RunId, TestContext.Current.CancellationToken));

        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>Both pause readers fall back to the budget copy rather than failing, so a non-empty check would pass on the
    /// fall-through. Continue IS the grant, so a card that does not name the tool asks for blind approval.</summary>
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

        // The fall-through is untouched for every other token, so the arm above is a mapping, not a rewrite.
        Assert.Equal("Flow_Run_WaitingAtBudget", AgentRunNotificationSurface.PausedBodyKey("step-cap"));
        Assert.Equal(
            "Flow_Run_WaitingAtBudget",
            AgentRunNotificationSurface.PausedBody(loc, new AgentRun { ExtraJson = """{"paused":true,"reason":"step-cap"}""" }));
    }

    /// <summary>A missing or blank <c>tool</c> member must read as null, never as an empty tool the Continue card would offer
    /// to grant; and a park with no approval tool must still write the byte-shape it always wrote.</summary>
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

        // The claim retires the whole envelope, so ResumeAsync has to read the tool BEFORE it CASes or every
        // approval silently grants nothing.
        Assert.True(await _runs.TryBeginResumeAsync(approval.Id, ct));
        Assert.Null((await GetRunAsync(approval.Id)).ExtraJson);
    }

    /// <summary>The envelope names one tool and that name is what Continue grants, so a later call — made after the model was
    /// told to stop — must not replace it. Two calls, because neither first-wins nor emit-once shows up with one.</summary>
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

        // Both calls were parkable, so a last-wins store would read "update_todo" here.
        Assert.Equal("write_file", PauseMember(run, "tool"));

        Assert.Empty(probe.ExecutedNames);
        Assert.Equal(2, probe.Results.Count);
        Assert.All(probe.Results, r => Assert.Contains("approval", r ?? string.Empty));

        // One audit row: a row per attempt would show the user a queue of decisions that does not exist.
        var parkRows = _timeline.Rows.Where(r => r.Decision == ToolGateDecision.ParkedForApproval).ToList();
        Assert.Equal("write_file", Assert.Single(parkRows).ToolName);

        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>A park discards the step's whole attempt and puts the row back to <c>Pending</c>, so a side effect that ran
    /// after the park would be replayed by the re-run — one Continue press creating the same todo twice.</summary>
    [Fact]
    public async Task AGrantedCallAfterThePark_DoesNotRun_AndIsNotReplayedByTheResume()
    {
        var ct = TestContext.Current.CancellationToken;
        var probe = new ToolProbe("write_file");
        var (launcher, _) = Build(probe, secondToolName: "update_todo");

        // The second tool is granted, so nothing but the park can stop it.
        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("g", AgentRunTrigger.Schedule, GrantedWrites: ["update_todo"]), ct);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), ct);
        Assert.Equal(AgentRunState.WaitingForInput, (await GetRunAsync(handle.RunId)).State);

        // The gate answered it, so this cannot pass on "the second call never happened".
        Assert.Equal(2, probe.Results.Count);
        Assert.DoesNotContain("update_todo", probe.ExecutedNames);

        Assert.True(await launcher.ResumeAsync(handle.RunId, ct: ct));
        await AwaitSettledAsync(handle.RunId);

        // Once across the park's whole life: the re-run is the only time it happens.
        Assert.Equal(1, probe.ExecutedNames.Count(n => n == "update_todo"));
        Assert.Equal(AgentRunState.Completed, (await GetRunAsync(handle.RunId)).State);

        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>The audit row is written the moment the gate parks, so a fault that then discarded the park left the timeline
    /// saying "awaiting approval" on a terminally settled run. The pairing is the assertion: each half alone was already true.</summary>
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

        // The audit says a decision is pending…
        Assert.Equal(
            ToolGateDecision.ParkedForApproval,
            Assert.Single(_timeline.Rows, r => r.ToolName == "write_file").Decision);

        // …so the run has to actually be parked, on the tool the card will name.
        Assert.Equal(AgentRunState.WaitingForInput, run.State);
        Assert.Equal("tool-approval", PauseMember(run, "reason"));
        Assert.Equal("write_file", PauseMember(run, "tool"));
        Assert.Null(run.CompletedAt);

        // The step is still there, so answering the question is not answering it into a void.
        Assert.NotNull(await _runs.NextPendingStepAsync(run.Id, ct));
        Assert.False(probe.Executed);

        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>The resume floor is a per-dispatch degrade and is wider than some launches, so persisting it would make a run
    /// that never held <c>write_file</c> hold it forever — and would overwrite a document a newer build wrote.</summary>
    [Fact]
    public async Task Resume_DoesNotPersistTheFloorOverAnEnvelopeItCouldNotRead()
    {
        var ct = TestContext.Current.CancellationToken;
        // A future version: readable JSON this build must not act on, which is what the floor is for.
        const string future = """{"v":2,"grantedWrites":["read_file"]}""";
        var run = await NewRunAsync(policyJson: future);
        Assert.Null(HeadlessRunLauncher.TryRestoreGrantEnvelope(future)); // the premise: unreadable here

        await _runs.ReplaceStepsAsync(run.Id, [new AgentStep
        {
            Id = Guid.NewGuid(), RunId = run.Id, Ordinal = 0, Title = "S1", Intent = "do it",
            Status = AgentStepStatus.Pending,
        }], ct);
        // Not in the floor, so the widening block is the code under test.
        await _runs.PauseAsync(run.Id, "tool-approval", ct, approvalTool: "update_todo");

        var probe = new ToolProbe("update_todo");
        var (launcher, _) = Build(probe);
        Assert.True(await launcher.ResumeAsync(run.Id, ct: ct));

        var after = await GetRunAsync(run.Id);
        Assert.Equal(future, after.PolicyJson);
        Assert.DoesNotContain("write_file", after.PolicyJson!);

        // The grant still applies to THIS dispatch, which is all the Continue card ever promised.
        await AwaitSettledAsync(run.Id);
        Assert.Contains("update_todo", probe.ExecutedNames);

        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>The destructive floor only catches delete-like tools, so this is the case that fell between the two guards. It
    /// must deny with <c>DeniedNotGranted</c> and not through the floor, or the audit records the wrong reason.</summary>
    [Fact]
    public async Task NonDestructiveExternalTool_StillHardDenies_AndNeverParks()
    {
        var probe = new ToolProbe("send_email"); // routed, granted to nothing, and not delete-like
        var (launcher, _) = Build(probe, isMcpTool: true);

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("g", AgentRunTrigger.Schedule, GrantedWrites: []),
            TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        await AwaitSettledAsync(handle.RunId);

        await AssertHardDeniedNotParkedAsync(handle.RunId, probe, ToolGateDecision.DeniedNotGranted);
        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>"Do not make me answer this again" has to include the background run about to ask the same question through a
    /// Flow card, so a session grant held in this process keeps the run from parking at all.</summary>
    [Fact]
    public async Task ASessionGrantHeldInThisProcess_KeepsTheRunFromParkingAtAll()
    {
        var pluginId = Guid.NewGuid();
        var session = new SessionToolGrantStore();
        session.Grant(pluginId, "write_file");

        var probe = new ToolProbe("write_file");
        var (launcher, _) = Build(probe, sessionGrants: session, pluginId: pluginId);

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("g", AgentRunTrigger.Schedule, GrantedWrites: []),
            TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        await AwaitSettledAsync(handle.RunId);

        var run = await GetRunAsync(handle.RunId);
        Assert.Equal(AgentRunState.Completed, run.State);
        Assert.NotEqual(AgentRunState.WaitingForInput, run.State);
        Assert.Null(PauseMember(run, "tool"));
        Assert.DoesNotContain(_timeline.Rows, r => r.Decision == ToolGateDecision.ParkedForApproval);

        Assert.True(probe.Executed);
        // Names the tier that carried it: the launch granted nothing and there is no policy.
        Assert.Equal(
            ToolGateDecision.AutoApprovedSessionGrant,
            Assert.Single(_timeline.Rows, r => r.ToolName == "write_file").Decision);

        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>The resume widens its own grants with the parked tool name, so this needs two discriminators the widening
    /// cannot explain: the decision cites the session tier, and a second unrelated run never parks.</summary>
    [Fact]
    public async Task TheSessionTierAppliesToTheResumedRun_AndToALaterRun_WithoutTouchingSettings()
    {
        var pluginId = Guid.NewGuid();
        var session = new SessionToolGrantStore();
        var appSettings = new AppSettings();

        var probe = new ToolProbe("write_file");
        var (launcher, _) = Build(probe, appSettings: appSettings, sessionGrants: session, pluginId: pluginId);

        // The grant does not exist yet, so the run parks.
        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("g", AgentRunTrigger.Schedule, GrantedWrites: []),
            TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.WaitingForInput, (await GetRunAsync(handle.RunId)).State);
        Assert.Equal("write_file", PauseMember(await GetRunAsync(handle.RunId), "tool"));
        Assert.False(probe.Executed);

        // The human chooses the session tier for the capability the card named, then continues the run.
        session.Grant(pluginId, "write_file");
        Assert.True(await launcher.ResumeAsync(handle.RunId, ct: TestContext.Current.CancellationToken));
        await AwaitSettledAsync(handle.RunId);

        Assert.True(probe.Executed);
        Assert.Equal(AgentRunState.Completed, (await GetRunAsync(handle.RunId)).State);
        // The re-run call cites the session grant, not the resume's own widening.
        Assert.Equal(
            ToolGateDecision.AutoApprovedSessionGrant,
            _timeline.Rows.Last(r => r.ToolName == "write_file").Decision);

        // A fresh, unrelated root run with an empty grant set does not park either.
        var second = new ToolProbe("write_file");
        var (launcher2, _) = Build(second, appSettings: appSettings, sessionGrants: session, pluginId: pluginId);
        var handle2 = await launcher2.LaunchAsync(
            new HeadlessRunRequest("g2", AgentRunTrigger.Schedule, GrantedWrites: []),
            TestContext.Current.CancellationToken);
        await handle2.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        await AwaitSettledAsync(handle2.RunId);

        var run2 = await GetRunAsync(handle2.RunId);
        Assert.Equal(AgentRunState.Completed, run2.State);
        Assert.Null(PauseMember(run2, "tool"));
        Assert.True(second.Executed);

        // The session tier writes no settings, so there is no persisted grant for the user to go and revoke.
        Assert.Empty(appSettings.AlwaysAllowedTools);

        await launcher.StopAsync(CancellationToken.None);
        await launcher2.StopAsync(CancellationToken.None);
    }

    /// <summary>Continue's whole vocabulary is "carry on", so reading it as "never ask me again this session" would hand out the
    /// wider tier on evidence of the narrower one.</summary>
    [Fact]
    public async Task ContinuingAParkGrantsTheRunOnly_NotTheSession()
    {
        var pluginId = Guid.NewGuid();
        var session = new SessionToolGrantStore();

        var probe = new ToolProbe("write_file");
        var (launcher, _) = Build(probe, sessionGrants: session, pluginId: pluginId);

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("g", AgentRunTrigger.Schedule, GrantedWrites: []),
            TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.True(await launcher.ResumeAsync(handle.RunId, ct: TestContext.Current.CancellationToken));
        await AwaitSettledAsync(handle.RunId);
        Assert.True(probe.Executed); // the run itself was granted

        // The behavioural half is below: a new run asks again.
        Assert.False(session.IsGranted(pluginId, "write_file"));

        var second = new ToolProbe("write_file");
        var (launcher2, _) = Build(second, sessionGrants: session, pluginId: pluginId);
        var handle2 = await launcher2.LaunchAsync(
            new HeadlessRunRequest("g2", AgentRunTrigger.Schedule, GrantedWrites: []),
            TestContext.Current.CancellationToken);
        await handle2.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        var run2 = await GetRunAsync(handle2.RunId);
        Assert.Equal(AgentRunState.WaitingForInput, run2.State);
        Assert.Equal("write_file", PauseMember(run2, "tool"));
        Assert.False(second.Executed);

        await launcher.StopAsync(CancellationToken.None);
        await launcher2.StopAsync(CancellationToken.None);
    }

    /// <summary>A session grant ACQUIRES authority just as a park does, so the tier is armed on the same <c>CanPark</c> flag a
    /// child never has.</summary>
    [Fact]
    public async Task AChildRun_DoesNotInheritTheSessionTier()
    {
        var pluginId = Guid.NewGuid();
        var session = new SessionToolGrantStore();
        session.Grant(pluginId, "write_file"); // the grant a root run would happily use

        var probe = new ToolProbe("write_file");
        var (launcher, _) = Build(probe, sessionGrants: session, pluginId: pluginId);

        var parent = await NewRunAsync();
        var child = await ParkedChildAsync(parent.Id);
        Assert.True(await launcher.ResumeAsync(child.Id, ct: TestContext.Current.CancellationToken));
        await AwaitSettledAsync(child.Id);

        await AssertHardDeniedNotParkedAsync(child.Id, probe, ToolGateDecision.DeniedNotGranted);
        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>Asserts the run finished without parking, and that the gate really refused this tool.</summary>
    private async Task AssertHardDeniedNotParkedAsync(Guid runId, ToolProbe probe, ToolGateDecision expected)
    {
        var run = await GetRunAsync(runId);

        // Read on the state and the envelope: a park does not run the tool either, so "did not run" cannot
        // tell a denial and a park apart.
        Assert.NotEqual(AgentRunState.WaitingForInput, run.State);
        Assert.Null(PauseMember(run, "tool"));
        Assert.DoesNotContain(_timeline.Rows, r => r.Decision == ToolGateDecision.ParkedForApproval);

        Assert.False(probe.Executed);
        Assert.Contains("Denied", probe.GateResult ?? string.Empty);
        Assert.Equal(expected, Assert.Single(_timeline.Rows, r => r.ToolName == probe.ToolName).Decision);
    }

    private async Task<AgentRun> GetRunAsync(Guid runId)
        => (await _runs.GetAsync(runId, TestContext.Current.CancellationToken))!;

    /// <summary>Polls to a terminal state; an approval park is not terminal, so this also proves non-parking.</summary>
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

    /// <summary>The member name is asserted, not just used: a helper that cannot find it would report every run's clock as
    /// shut. A closed clock writes <c>"segmentStartedAt":null</c>, so present and open are different questions.</summary>
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

    /// <summary>Records what the unattended gate did with the tool call(s) a run makes.</summary>
    private sealed class ToolProbe
    {
        public ToolProbe(string toolName) => ToolName = toolName;

        /// <summary>The first tool the run calls — the one the park's envelope must name.</summary>
        public string ToolName { get; }

        /// <summary>Every name that actually reached <c>Execute()</c>, in order.</summary>
        public List<string> ExecutedNames { get; } = [];

        /// <summary>Every string the gate handed back, in call order.</summary>
        public List<string?> Results { get; } = [];

        public bool Executed => ExecutedNames.Count > 0;

        /// <summary>The first call's gate result.</summary>
        public string? GateResult => Results.Count > 0 ? Results[0] : null;

        public void MarkExecuted(string name) => ExecutedNames.Add(name);
        public void Record(object? gateResult) => Results.Add(gateResult as string);
    }

    /// <summary>Not <see cref="PlanResult.Fallback"/>: the single-turn degrade creates no AgentStep row, so it is never offered
    /// the park at all.</summary>
    private sealed class OneStepPlanner : IAgentPlanner
    {
        public Task<PlanResult> PlanAsync(string goal, RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct)
            => Task.FromResult(new PlanResult(
                [new AgentStep { Id = Guid.NewGuid(), Ordinal = 0, Title = "S0", Intent = "do it", Status = AgentStepStatus.Pending }],
                FallBackToSingleTurn: false));

        // A park is not a failure, so a parked step must never reach this; returning Fallback makes that loud.
        public Task<PlanResult> ReplanAsync(RunContext ctx, string? failure, Persona persona, AiProvider provider, CancellationToken ct)
            => Task.FromResult(PlanResult.Fallback);
    }

    /// <param name="routed">False ⇒ the plugin service resolves no route for the call (the "Unknown tool." path).</param>
    /// <param name="isMcpTool">True ⇒ the gate derives ToolClass.External, which is what arms the floor.</param>
    /// <param name="secondToolName">A second call the same step turn makes, after the first has already parked.</param>
    /// <param name="faultAfterFirstCall">The provider faults on a later round of the same exchange.</param>
    /// <param name="sessionGrants">Omitted ⇒ not registered at all, so the run has no session tier.</param>
    /// <param name="pluginId">A stable owner: a session grant is keyed on (PluginId, ToolName), so a fact about
    /// it has to route the same owner twice, while the default mints a fresh id per call.</param>
    private (HeadlessRunLauncher Launcher, OneStepPlanner Planner) Build(
        ToolProbe probe, bool routed = true, bool isMcpTool = false, AppSettings? appSettings = null,
        string? secondToolName = null, bool faultAfterFirstCall = false,
        ISessionToolGrantStore? sessionGrants = null, Guid? pluginId = null)
    {
        var provider = new AiProvider { Id = Guid.NewGuid(), Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI };
        var persona = new Persona { Name = "Pia", SystemPrompt = "sys" };
        var planner = new OneStepPlanner();

        var ai = Substitute.For<IAiClientService>();
        ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci => DriveWithToolCall(
                ci.ArgAt<ToolCallHandler?>(3), probe, secondToolName,
                faultAfterFirstCall));

        var plugins = Substitute.For<IPluginService>();
        plugins.IsMcpTool(Arg.Any<string>()).Returns(isMcpTool);
        plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            // Echoes the incoming name, not the probe's, so a two-call turn presents two tools to the gate.
            .Returns(ci =>
            {
                var name = ci.Arg<FunctionCallContent>().Name;
                return routed
                    // A deferred write is the only shape that reaches the gate; a read short-circuits above it.
                    ? ((object? Result, PluginToolCall? PendingAction)?)(null, new PluginToolCall(
                        name, pluginId ?? Guid.NewGuid(), isMcpTool ? "some-mcp-server" : "files", "desc", null,
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
        // Registered only when a fact is about the session tier; its absence keeps every other fact in this
        // file on the gate that has no session grants at all.
        if (sessionGrants is not null)
            services.AddSingleton(sessionGrants);
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
        ToolCallHandler? handler, ToolProbe probe, string? secondToolName = null,
        bool faultAfterFirstCall = false)
    {
        await Task.Yield();
        if (handler is not null)
        {
            probe.Record(await handler(new FunctionCallContent("call-1", probe.ToolName, new Dictionary<string, object?>()), new ToolDispatchContext(1)));

            // The exchange dies on a LATER round than the one that parked. Thrown from inside the stream
            // because that is where a real transport error, a timeout and a truncation all surface.
            if (faultAfterFirstCall)
                throw new InvalidOperationException("provider faulted after the park");

            // A model that keeps going after being told the run is parking. Round-tripped through the SAME
            // handler, because that is the only way the store's first-wins rule is observable.
            if (secondToolName is not null)
                probe.Record(await handler(new FunctionCallContent("call-2", secondToolName, new Dictionary<string, object?>()), new ToolDispatchContext(1)));
        }

        // TEXT STILL FLOWS. Every park fact in this file therefore discriminates on the RUN's state, never on
        // "the step produced nothing" — neutralising the park leaves this reply exactly where it was.
        yield return new TextDelta("reply");
        yield return new Finished(null, "test-model");
    }
}
