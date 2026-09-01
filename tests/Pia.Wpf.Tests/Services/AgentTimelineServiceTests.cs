using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>Every run here needs a REAL chat + run row, because the enforced <c>RunId</c> FK would reject the
/// INSERT and drop it — and <c>Emit</c> is fire-and-forget, so a test that observes drains first.</summary>
public sealed class AgentTimelineServiceTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string _dbPath;
    private readonly SqliteContext _ctx;
    private readonly AgentRunService _runs;
    private readonly AssistantChatService _chats;
    private readonly AgentTimelineService _service;

    public AgentTimelineServiceTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "PiaTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        _dbPath = Path.Combine(_tmpDir, "history.db");
        _ctx = new SqliteContext(_dbPath);
        _runs = new AgentRunService(_ctx, NullLogger<AgentRunService>.Instance);
        _chats = new AssistantChatService(_ctx, _runs);
        _service = new AgentTimelineService(_ctx, NullLogger<AgentTimelineService>.Instance);
    }

    [Fact]
    public async Task Emit_ThenGetForRun_ReturnsTheRowsInSeqOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await MakeRunAsync();
        var scope = new AgentTimelineScope(_service, run.Id, stepId: null);

        for (var i = 0; i < 5; i++)
        {
            scope.Emit(ToolGateSurface.Unattended, $"tool_{i}", ToolClass.Files, Guid.NewGuid(),
                ToolGateDecision.GrantedByName, AgentTimelineOutcome.Ok, argsChars: 10, resultChars: 20, durationMs: 3, toolCallId: null, round: null, requestedAt: null, decidedAt: null);
        }

        var rows = await _service.GetForRunAsync(run.Id, ct);

        Assert.Equal(5, rows.Count);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, rows.Select(r => r.Seq).ToArray());
        Assert.Equal(new[] { "tool_0", "tool_1", "tool_2", "tool_3", "tool_4" }, rows.Select(r => r.ToolName).ToArray());
        Assert.Equal(5, rows.Select(r => r.Id).Distinct().Count());
        Assert.All(rows, r =>
        {
            Assert.Equal(AgentTimelineEventKind.ToolCall, r.Kind);
            Assert.Equal(ToolGateDecision.GrantedByName, r.Decision);
            Assert.Equal(AgentTimelineOutcome.Ok, r.Outcome);
            Assert.Equal(ToolClass.Files, r.ToolClass);
            Assert.Equal(10, r.ArgsChars);
            Assert.Equal(20, r.ResultChars);
            Assert.Equal(3, r.DurationMs);
            // Read from the row's OWN column rather than the record default, so this is a write-then-read fact.
            Assert.Equal(2, r.SchemaVersion);
        });
    }

    [Fact]
    public async Task Emit_AllocatesSeqSynchronously_EvenUnderConcurrentCallers()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await MakeRunAsync();
        var scope = new AgentTimelineScope(_service, run.Id, stepId: null);

        // 8 concurrent producers × 25 rows. Seq is allocated under the lock, so the 200 values must be
        // exactly 1..200 with no duplicate even though the writes land asynchronously.
        var workers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 25; i++)
            {
                scope.Emit(ToolGateSurface.Unattended, "write_file", ToolClass.Files, null,
                    ToolGateDecision.GrantedByName, AgentTimelineOutcome.Ok, toolCallId: null, round: null, requestedAt: null, decidedAt: null);
            }
        }, ct)).ToArray();
        await Task.WhenAll(workers);

        var rows = await _service.GetForRunAsync(run.Id, ct);

        Assert.Equal(200, rows.Count);
        Assert.Equal(Enumerable.Range(1, 200).Select(i => (long)i).ToArray(), rows.Select(r => r.Seq).ToArray());
    }

    [Fact]
    public async Task SeqContinuesAcrossAProcessBoundary()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await MakeRunAsync();
        var scope = new AgentTimelineScope(_service, run.Id, stepId: null);
        for (var i = 0; i < 3; i++)
            scope.Emit(ToolGateSurface.Unattended, "write_file", ToolClass.Files, null, ToolGateDecision.GrantedByName, AgentTimelineOutcome.Ok, toolCallId: null, round: null, requestedAt: null, decidedAt: null);
        await _service.DrainAsync();
        _service.Dispose();

        // A run parked in one process and resumed in another: the second instance must continue the sequence.
        using var second = new AgentTimelineService(_ctx, NullLogger<AgentTimelineService>.Instance);
        new AgentTimelineScope(second, run.Id, stepId: null)
            .Emit(ToolGateSurface.Unattended, "write_file", ToolClass.Files, null, ToolGateDecision.GrantedByName, AgentTimelineOutcome.Ok, toolCallId: null, round: null, requestedAt: null, decidedAt: null);

        var rows = await second.GetForRunAsync(run.Id, ct);
        Assert.Equal(4, rows.Count);
        Assert.Equal(4, rows[^1].Seq);
    }

    [Fact]
    public async Task AFailedSeedIsRetried_SoAResumedRunNeverDuplicatesSeq()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await MakeRunAsync();
        var scope = new AgentTimelineScope(_service, run.Id, stepId: null);
        for (var i = 0; i < 3; i++)
            scope.Emit(ToolGateSurface.Unattended, "write_file", ToolClass.Files, null, ToolGateDecision.GrantedByName, AgentTimelineOutcome.Ok, toolCallId: null, round: null, requestedAt: null, decidedAt: null);
        await _service.DrainAsync();
        _service.Dispose();

        // Make the seed query fail the way SQLITE_BUSY would. A rename, not a drop, so the parked segment's
        // three rows stay alive for the resumed segment to collide with.
        Exec("ALTER TABLE AgentTimelineEvents RENAME TO AgentTimelineEvents_hidden;");

        using var second = new AgentTimelineService(_ctx, NullLogger<AgentTimelineService>.Instance);
        var resumed = new AgentTimelineScope(second, run.Id, stepId: null);
        resumed.Emit(ToolGateSurface.Unattended, "write_file", ToolClass.Files, null, ToolGateDecision.GrantedByName, AgentTimelineOutcome.Ok, toolCallId: null, round: null, requestedAt: null, decidedAt: null);
        await second.DrainAsync();

        // Table back: the store is readable again and the slot must NOT still be trusting its failed seed.
        Exec("ALTER TABLE AgentTimelineEvents_hidden RENAME TO AgentTimelineEvents;");

        resumed.Emit(ToolGateSurface.Unattended, "write_file", ToolClass.Files, null, ToolGateDecision.GrantedByName, AgentTimelineOutcome.Ok, toolCallId: null, round: null, requestedAt: null, decidedAt: null);

        var rows = await second.GetForRunAsync(run.Id, ct);

        // Caching the failed seed restarted the sequence at 1, duplicating the parked segment's second row;
        // retrying the aggregate is what makes this 4.
        Assert.Equal(new long[] { 1, 2, 3, 4 }, rows.Select(r => r.Seq).ToArray());
        Assert.Equal(rows.Count, rows.Select(r => r.Seq).Distinct().Count());
    }

    /// <summary>DDL against the SHARED context connection — the store holds its own, so this is how a test
    /// changes the world under it.</summary>
    private void Exec(string sql)
    {
        using var cmd = _ctx.GetConnection().CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task Emit_NeverThrows_WhenTheStoreIsBroken()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await MakeRunAsync();
        _service.Dispose();

        // No throw, and nothing written.
        new AgentTimelineScope(_service, run.Id, stepId: null)
            .Emit(ToolGateSurface.Interactive, "write_file", ToolClass.Files, null, ToolGateDecision.ApprovedOnce, AgentTimelineOutcome.Ok, toolCallId: null, round: null, requestedAt: null, decidedAt: null);

        using var reader = new AgentTimelineService(_ctx, NullLogger<AgentTimelineService>.Instance);
        Assert.Empty(await reader.GetForRunAsync(run.Id, ct));
    }

    [Fact]
    public async Task RoundTrips_ToolCallId_Round_RequestedAt_DecidedAt()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await MakeRunAsync();
        var scope = new AgentTimelineScope(_service, run.Id, stepId: null);
        var asked = new DateTime(2026, 3, 4, 5, 6, 7, 890, DateTimeKind.Utc);
        var decided = asked.AddMilliseconds(1250);

        scope.Emit(ToolGateSurface.Unattended, "write_file", ToolClass.Files, null,
            ToolGateDecision.GrantedByName, AgentTimelineOutcome.Ok,
            toolCallId: "call_abc123", round: 3, requestedAt: asked, decidedAt: decided);

        // The park's shape: a real RequestedAt and NO decision yet.
        scope.Emit(ToolGateSurface.Unattended, "delete_file", ToolClass.Files, null,
            ToolGateDecision.ParkedForApproval, AgentTimelineOutcome.NotExecuted,
            toolCallId: "toolu_01ABCdef", round: 4, requestedAt: asked, decidedAt: null);

        var rows = await _service.GetForRunAsync(run.Id, ct);

        Assert.Equal(2, rows.Count);
        // Through the REAL INSERT and the REAL SELECT, so this covers the parameter binding, the "O" format
        // and Map's hardcoded reader indexes together — a mid-list column insertion would land here.
        Assert.Equal("call_abc123", rows[0].ToolCallId);
        Assert.Equal(3, rows[0].Round);
        Assert.Equal(asked, rows[0].RequestedAt);
        Assert.Equal(decided, rows[0].DecidedAt);
        Assert.Equal(DateTimeKind.Utc, rows[0].RequestedAt!.Value.Kind); // RoundtripKind, not a local shift
        Assert.Equal(2, rows[0].SchemaVersion);

        Assert.Equal("toolu_01ABCdef", rows[1].ToolCallId);
        Assert.Equal(4, rows[1].Round);
        Assert.Equal(asked, rows[1].RequestedAt);
        Assert.Null(rows[1].DecidedAt); // a NULL that means "still pending", not "not recorded"
    }

    [Fact]
    public async Task Emit_AllocatesStepOrdinal_PerStepNotPerRun()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await MakeRunAsync();
        var stepA = Guid.NewGuid();
        var stepB = Guid.NewGuid();
        var a = new AgentTimelineScope(_service, run.Id, stepA);
        var b = new AgentTimelineScope(_service, run.Id, stepB);

        // INTERLEAVED on purpose: a single shared counter, or one keyed off Seq, would produce
        // 1,2,3,4,5,6 rather than two independent 1,2,3 sequences.
        for (var i = 0; i < 3; i++)
        {
            EmitOne(a, "write_file");
            EmitOne(b, "append_file");
        }

        var rows = await _service.GetForRunAsync(run.Id, ct);

        // Seq is per RUN: 1..6 across both steps.
        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6 }, rows.Select(r => r.Seq).ToArray());
        // StepOrdinal is per STEP: each restarts at 1 and is gap-free.
        Assert.Equal(new long?[] { 1, 1, 2, 2, 3, 3 }, rows.Select(r => r.StepOrdinal).ToArray());
        Assert.Equal(new long?[] { 1, 2, 3 }, rows.Where(r => r.StepId == stepA).Select(r => r.StepOrdinal).ToArray());
        Assert.Equal(new long?[] { 1, 2, 3 }, rows.Where(r => r.StepId == stepB).Select(r => r.StepOrdinal).ToArray());
    }

    [Fact]
    public async Task Emit_LeavesStepOrdinalNull_ForRunLevelAndTruncationRows()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await MakeRunAsync();

        // A run-level turn (the planner-degrade fallback) has no step, so it gets Seq and no ordinal.
        var runLevel = new AgentTimelineScope(_service, run.Id, stepId: null);
        EmitOne(runLevel, "write_file");

        // Then drive a REAL truncation by exceeding the cap on a STEP scope: the marker is built from a capped
        // event that DID carry an ordinal, so this is what catches a `with` block that inherits it.
        var step = new AgentTimelineScope(_service, run.Id, Guid.NewGuid());
        for (var i = 0; i < AgentTimelineService.MaxEventsPerRun + 50; i++)
            EmitOne(step, "write_file");

        var rows = await _service.GetForRunAsync(run.Id, ct);
        var marker = Assert.Single(rows, r => r.Kind == AgentTimelineEventKind.TraceTruncated);

        Assert.Null(rows[0].StepOrdinal);   // the run-level row
        Assert.Equal(1, rows[1].StepOrdinal); // the step's first row DOES get one — the control assertion
        Assert.Null(marker.StepOrdinal);
        Assert.Null(marker.StepId);
        // The marker inherits none of the correlation fields of the call that hit the cap.
        Assert.Null(marker.ToolCallId);
        Assert.Null(marker.Round);
        Assert.Null(marker.RequestedAt);
        Assert.Null(marker.DecidedAt);
        // The cap accounting is unchanged by any of this.
        Assert.Equal(AgentTimelineService.MaxEventsPerRun + 1, rows.Count);
    }

    [Fact]
    public async Task SeedSlot_ResumesStepOrdinal_InASecondServiceInstance()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await MakeRunAsync();
        var stepA = Guid.NewGuid();
        var stepB = Guid.NewGuid();

        // Three groups for the seed to fold: MAX across them for the run's Seq, SUM for its Count, and
        // MAX(StepOrdinal) WITHIN each step group — a per-group Math.Max on the count would seed 3, not 7.
        var a = new AgentTimelineScope(_service, run.Id, stepA);
        var b = new AgentTimelineScope(_service, run.Id, stepB);
        var runLevel = new AgentTimelineScope(_service, run.Id, stepId: null);
        for (var i = 0; i < 3; i++) EmitOne(a, "write_file");
        for (var i = 0; i < 3; i++) EmitOne(b, "append_file");
        EmitOne(runLevel, "write_file");
        await _service.DrainAsync();
        _service.Dispose();

        // A run parked in one process and resumed in another.
        using var second = new AgentTimelineService(_ctx, NullLogger<AgentTimelineService>.Instance);
        EmitOne(new AgentTimelineScope(second, run.Id, stepA), "write_file");
        EmitOne(new AgentTimelineScope(second, run.Id, stepB), "append_file");
        EmitOne(new AgentTimelineScope(second, run.Id, stepId: null), "write_file");

        var rows = await second.GetForRunAsync(run.Id, ct);

        Assert.Equal(10, rows.Count);
        // Run Seq continues (8, 9, 10) rather than restarting — folded as MAX across the groups.
        Assert.Equal(new long[] { 8, 9, 10 }, rows.TakeLast(3).Select(r => r.Seq).ToArray());
        // Each STEP's ordinal continues at 4, reconstructed from MAX(StepOrdinal) per group: MAX(Seq) per group
        // would give 8/9 here, and restarting would collide with the parked segment's own first row.
        Assert.Equal(4, rows.Last(r => r.StepId == stepA).StepOrdinal);
        Assert.Equal(4, rows.Last(r => r.StepId == stepB).StepOrdinal);
        Assert.Null(rows[^1].StepOrdinal); // the resumed run-level row still gets none
    }

    [Fact]
    public async Task Prune_ThenEmit_RebuildsStepOrdinalWithoutColliding()
    {
        var ct = TestContext.Current.CancellationToken;
        var keptRun = await MakeRunAsync();
        var prunedRun = await MakeRunAsync();
        var step = Guid.NewGuid();

        // PruneOlderThanAsync clears _slots, so the per-step dictionary is dropped with the slot it lives in.
        // Whether the next emit collides depends entirely on the re-seed reading MAX(StepOrdinal).
        var pruned = new AgentTimelineScope(_service, prunedRun.Id, step);
        for (var i = 0; i < 3; i++) EmitOne(pruned, "write_file");
        var kept = new AgentTimelineScope(_service, keptRun.Id, step);
        for (var i = 0; i < 2; i++) EmitOne(kept, "write_file");
        await _service.DrainAsync();

        // Age only the pruned run's rows, so the OTHER run's slot is cleared with its rows still in the table.
        // The cutoff is an HOUR AGO, not UtcNow, which would sweep the kept run's just-written rows too.
        AgeRow(prunedRun.Id, DateTime.UtcNow - TimeSpan.FromDays(1));
        Assert.Equal(3, await _service.PruneOlderThanAsync(DateTime.UtcNow - TimeSpan.FromHours(1), ct));

        EmitOne(kept, "write_file");
        EmitOne(pruned, "write_file");

        var keptRows = await _service.GetForRunAsync(keptRun.Id, ct);
        var prunedRows = await _service.GetForRunAsync(prunedRun.Id, ct);

        // The surviving run's step continues at 3 — re-seeded from the table, not restarted.
        Assert.Equal(new long?[] { 1, 2, 3 }, keptRows.Select(r => r.StepOrdinal).ToArray());
        // The emptied run's step legitimately restarts at 1: there is nothing left to collide with.
        Assert.Equal(new long?[] { 1 }, prunedRows.Select(r => r.StepOrdinal).ToArray());
    }

    /// <summary>One correlation-free tool call, for the tests whose subject is the allocation, not the
    /// payload.</summary>
    private static void EmitOne(AgentTimelineScope scope, string toolName) =>
        scope.Emit(ToolGateSurface.Unattended, toolName, ToolClass.Files, null,
            ToolGateDecision.GrantedByName, AgentTimelineOutcome.Ok,
            toolCallId: null, round: null, requestedAt: null, decidedAt: null);

    [Fact]
    public async Task PerRunCapIsEnforced_AndTheTruncationIsRecordedOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await MakeRunAsync();
        var scope = new AgentTimelineScope(_service, run.Id, stepId: null);

        for (var i = 0; i < 600; i++)
            scope.Emit(ToolGateSurface.Unattended, "write_file", ToolClass.Files, null, ToolGateDecision.GrantedByName, AgentTimelineOutcome.Ok, toolCallId: null, round: null, requestedAt: null, decidedAt: null);

        var rows = await _service.GetForRunAsync(run.Id, ct);
        Assert.Equal(AgentTimelineService.MaxEventsPerRun + 1, rows.Count);
        Assert.Equal(AgentTimelineEventKind.TraceTruncated, rows[^1].Kind);
        Assert.Equal(AgentTimelineEventKind.ToolCall, rows[^2].Kind);
        // The marker carries no tool identity: it is a statement about the trace, not about a call.
        Assert.Equal(string.Empty, rows[^1].ToolName);

        // A further 100 events add nothing, and no second marker appears.
        for (var i = 0; i < 100; i++)
            scope.Emit(ToolGateSurface.Unattended, "write_file", ToolClass.Files, null, ToolGateDecision.GrantedByName, AgentTimelineOutcome.Ok, toolCallId: null, round: null, requestedAt: null, decidedAt: null);

        var after = await _service.GetForRunAsync(run.Id, ct);
        Assert.Equal(AgentTimelineService.MaxEventsPerRun + 1, after.Count);
        Assert.Single(after, r => r.Kind == AgentTimelineEventKind.TraceTruncated);
    }

    [Fact]
    public async Task TheCapIsPerRun_NotGlobal()
    {
        var ct = TestContext.Current.CancellationToken;
        var runA = await MakeRunAsync();
        var runB = await MakeRunAsync();
        var a = new AgentTimelineScope(_service, runA.Id, stepId: null);
        var b = new AgentTimelineScope(_service, runB.Id, stepId: null);

        for (var i = 0; i < 600; i++)
            a.Emit(ToolGateSurface.Unattended, "write_file", ToolClass.Files, null, ToolGateDecision.GrantedByName, AgentTimelineOutcome.Ok, toolCallId: null, round: null, requestedAt: null, decidedAt: null);
        for (var i = 0; i < 5; i++)
            b.Emit(ToolGateSurface.Unattended, "write_file", ToolClass.Files, null, ToolGateDecision.GrantedByName, AgentTimelineOutcome.Ok, toolCallId: null, round: null, requestedAt: null, decidedAt: null);

        Assert.Equal(AgentTimelineService.MaxEventsPerRun + 1, (await _service.GetForRunAsync(runA.Id, ct)).Count);
        Assert.Equal(5, (await _service.GetForRunAsync(runB.Id, ct)).Count);
    }

    [Fact]
    public async Task TheCapSurvivesARestart()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await MakeRunAsync();
        var scope = new AgentTimelineScope(_service, run.Id, stepId: null);
        for (var i = 0; i < 600; i++)
            scope.Emit(ToolGateSurface.Unattended, "write_file", ToolClass.Files, null, ToolGateDecision.GrantedByName, AgentTimelineOutcome.Ok, toolCallId: null, round: null, requestedAt: null, decidedAt: null);
        await _service.DrainAsync();
        _service.Dispose();

        using var second = new AgentTimelineService(_ctx, NullLogger<AgentTimelineService>.Instance);
        new AgentTimelineScope(second, run.Id, stepId: null)
            .Emit(ToolGateSurface.Unattended, "write_file", ToolClass.Files, null, ToolGateDecision.GrantedByName, AgentTimelineOutcome.Ok, toolCallId: null, round: null, requestedAt: null, decidedAt: null);

        var rows = await second.GetForRunAsync(run.Id, ct);
        // Count is seeded from the DB, so the cap re-applies instead of restarting at zero — and no second
        // truncation marker is appended.
        Assert.Equal(AgentTimelineService.MaxEventsPerRun + 1, rows.Count);
        Assert.Single(rows, r => r.Kind == AgentTimelineEventKind.TraceTruncated);
    }

    [Fact]
    public async Task PruneOlderThan_DeletesByTheRowsOwnCreatedAt()
    {
        var ct = TestContext.Current.CancellationToken;
        var oldRun = await MakeRunAsync();
        var newRun = await MakeRunAsync();
        var cutoff = DateTime.UtcNow;

        // BOTH runs' CompletedAt stay NULL — a crash-swept run never settles one, which is exactly why the
        // prune keys off the ROW's CreatedAt. A join on AgentRuns.CompletedAt would delete nothing here.
        Assert.Null((await _runs.GetAsync(oldRun.Id, ct))!.CompletedAt);
        Assert.Null((await _runs.GetAsync(newRun.Id, ct))!.CompletedAt);

        new AgentTimelineScope(_service, oldRun.Id, null)
            .Emit(ToolGateSurface.Unattended, "old_tool", ToolClass.Files, null, ToolGateDecision.GrantedByName, AgentTimelineOutcome.Ok, toolCallId: null, round: null, requestedAt: null, decidedAt: null);
        new AgentTimelineScope(_service, newRun.Id, null)
            .Emit(ToolGateSurface.Unattended, "new_tool", ToolClass.Files, null, ToolGateDecision.GrantedByName, AgentTimelineOutcome.Ok, toolCallId: null, round: null, requestedAt: null, decidedAt: null);
        await _service.DrainAsync();
        // Age the first row a day past the cutoff (the service reads UtcNow, so the fixture moves the row).
        AgeRow(oldRun.Id, cutoff - TimeSpan.FromDays(1));

        var deleted = await _service.PruneOlderThanAsync(cutoff, ct);

        Assert.Equal(1, deleted);
        Assert.Empty(await _service.GetForRunAsync(oldRun.Id, ct));
        Assert.Single(await _service.GetForRunAsync(newRun.Id, ct));
    }

    [Fact]
    public async Task PruneReturnsTheDeletedCount_AndNeverThrows()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await MakeRunAsync();
        var scope = new AgentTimelineScope(_service, run.Id, null);
        for (var i = 0; i < 3; i++)
            scope.Emit(ToolGateSurface.Unattended, "write_file", ToolClass.Files, null, ToolGateDecision.GrantedByName, AgentTimelineOutcome.Ok, toolCallId: null, round: null, requestedAt: null, decidedAt: null);
        await _service.DrainAsync();
        AgeRow(run.Id, DateTime.UtcNow - TimeSpan.FromDays(2));

        Assert.Equal(3, await _service.PruneOlderThanAsync(DateTime.UtcNow, ct));
        Assert.Equal(0, await _service.PruneOlderThanAsync(DateTime.UtcNow, ct));

        _service.Dispose();
        Assert.Equal(0, await _service.PruneOlderThanAsync(DateTime.UtcNow, ct));
    }

    [Fact]
    public async Task DeletingTheChatCascadesTheTimelineAway()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await MakeRunAsync();
        var scope = new AgentTimelineScope(_service, run.Id, null);
        for (var i = 0; i < 3; i++)
            scope.Emit(ToolGateSurface.Unattended, "write_file", ToolClass.Files, null, ToolGateDecision.GrantedByName, AgentTimelineOutcome.Ok, toolCallId: null, round: null, requestedAt: null, decidedAt: null);
        await _service.DrainAsync();

        // A control assertion, not belt-and-braces: with an undrained queue the delete would cascade nothing
        // and the later insert would fail the FK — zero rows, green, proving nothing.
        Assert.Equal(3, CountRows());

        await _chats.DeleteAsync(run.ChatId, ct);

        Assert.Equal(0, CountRows());
    }

    [Fact]
    public async Task AReplanThatReplacesStepsLeavesTheTimelineIntact()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await MakeRunAsync();
        var stepId = Guid.NewGuid();
        await _runs.ReplaceStepsAsync(run.Id, [Step(run.Id, stepId, 0, "first")], ct);

        var scope = new AgentTimelineScope(_service, run.Id, stepId);
        for (var i = 0; i < 3; i++)
            scope.Emit(ToolGateSurface.Unattended, "write_file", ToolClass.Files, null, ToolGateDecision.GrantedByName, AgentTimelineOutcome.Ok, toolCallId: null, round: null, requestedAt: null, decidedAt: null);
        await _service.DrainAsync();
        Assert.Equal(3, CountRows());

        // A replan DELETEs every AgentSteps row for the run and re-inserts a fresh plan. StepId is FK-free
        // precisely so the trail of what already ran survives that.
        await _runs.ReplaceStepsAsync(run.Id, [Step(run.Id, Guid.NewGuid(), 0, "replanned")], ct);

        var rows = await _service.GetForRunAsync(run.Id, ct);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal(stepId, r.StepId)); // now dangling, deliberately
    }

    private async Task<AgentRun> MakeRunAsync()
    {
        var chatId = await MakeChatAsync();
        return await _runs.CreateAsync(
            new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.User),
            TestContext.Current.CancellationToken);
    }

    private async Task<Guid> MakeChatAsync()
    {
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await _chats.SaveAsync(new SyncAssistantChat
        {
            Id = id,
            CreatedAt = now,
            UpdatedAt = now,
            LastAccessedAt = now,
            WindowMode = "Assistant",
        }, TestContext.Current.CancellationToken);
        return id;
    }

    private static AgentStep Step(Guid runId, Guid id, int ordinal, string title) => new()
    {
        Id = id,
        RunId = runId,
        Ordinal = ordinal,
        Title = title,
        Status = AgentStepStatus.Pending,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private int CountRows()
    {
        using var cmd = _ctx.GetConnection().CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM AgentTimelineEvents";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private void AgeRow(Guid runId, DateTime createdAt)
    {
        using var cmd = _ctx.GetConnection().CreateCommand();
        cmd.CommandText = "UPDATE AgentTimelineEvents SET CreatedAt = @At WHERE RunId = @RunId";
        cmd.Parameters.AddWithValue("@At", createdAt.ToString("O"));
        cmd.Parameters.AddWithValue("@RunId", runId.ToString());
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _service.Dispose();
        _runs.Dispose();
        _chats.Dispose();
        _ctx.Dispose();
        SqlitePool.ClearFor(_ctx.ConnectionString);
        TempPath.Remove(_tmpDir);
    }
}
