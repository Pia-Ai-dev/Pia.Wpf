using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
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

    /// <summary>The run's payload store, built by <see cref="Build"/> so a fact can read the parked rows back.</summary>
    private AgentToolExchangeStore? _exchanges;

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
        _exchanges?.Dispose();
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

    /// <summary>The park raises the tool loop's stop flag, so the exchange ends on the round it parked in instead
    /// of spending every remaining round on a provider round-trip the person is waiting through.</summary>
    [Fact]
    public async Task ParkingACall_RaisesTheLoopStopSignal_AndStillReachesWaitingForInput()
    {
        var probe = new ToolProbe("write_file");
        var (launcher, _) = Build(probe);

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("g", AgentRunTrigger.Schedule, GrantedWrites: []),
            TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        Assert.True(probe.StopAfterFirstCall);

        // The stop must not cost the park itself: the run still parks, naming the tool.
        var run = await GetRunAsync(handle.RunId);
        Assert.Equal(AgentRunState.WaitingForInput, run.State);
        Assert.Equal("write_file", PauseMember(run, "tool"));

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

        await AssertHardDeniedNotParkedAsync(handle.RunId, probe, ToolGateDecision.DeniedNotGranted);
        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>Nobody is expected at the machine for a scheduled run, so an irreversible call has no one to ask and a park
    /// would strand it until somebody happened to look. A run the user started themselves parks instead — see below.</summary>
    [Fact]
    public async Task DeleteLikeBuiltInTool_OnAScheduledRun_StillHardDenies_AndNeverParks()
    {
        var probe = new ToolProbe("delete_file");
        var (launcher, _) = Build(probe);

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("g", AgentRunTrigger.Schedule, GrantedWrites: []),
            TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        await AwaitSettledAsync(handle.RunId);

        await AssertHardDeniedNotParkedAsync(handle.RunId, probe, ToolGateDecision.DeniedNotGranted);
        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>An approved plan hands a foreground run to this executor, so this is the surface a person who pressed Send
    /// actually meets. The park may ask them — and the envelope has to name every path, because the model issues one call per
    /// file in a single round and a card naming the first understates what Continue allows.</summary>
    [Fact]
    public async Task DeleteLikeBuiltInTool_OnATopLevelUserRun_Parks_AndNamesEveryPath()
    {
        var probe = new ToolProbe("delete_file");
        var (launcher, _) = Build(probe, secondToolName: "delete_file",
            firstPath: "fragments/0001-agent-panel.md", secondPath: "fragments/0004-scroll.md");

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("g", AgentRunTrigger.User, GrantedWrites: []),
            TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        var run = await GetRunAsync(handle.RunId);
        Assert.Equal(AgentRunState.WaitingForInput, run.State);
        Assert.Equal("tool-approval", PauseMember(run, "reason"));
        Assert.Equal("delete_file", PauseMember(run, "tool"));

        var args = PauseMember(run, "args");
        Assert.Contains("fragments/0001-agent-panel.md", args);
        Assert.Contains("fragments/0004-scroll.md", args);

        Assert.False(probe.Executed);
        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>What Continue replays is the call, not the card: the arguments are persisted at their full
    /// length beside today's capped display line. The envelope stays capped — this adds a channel.</summary>
    [Fact]
    public async Task AParkPersistsTheCallVerbatim_NotJustTheDisplayString()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = new string('c', 200_000);
        var probe = new ToolProbe("write_file");
        var (launcher, _) = Build(probe, firstPath: "report.md", firstContent: content);

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("g", AgentRunTrigger.Schedule, GrantedWrites: []), ct);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), ct);

        Assert.Equal(1, CountExchangeRows(handle.RunId));
        var row = Assert.Single(await _exchanges!.GetReplayableAsync(handle.RunId, "write_file", ct));

        Assert.Equal(AgentToolExchangeKind.ParkedCall, row.Kind);
        Assert.Equal("write_file", row.ToolName);
        Assert.Equal("call-1", row.CallId);
        Assert.Equal(1, row.Round);
        Assert.Null(row.ResultText);
        Assert.Null(row.ReplayedAt);
        Assert.Null(row.SupersededAt);

        // The property that makes a replay possible at all: the body is there whole, not capped for display.
        var arguments = AgentToolExchangeSerializer.DeserializeArguments(row.ArgumentsJson);
        Assert.NotNull(arguments);
        Assert.Equal(content, ((JsonElement)arguments!["content"]!).GetString());

        Assert.NotNull(row.DisplayArgs);
        Assert.NotEqual(row.ArgumentsJson, row.DisplayArgs);
        Assert.DoesNotContain(content, row.DisplayArgs!, StringComparison.Ordinal);

        // The control: the pause envelope still carries the capped line and nothing more.
        var envelope = PauseMember(await GetRunAsync(handle.RunId), "args");
        Assert.NotNull(envelope);
        Assert.True(envelope!.Length < 1000, "the envelope must stay a display line: " + envelope.Length);

        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>The other half of the reported loss: the run's only real vault write was the SECOND call in the
    /// parked exchange, discarded with the same envelope. It survives as its own replayable row.</summary>
    [Fact]
    public async Task TheSecondCallInAParkedExchange_IsPersistedAsAWithheldRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var probe = new ToolProbe("write_file");
        var (launcher, _) = Build(probe, secondToolName: "create_source",
            firstPath: "report.md", secondPath: "sources/agent-panel.md");

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("g", AgentRunTrigger.Schedule, GrantedWrites: []), ct);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), ct);

        var parked = Assert.Single(await _exchanges!.GetReplayableAsync(handle.RunId, "write_file", ct));
        Assert.Equal(AgentToolExchangeKind.ParkedCall, parked.Kind);

        var withheld = Assert.Single(await _exchanges.GetReplayableAsync(handle.RunId, "create_source", ct));
        Assert.Equal(AgentToolExchangeKind.WithheldCall, withheld.Kind);
        Assert.Equal("call-2", withheld.CallId);
        Assert.Contains("sources/agent-panel.md", withheld.ArgumentsJson!, StringComparison.Ordinal);
        Assert.True(withheld.Seq > parked.Seq, "the withheld call comes after the one that parked");

        // Withheld means withheld: neither call ran, and only the one that parked is in the envelope.
        Assert.False(probe.Executed);
        Assert.Equal("write_file", PauseMember(await GetRunAsync(handle.RunId), "tool"));

        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>Continue means the call itself runs, not merely that the capability is granted — and it runs
    /// BEFORE the step's first provider round-trip, so the model's first view already contains its result.</summary>
    [Fact]
    public async Task ContinuingAPark_ExecutesTheParkedCallOnce_BeforeTheStepReruns()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = new string('c', 200_000);
        var probe = new ToolProbe("write_file");
        var (launcher, _) = Build(probe, firstPath: "report.md", firstContent: content);

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("g", AgentRunTrigger.Schedule, GrantedWrites: []), ct);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), ct);
        Assert.False(probe.Executed); // the premise: it parked rather than ran

        Assert.True(await launcher.ResumeAsync(handle.RunId, ct: ct));
        await AwaitSettledAsync(handle.RunId);

        // The park's dispatch, then the re-run's. Both snapshots predate the terminal purge.
        Assert.Equal(2, probe.Dispatches.Count);
        Assert.Equal(0, probe.Dispatches[0].ExecutedCount);
        var resumed = probe.Dispatches[1];

        // ONCE, and before the request went out — which is what "before the step re-runs" means. Whatever the
        // re-run then does is the model's own call, made with the result already in front of it.
        Assert.Equal(1, resumed.ExecutedCount);
        Assert.Equal("report.md", probe.ExecutedPaths[0]);

        var row = Assert.Single(resumed.Rows, r => r.Kind == AgentToolExchangeKind.ParkedCall);
        Assert.NotNull(row.ReplayedAt);
        Assert.Equal(ExecuteResult, row.ResultText);

        var seeded = Assert.Single(
            resumed.Request.SelectMany(m => m.Contents).OfType<FunctionCallContent>(),
            c => c.Name == "write_file");
        Assert.Contains(resumed.Request.SelectMany(m => m.Contents).OfType<FunctionResultContent>(),
            r => r.CallId == seeded.CallId && (r.Result as string) == ExecuteResult);

        // Capped for the model, verbatim in the row: the tool got the real body, the context does not pay for it.
        var seededContent = ArgumentText(seeded, "content");
        Assert.NotNull(seededContent);
        Assert.True(seededContent!.Length <= AgentToolExchangeSerializer.MaxSeedValueChars + 1,
            "the seeded content must be capped, not the persisted 200 000 chars: " + seededContent.Length);
        Assert.Contains(content, row.ArgumentsJson!, StringComparison.Ordinal);

        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>The seeded pair bypasses the tokenizing decorator entirely, so both halves have to be tokenized
    /// here — otherwise real user content the gate detokenized reaches the provider raw on the next round.</summary>
    [Fact]
    public async Task AReplayedCallIsSeededInItsTokenizedForm()
    {
        var ct = TestContext.Current.CancellationToken;
        const string raw = "+49 170 1234567";
        const string masked = "[Phone_1]";
        var map = Substitute.For<ITokenMapService>();
        map.TokenizeStructuredResult(Arg.Any<string>())
            .Returns(ci => ((string)ci[0]).Replace(raw, masked, StringComparison.Ordinal));

        var probe = new ToolProbe("write_file");
        var (launcher, _) = Build(probe, appSettings: new AppSettings { Privacy = new PrivacySettings { TokenizationEnabled = true } },
            firstPath: "report.md", firstContent: "call me on " + raw,
            executeResult: "wrote report.md for " + raw, tokenMap: map);

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("g", AgentRunTrigger.Schedule, GrantedWrites: []), ct);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), ct);
        Assert.True(await launcher.ResumeAsync(handle.RunId, ct: ct));
        await AwaitSettledAsync(handle.RunId);

        var resumed = probe.Dispatches[1];
        var contents = resumed.Request.SelectMany(m => m.Contents).ToList();

        // The CALL half: a build that tokenizes only the result fails right here.
        var call = Assert.Single(contents.OfType<FunctionCallContent>(), c => c.Name == "write_file");
        var seededContent = ArgumentText(call, "content");
        Assert.Contains(masked, seededContent!, StringComparison.Ordinal);

        // The RESULT half.
        var result = Assert.Single(contents.OfType<FunctionResultContent>(), r => r.CallId == call.CallId);
        Assert.Contains(masked, (string)result.Result!, StringComparison.Ordinal);

        // And nowhere in the request at all — including any prose the run wrote around it.
        Assert.DoesNotContain(raw, Flatten(resumed.Request), StringComparison.Ordinal);

        // The row keeps the detokenized truth: it is what the GATE saw, and what a further replay would run.
        var row = Assert.Single(resumed.Rows, r => r.Kind == AgentToolExchangeKind.ParkedCall);
        var persisted = AgentToolExchangeSerializer.DeserializeArguments(row.ArgumentsJson);
        Assert.Contains(raw, ((JsonElement)persisted!["content"]!).GetString()!, StringComparison.Ordinal);

        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>A replay is a best-effort seed, never a verdict on the step: it runs outside the step's own
    /// try/catch, and the row was claimed before the call, so a failure is consumed rather than retried.</summary>
    [Fact]
    public async Task AReplayThatFaults_SeedsTheStepWithTheFailure_AndIsNeverRetried()
    {
        var ct = TestContext.Current.CancellationToken;
        var probe = new ToolProbe("write_file");
        var (launcher, _) = Build(probe, firstPath: "report.md", faultOnExecute: true);

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("g", AgentRunTrigger.Schedule, GrantedWrites: []), ct);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), ct);
        Assert.True(await launcher.ResumeAsync(handle.RunId, ct: ct));
        await AwaitSettledAsync(handle.RunId);

        // The step is not failed by a replay that threw.
        Assert.Equal(AgentRunState.Completed, (await GetRunAsync(handle.RunId)).State);

        var resumed = probe.Dispatches[1];
        Assert.Equal(1, resumed.ExecutedCount);

        // Consumed, not retried: ReplayedAt was stamped before the call, so no later resume offers it again.
        var row = Assert.Single(resumed.Rows, r => r.Kind == AgentToolExchangeKind.ParkedCall);
        Assert.NotNull(row.ReplayedAt);
        Assert.Contains(ExecuteFailure, row.ResultText!, StringComparison.Ordinal);

        // The step sees an ordinary tool exchange whose result happens to be an error.
        var call = Assert.Single(
            resumed.Request.SelectMany(m => m.Contents).OfType<FunctionCallContent>(),
            c => c.Name == "write_file");
        var seeded = Assert.Single(
            resumed.Request.SelectMany(m => m.Contents).OfType<FunctionResultContent>(),
            r => r.CallId == call.CallId);
        Assert.Contains(ExecuteFailure, (string)seeded.Result!, StringComparison.Ordinal);

        Assert.Contains(_timeline.Rows,
            r => r.ToolName == "write_file" && r.Outcome == AgentTimelineOutcome.Error);

        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>A refusal is an answer about the payload too: the declined tool's rows go, and ONLY its rows —
    /// a run-wide delete would take another tool's surviving withheld call with them.</summary>
    [Fact]
    public async Task DecliningAPark_PurgesTheParkedCall_AndReplaysNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var probe = new ToolProbe("write_file");
        var (launcher, _) = Build(probe, secondToolName: "update_todo",
            firstPath: "report.md", secondPath: "todo.md");

        // The second tool is granted, so the declined resume can still finish the step.
        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("g", AgentRunTrigger.Schedule, GrantedWrites: ["update_todo"]), ct);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), ct);
        Assert.Equal(2, CountExchangeRows(handle.RunId)); // the premise: both calls were persisted

        Assert.True(await launcher.ResumeAsync(handle.RunId, ct: ct, declineToolApproval: true));
        await AwaitSettledAsync(handle.RunId);

        var resumed = probe.Dispatches[1];
        Assert.DoesNotContain(resumed.Rows, r => r.ToolName == "write_file");
        // Scoped to the declined tool alone: this is the row a later grant still replays.
        Assert.Contains(resumed.Rows, r => r.ToolName == "update_todo" && r.Kind == AgentToolExchangeKind.WithheldCall);

        // Nothing was replayed before the re-run, and the declined tool never ran at all.
        Assert.Equal(0, resumed.ExecutedCount);
        Assert.DoesNotContain("write_file", probe.ExecutedNames);

        Assert.Contains(probe.Results, r => r?.Contains("Denied", StringComparison.Ordinal) == true);
        Assert.Contains(_timeline.Rows,
            r => r.ToolName == "write_file" && r.Decision == ToolGateDecision.DeniedForRun);

        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>What a person approved is the tool and everything it was about to do with it, so a four-file
    /// approval replays four calls — in the order they were made, not newest-first and not the first alone.</summary>
    [Fact]
    public async Task AMultiCallPark_ReplaysEveryCallInCallOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var probe = new ToolProbe("write_file");
        var (launcher, _) = Build(probe, secondToolName: "write_file",
            firstPath: "first.md", secondPath: "second.md");

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("g", AgentRunTrigger.Schedule, GrantedWrites: []), ct);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), ct);
        Assert.Equal(2, CountExchangeRows(handle.RunId));

        Assert.True(await launcher.ResumeAsync(handle.RunId, ct: ct));
        await AwaitSettledAsync(handle.RunId);

        var resumed = probe.Dispatches[1];
        Assert.Equal(2, resumed.ExecutedCount);
        // The persisted Seq order, not a set: a newest-first read would report second.md then first.md.
        Assert.Equal(["first.md", "second.md"], probe.ExecutedPaths.Take(2));

        var rows = resumed.Rows.Where(r => r.ToolName == "write_file").ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.NotNull(r.ReplayedAt));
        // The withheld sibling is replayed too — it is part of what the person said yes to.
        Assert.Contains(rows, r => r.Kind == AgentToolExchangeKind.WithheldCall);

        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>The exact loss from the reported run: the run parked on <c>write_file</c> and the
    /// <c>create_source</c> holding the document body was discarded with it. One Continue grants ONE tool, so
    /// that call is not replayed here — but it survives whole, and a later park on it is what replays it.</summary>
    [Fact]
    public async Task AWithheldUngrantedCall_SurvivesTheGrantOfTheParkedTool_WithItsArgumentsIntact()
    {
        var ct = TestContext.Current.CancellationToken;
        var body = new string('s', 50_000);
        var probe = new ToolProbe("write_file");
        var (launcher, _) = Build(probe, dispatchScript:
        [
            [new ScriptedCall("write_file", "report.md"),
             new ScriptedCall("create_source", "sources/agent-panel.md", body)],
            [new ScriptedCall("write_file", "report.md")],
        ]);

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("g", AgentRunTrigger.Schedule, GrantedWrites: []), ct);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), ct);

        Assert.True(await launcher.ResumeAsync(handle.RunId, ct: ct));
        await AwaitSettledAsync(handle.RunId);

        // Off the re-run's snapshot: the terminal purge takes the rows before the test could read them.
        var resumed = probe.Dispatches[1];
        Assert.NotNull(Assert.Single(resumed.Rows, r => r.ToolName == "write_file").ReplayedAt);

        var withheld = Assert.Single(resumed.Rows, r => r.ToolName == "create_source");
        Assert.Equal(AgentToolExchangeKind.WithheldCall, withheld.Kind);
        Assert.Null(withheld.ReplayedAt);
        Assert.Null(withheld.SupersededAt);

        // The whole body, which is what the run had to compose a second time before the store existed.
        var arguments = AgentToolExchangeSerializer.DeserializeArguments(withheld.ArgumentsJson);
        Assert.NotNull(arguments);
        Assert.Equal(body, ((JsonElement)arguments!["content"]!).GetString());

        // Nobody was asked about create_source, so granting write_file must not have run it.
        Assert.DoesNotContain("create_source", probe.ExecutedNames);

        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>A call withheld by the park that the run ALREADY held a grant for is not the approved tool, so
    /// it stays unreplayed and the re-run performs it — once across the park's whole life. A predicate that
    /// drifted to the grant set instead of the approved tool runs it twice.</summary>
    [Fact]
    public async Task AWithheldCallOfAnAlreadyGrantedTool_IsNotReplayed_AndStillRunsExactlyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var probe = new ToolProbe("write_file");
        var (launcher, _) = Build(probe, secondToolName: "update_todo",
            firstPath: "report.md", secondPath: "todo.md");

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("g", AgentRunTrigger.Schedule, GrantedWrites: ["update_todo"]), ct);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), ct);
        Assert.False(probe.Executed); // the premise: the park withheld the granted call too

        Assert.True(await launcher.ResumeAsync(handle.RunId, ct: ct));
        await AwaitSettledAsync(handle.RunId);

        var resumed = probe.Dispatches[1];
        // Snapshotted after the replay and before the re-run: nothing claimed the granted tool's row.
        var withheld = Assert.Single(resumed.Rows, r => r.ToolName == "update_todo");
        Assert.Equal(AgentToolExchangeKind.WithheldCall, withheld.Kind);
        Assert.Null(withheld.ReplayedAt);

        // One execution before the re-run's request went out, and it is the approved tool's.
        Assert.Equal(1, resumed.ExecutedCount);
        Assert.Equal("write_file", probe.ExecutedNames[0]);

        // The re-run is the only time it happens, which is what the withheld row bought.
        Assert.Equal(1, probe.ExecutedNames.Count(n => n == "update_todo"));

        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>Two parks on one tool leave two replayable rows carrying different arguments, and one Continue
    /// would write both. The later park is the model's current intent, so it stales the earlier row.</summary>
    [Fact]
    public async Task ASecondParkOnTheSameTool_SupersedesTheStaleWithheldRow_SoTheGrantWritesOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var probe = new ToolProbe("write_file");
        var (launcher, _) = Build(probe, dispatchScript:
        [
            [new ScriptedCall("write_file", "report.md"), new ScriptedCall("create_source", "sources/first.md")],
            [new ScriptedCall("create_source", "sources/second.md")],
            [],
        ]);

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("g", AgentRunTrigger.Schedule, GrantedWrites: []), ct);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), ct);

        Assert.True(await launcher.ResumeAsync(handle.RunId, ct: ct));
        await AwaitParkedAsync(handle.RunId);
        // The second park is on the tool the first one withheld, which is what makes the rows collide.
        Assert.Equal("create_source", PauseMember(await GetRunAsync(handle.RunId), "tool"));

        Assert.True(await launcher.ResumeAsync(handle.RunId, ct: ct));
        await AwaitSettledAsync(handle.RunId);

        var rows = probe.Dispatches[2].Rows.Where(r => r.ToolName == "create_source").ToList();
        Assert.Equal(2, rows.Count);

        var stale = Assert.Single(rows, r => r.Kind == AgentToolExchangeKind.WithheldCall);
        Assert.NotNull(stale.SupersededAt);
        Assert.Null(stale.ReplayedAt);
        Assert.Contains("sources/first.md", stale.ArgumentsJson!, StringComparison.Ordinal);

        var current = Assert.Single(rows, r => r.Kind == AgentToolExchangeKind.ParkedCall);
        Assert.NotNull(current.ReplayedAt);

        // Once, with the newest arguments: without the supersede both rows replay and the source is created twice.
        Assert.Equal(1, probe.ExecutedNames.Count(n => n == "create_source"));
        Assert.Equal("sources/second.md", probe.ExecutedPaths[probe.ExecutedNames.IndexOf("create_source")]);

        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// THE PATH FG2 ACTUALLY TAKES, and it is not the launch path: a Planned run starts on LiveTurnExecutor,
    /// parks for plan approval, and Approve resumes it into the headless executor. Both facts the park needs —
    /// CanPark from the launcher and IsTopLevelUserRun from the row — have to survive that hand-off.
    /// </summary>
    [Fact]
    public async Task AnApprovedPlanResume_OfAUserRun_StillParksOnADelete()
    {
        var probe = new ToolProbe("delete_file");
        var (launcher, _) = Build(probe, firstPath: "fragments/0001-agent-panel.md");

        var parked = await ParkedUserRunAwaitingApprovalAsync();
        Assert.True(await launcher.ResumeAsync(parked.Id, ct: TestContext.Current.CancellationToken));
        await AwaitParkedAsync(parked.Id);

        var run = await GetRunAsync(parked.Id);
        Assert.Equal("tool-approval", PauseMember(run, "reason"));
        Assert.Equal("delete_file", PauseMember(run, "tool"));
        Assert.Contains("fragments/0001-agent-panel.md", PauseMember(run, "args"));
        Assert.False(probe.Executed);

        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>A delegate never acquires authority its parent narrowed away — including the authority to put an irreversible
    /// call in front of a person. The tool here is the one a top-level user run now parks on.</summary>
    [Fact]
    public async Task DeleteLikeBuiltInTool_OnAChildRun_StillHardDenies()
    {
        var probe = new ToolProbe("delete_file");
        var (launcher, _) = Build(probe, firstPath: "fragments/0001.md");

        var parent = await NewRunAsync();
        var child = await ParkedChildAsync(parent.Id);
        Assert.True(await launcher.ResumeAsync(child.Id, ct: TestContext.Current.CancellationToken));
        await AwaitSettledAsync(child.Id);

        await AssertHardDeniedNotParkedAsync(child.Id, probe, ToolGateDecision.DeniedNotGranted);
        await launcher.StopAsync(CancellationToken.None);
    }

    /// <summary>The Flow card is the other affordance, and it has to say the same thing: the tool AND what it would act on.
    /// An envelope with no args keeps the one-placeholder sentence rather than trailing off after "on ".</summary>
    [Fact]
    public void TheFlowContinueCard_NamesWhatTheCallWouldActOn()
    {
        var loc = Substitute.For<ILocalizationService>();
        loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        loc.Format(Arg.Any<string>(), Arg.Any<object[]>())
            .Returns(ci => (string)ci[0] + "|" + string.Join(',', ((object[])ci[1]).Select(a => a?.ToString())));

        var withArgs = new AgentRun
        {
            Id = Guid.NewGuid(),
            ExtraJson = """{"paused":true,"reason":"tool-approval","tool":"delete_file","args":"path=fragments/0001.md"}""",
        };
        Assert.Equal(
            "Flow_Run_ToolApprovalOn|delete_file,path=fragments/0001.md",
            AgentRunNotificationSurface.PausedBody(loc, withArgs));

        var withoutArgs = new AgentRun
        {
            Id = Guid.NewGuid(),
            ExtraJson = """{"paused":true,"reason":"tool-approval","tool":"git_commit"}""",
        };
        Assert.Equal("Flow_Run_ToolApproval|git_commit", AgentRunNotificationSurface.PausedBody(loc, withoutArgs));
    }

    /// <summary>The complement of the two facts above, and the reason the Tool access page is not lying: "Always" is offered for
    /// exactly the tools the park refuses to ask about, so a grant that stopped at the interactive gate bought those nothing.</summary>
    [Fact]
    public async Task StandingGrant_RunsTheDeleteLikeToolTheParkWillNotEvenAskAbout()
    {
        var pluginId = Guid.NewGuid();
        var permissions = Substitute.For<IToolPermissionService>();
        permissions.IsGranted(pluginId, "delete_file").Returns(true);
        var probe = new ToolProbe("delete_file");
        var (launcher, _) = Build(probe, pluginId: pluginId, permissions: permissions);

        var handle = await launcher.LaunchAsync(
            new HeadlessRunRequest("g", AgentRunTrigger.Schedule, GrantedWrites: []),
            TestContext.Current.CancellationToken);
        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        await AwaitSettledAsync(handle.RunId);

        Assert.True(probe.Executed);
        var run = await GetRunAsync(handle.RunId);
        Assert.Equal(AgentRunState.Completed, run.State);

        var row = Assert.Single(_timeline.Rows, r => r.ToolName == "delete_file");
        Assert.Equal(ToolGateDecision.AutoApprovedStandingGrant, row.Decision);
        Assert.Equal(AgentTimelineOutcome.Ok, row.Outcome);

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

    /// <summary>Not delete-like, so only the park's own External clause stops it — the case that used to fall
    /// between two guards.</summary>
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

    /// <summary>The route's result on a successful execution.</summary>
    private const string ExecuteResult = "did it";

    /// <summary>What a route configured to fault throws.</summary>
    private const string ExecuteFailure = "the write failed";

    /// <summary>Every exchange row as it stands right now, on its OWN connection: this runs on the dispatch
    /// thread, and the shared context connection belongs to the test thread.</summary>
    private List<ExchangeRowSnapshot> ReadExchangeRows()
    {
        using var connection = new SqliteConnection(_ctx.ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Kind, ToolName, ArgumentsJson, ResultText, ReplayedAt, SupersededAt "
            + "FROM AgentToolExchanges ORDER BY Seq;";
        using var reader = cmd.ExecuteReader();

        var rows = new List<ExchangeRowSnapshot>();
        while (reader.Read())
        {
            rows.Add(new ExchangeRowSnapshot(
                (AgentToolExchangeKind)reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return rows;
    }

    /// <summary>A string argument as the gate saw it — a replayed call carries <c>JsonElement</c> values.</summary>
    private static string? ArgumentText(FunctionCallContent call, string name)
    {
        if (call.Arguments is null || !call.Arguments.TryGetValue(name, out var value))
            return null;

        return value switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } el => el.GetString(),
            _ => null,
        };
    }

    /// <summary>Every tool call, tool result and text of a captured request, flattened for a "nowhere" assertion.</summary>
    private static string Flatten(IEnumerable<ChatMessage> request) =>
        string.Join("\n", request.Select(m => m.Text + "\n" + string.Join("\n", m.Contents.Select(c => c switch
        {
            // The argument VALUES, not their JSON: a serializer escape would hide a raw value from a
            // "nowhere in the request" assertion.
            FunctionCallContent call => string.Join(" ", call.Arguments?.Values.Select(v => v?.ToString()) ?? []),
            FunctionResultContent result => result.Result as string ?? string.Empty,
            _ => string.Empty,
        }))));

    private int CountExchangeRows(Guid runId)
    {
        using var cmd = _ctx.GetConnection().CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM AgentToolExchanges WHERE RunId = @RunId";
        cmd.Parameters.AddWithValue("@RunId", runId.ToString());
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

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

    private async Task<AgentRun> NewRunAsync(
        Guid? parentRunId = null, string? policyJson = null, AgentRunTrigger trigger = AgentRunTrigger.Schedule)
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
            new AgentRunCreateRequest(chatId, RunShape.Planned, trigger, Goal: "g",
                PolicyJson: policyJson, ParentRunId: parentRunId), ct);
    }

    /// <summary>The shape a plan-approval park leaves behind: a top-level USER run with a Pending step,
    /// waiting for Approve. Resuming it is what hands a foreground run to the headless executor.</summary>
    private async Task<AgentRun> ParkedUserRunAwaitingApprovalAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await NewRunAsync(
            policyJson: HeadlessRunLauncher.SerializeGrantEnvelope([], AgentRunTrigger.User),
            trigger: AgentRunTrigger.User);
        await _runs.ReplaceStepsAsync(run.Id, [new AgentStep
        {
            Id = Guid.NewGuid(),
            RunId = run.Id,
            Ordinal = 0,
            Title = "S1",
            Intent = "delete the merged fragments",
            Status = AgentStepStatus.Pending,
        }], ct);
        await _runs.PauseAsync(run.Id, "plan-approval", ct);
        return run;
    }

    /// <summary>Polls until the run is parked again.</summary>
    private async Task AwaitParkedAsync(Guid runId)
    {
        var ct = TestContext.Current.CancellationToken;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if ((await _runs.GetAsync(runId, ct))!.State == AgentRunState.WaitingForInput)
                return;
            await Task.Delay(20, ct);
        }

        Assert.Equal(AgentRunState.WaitingForInput, (await _runs.GetAsync(runId, ct))!.State);
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

        /// <summary>The <c>path</c> argument of each of those executions, in the same order.</summary>
        public List<string?> ExecutedPaths { get; } = [];

        /// <summary>One entry per provider dispatch, taken as the request is handed over.</summary>
        public List<DispatchSnapshot> Dispatches { get; } = [];

        /// <summary>Every string the gate handed back, in call order.</summary>
        public List<string?> Results { get; } = [];

        /// <summary>Whether the round's stop flag was raised once the first call came back.</summary>
        public bool StopAfterFirstCall { get; set; }

        public bool Executed => ExecutedNames.Count > 0;

        /// <summary>The first call's gate result.</summary>
        public string? GateResult => Results.Count > 0 ? Results[0] : null;

        public void MarkExecuted(string name, string? path)
        {
            ExecutedNames.Add(name);
            ExecutedPaths.Add(path);
        }

        public void Record(object? gateResult) => Results.Add(gateResult as string);
    }

    /// <summary>What one provider dispatch was handed, plus the state a settled run no longer has: the exchange
    /// rows, and how many tool executions had already happened when the request went out.</summary>
    private sealed record DispatchSnapshot(
        List<ChatMessage> Request, List<ExchangeRowSnapshot> Rows, int ExecutedCount);

    private sealed record ExchangeRowSnapshot(
        AgentToolExchangeKind Kind, string? ToolName, string? ArgumentsJson, string? ResultText,
        string? ReplayedAt, string? SupersededAt);

    /// <summary>One tool call a scripted dispatch makes.</summary>
    private sealed record ScriptedCall(string ToolName, string? Path = null, string? Content = null);

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
    /// <param name="isMcpTool">True ⇒ the gate derives ToolClass.External, which the park (and the session
    /// tier, unattended) refuse to ask about or honour.</param>
    /// <param name="secondToolName">A second call the same step turn makes, after the first has already parked.</param>
    /// <param name="faultAfterFirstCall">The provider faults on a later round of the same exchange.</param>
    /// <param name="sessionGrants">Omitted ⇒ not registered at all, so the run has no session tier.</param>
    /// <param name="pluginId">A stable owner: a session grant is keyed on (PluginId, ToolName), so a fact about
    /// it has to route the same owner twice, while the default mints a fresh id per call.</param>
    /// <param name="permissions">Omitted ⇒ an all-false substitute, i.e. no standing grants. Registered either
    /// way, unlike <paramref name="sessionGrants"/>: the runner reads this tier on every call.</param>
    /// <param name="faultOnExecute">The route throws on its FIRST execution — which, on a parked tool, is the
    /// replay: nothing ran before the park.</param>
    /// <param name="executeResult">What a successful route hands back, so a fact about tokenization can put a
    /// masked value in the result half as well as the argument half.</param>
    /// <param name="tokenMap">Omitted ⇒ a pass-through map, i.e. tokenization that changes nothing.</param>
    /// <param name="dispatchScript">One entry per provider dispatch, so a two-park scenario is expressible; a
    /// dispatch past its end makes no call. Omitted ⇒ every dispatch drives the same two calls as before.</param>
    private (HeadlessRunLauncher Launcher, OneStepPlanner Planner) Build(
        ToolProbe probe, bool routed = true, bool isMcpTool = false, AppSettings? appSettings = null,
        string? secondToolName = null, bool faultAfterFirstCall = false,
        ISessionToolGrantStore? sessionGrants = null, Guid? pluginId = null,
        IToolPermissionService? permissions = null,
        string? firstPath = null, string? secondPath = null, string? firstContent = null,
        bool faultOnExecute = false, string executeResult = ExecuteResult, ITokenMapService? tokenMap = null,
        IReadOnlyList<IReadOnlyList<ScriptedCall>>? dispatchScript = null)
    {
        List<ScriptedCall> everyDispatch = [new(probe.ToolName, firstPath, firstContent)];
        if (secondToolName is not null)
            everyDispatch.Add(new ScriptedCall(secondToolName, secondPath));

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
                // Snapshotted per dispatch: the terminal EndRunAsync purges the rows, so a settled run has none
                // left to read, and the request captured HERE is the one the replay had to land ahead of.
                probe.Dispatches.Add(new DispatchSnapshot(
                    [.. ci.ArgAt<IList<ChatMessage>>(0)], ReadExchangeRows(), probe.ExecutedNames.Count));
                // Resolved here rather than inside the iterator, so the dispatch number is the one that was
                // just counted and not whatever it is when the stream is first pulled.
                var calls = dispatchScript is null
                    ? everyDispatch
                    : dispatchScript.ElementAtOrDefault(probe.Dispatches.Count - 1) ?? [];
                return DriveWithToolCall(ci.ArgAt<ToolCallHandler?>(3), probe, calls, faultAfterFirstCall);
            });

        var plugins = Substitute.For<IPluginService>();
        plugins.IsMcpTool(Arg.Any<string>()).Returns(isMcpTool);
        plugins.RouteToolCallAsync(Arg.Any<FunctionCallContent>(), Arg.Any<CancellationToken>())
            // Echoes the incoming name, not the probe's, so a two-call turn presents two tools to the gate.
            .Returns(ci =>
            {
                var call = ci.Arg<FunctionCallContent>();
                var name = call.Name;
                return routed
                    // A deferred write is the only shape that reaches the gate; a read short-circuits above it.
                    ? ((object? Result, PluginToolCall? PendingAction)?)(null, new PluginToolCall(
                        name, pluginId ?? Guid.NewGuid(), isMcpTool ? "some-mcp-server" : "files", "desc", null,
                        () =>
                        {
                            probe.MarkExecuted(name, ArgumentText(call, "path"));
                            if (faultOnExecute && probe.ExecutedNames.Count == 1)
                                throw new InvalidOperationException(ExecuteFailure);
                            return Task.FromResult<object?>(executeResult);
                        }))
                    // NULL, not a tuple of nulls: `route is null` is what the handler tests for, and a
                    // (null, null) tuple would fall out at "Tool call handled." instead — a different path.
                    : null;
            });

        var composer = Substitute.For<IAssistantPromptComposer>();
        composer.PrepareTurn(Arg.Any<Persona>(), Arg.Any<AiProvider>(), Arg.Any<IReadOnlyList<AtCommand>>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<string?>())
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
        // ONE instance, and a pass-through by default: the executor resolves the factory once, and a bare
        // substitute would answer every tokenize call with an empty string.
        var map = tokenMap ?? PassThroughTokenMap();
        services.AddSingleton<Func<ITokenMapService>>(_ => () => map);
        services.AddSingleton<IExecutingRunStore>(_executing);
        // The audit sink is REAL wiring here (the launcher suite omits it): the park's own timeline row is
        // one of the facts, and a decision nobody records is a decision nobody can be shown.
        services.AddSingleton<IAgentTimelineService>(_timeline);
        // Registered only when a fact is about the session tier; its absence keeps every other fact in this
        // file on the gate that has no session grants at all.
        if (sessionGrants is not null)
            services.AddSingleton(sessionGrants);
        services.AddSingleton(permissions ?? Substitute.For<IToolPermissionService>());
        // Real wiring, like the audit sink above: what a park leaves behind for a Continue press to replay is
        // one of the facts, and the executor resolves this off the container.
        _exchanges = new AgentToolExchangeStore(_ctx, NullLogger<AgentToolExchangeStore>.Instance);
        services.AddSingleton<IAgentToolExchangeStore>(_exchanges);
        services.AddTransient<BackgroundAssistantTurnRunner>();
        services.AddTransient<HeadlessTurnExecutor>();
        services.AddTransient<AgentRunOrchestrator>();
        var sp = services.BuildServiceProvider();

        var launcher = new HeadlessRunLauncher(
            sp.GetRequiredService<IServiceScopeFactory>(), _chats, _runs, settings, providers, personas,
            _executing, NullLogger<HeadlessRunLauncher>.Instance, runsBaseDirOverride: _runsBase,
            exchangeStore: _exchanges);
        return (launcher, planner);
    }

    private static ITokenMapService PassThroughTokenMap()
    {
        var map = Substitute.For<ITokenMapService>();
        map.TokenizeStructuredResult(Arg.Any<string>()).Returns(ci => (string)ci[0]);
        return map;
    }

    private static Dictionary<string, object?> PathArgs(string? path, string? content = null)
    {
        var arguments = new Dictionary<string, object?>();
        if (path is not null) arguments["path"] = path;
        if (content is not null) arguments["content"] = content;
        return arguments;
    }

    private static async IAsyncEnumerable<ChatStreamItem> DriveWithToolCall(
        ToolCallHandler? handler, ToolProbe probe, IReadOnlyList<ScriptedCall> calls,
        bool faultAfterFirstCall = false)
    {
        await Task.Yield();
        if (handler is not null)
        {
            // ONE signal for the round, as the real loop builds it: every call below is the same round.
            var stop = new ToolLoopStopSignal();
            for (var i = 0; i < calls.Count; i++)
            {
                // Every call after the first is a model that keeps going after being told the run is parking.
                // Round-tripped through the SAME handler, because that is the only way the store's
                // first-wins rule is observable, and unconditional on the stop: the production loop answers
                // the round's remaining calls too.
                probe.Record(await handler(
                    new FunctionCallContent($"call-{i + 1}", calls[i].ToolName,
                        PathArgs(calls[i].Path, calls[i].Content)),
                    new ToolDispatchContext(1, stop)));

                if (i > 0) continue;

                probe.StopAfterFirstCall = stop.IsStopRequested;

                // The exchange dies after the park. Thrown from inside the stream because that is where a real
                // transport error, a timeout and a truncation all surface.
                if (faultAfterFirstCall)
                    throw new InvalidOperationException("provider faulted after the park");
            }
        }

        // TEXT STILL FLOWS. Every park fact in this file therefore discriminates on the RUN's state, never on
        // "the step produced nothing" — neutralising the park leaves this reply exactly where it was.
        yield return new TextDelta("reply");
        yield return new Finished(null, "test-model");
    }
}
