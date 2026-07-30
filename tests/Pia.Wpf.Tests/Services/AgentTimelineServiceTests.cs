using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// The Batch 03 store: Seq allocation and ordering, the per-run cap, the retention prune, and the two FK
/// rules that make the trail outlive a replan while still dying with its chat.
/// <para>
/// Every run these tests emit against has a REAL chat + run row, because <c>AgentTimelineEvents.RunId</c> has
/// an enforced FK: emitting against a bare <c>Guid.NewGuid()</c> would have its INSERT rejected, logged as a
/// warning and dropped — producing a silently green "zero rows" test.
/// </para>
/// <para>
/// <c>Emit</c> is fire-and-forget, so every test that emits and then observes (including tests that
/// <i>mutate</i>, like the cascade and restart facts) awaits <c>DrainAsync</c> first. No sleeps.
/// </para>
/// </summary>
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

    // ---- T-STORE ----

    [Fact]
    public async Task Emit_ThenGetForRun_ReturnsTheRowsInSeqOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await MakeRunAsync();
        var scope = new AgentTimelineScope(_service, run.Id, stepId: null);

        for (var i = 0; i < 5; i++)
        {
            scope.Emit(ToolGateSurface.Unattended, $"tool_{i}", ToolClass.Files, Guid.NewGuid(),
                ToolGateDecision.GrantedByName, AgentTimelineOutcome.Ok, argsChars: 10, resultChars: 20, durationMs: 3);
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
            Assert.Equal(1, r.SchemaVersion);
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
                    ToolGateDecision.GrantedByName, AgentTimelineOutcome.Ok);
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
            scope.Emit(ToolGateSurface.Unattended, "write_file", ToolClass.Files, null, ToolGateDecision.GrantedByName, AgentTimelineOutcome.Ok);
        await _service.DrainAsync();
        _service.Dispose();

        // A run parked in one process and resumed in another: the second instance must continue the sequence.
        using var second = new AgentTimelineService(_ctx, NullLogger<AgentTimelineService>.Instance);
        new AgentTimelineScope(second, run.Id, stepId: null)
            .Emit(ToolGateSurface.Unattended, "write_file", ToolClass.Files, null, ToolGateDecision.GrantedByName, AgentTimelineOutcome.Ok);

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
            scope.Emit(ToolGateSurface.Unattended, "write_file", ToolClass.Files, null, ToolGateDecision.GrantedByName, AgentTimelineOutcome.Ok);
        await _service.DrainAsync();
        _service.Dispose();

        // Make the seed query FAIL for a resumed run, the way SQLITE_BUSY or an I/O error would: rename the
        // table out from under a fresh instance. A rename (not a drop) is what keeps the parked segment's three
        // rows alive, which is the whole point — the hazard is the resumed segment colliding with them.
        Exec("ALTER TABLE AgentTimelineEvents RENAME TO AgentTimelineEvents_hidden;");

        using var second = new AgentTimelineService(_ctx, NullLogger<AgentTimelineService>.Instance);
        var resumed = new AgentTimelineScope(second, run.Id, stepId: null);
        resumed.Emit(ToolGateSurface.Unattended, "write_file", ToolClass.Files, null, ToolGateDecision.GrantedByName, AgentTimelineOutcome.Ok);
        await second.DrainAsync();

        // Table back: the store is readable again and the slot must NOT still be trusting its failed seed.
        Exec("ALTER TABLE AgentTimelineEvents_hidden RENAME TO AgentTimelineEvents;");

        resumed.Emit(ToolGateSurface.Unattended, "write_file", ToolClass.Files, null, ToolGateDecision.GrantedByName, AgentTimelineOutcome.Ok);

        var rows = await second.GetForRunAsync(run.Id, ct);

        // Caching the failed seed (NextSeq = 0) restarted the sequence at 1, so this row landed as Seq 2 —
        // a DUPLICATE of the parked segment's second row, and ORDER BY Seq then interleaved the two segments
        // by rowid tie-break. Retrying the aggregate is what makes it 4.
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
            .Emit(ToolGateSurface.Interactive, "write_file", ToolClass.Files, null, ToolGateDecision.ApprovedOnce, AgentTimelineOutcome.Ok);

        using var reader = new AgentTimelineService(_ctx, NullLogger<AgentTimelineService>.Instance);
        Assert.Empty(await reader.GetForRunAsync(run.Id, ct));
    }

    // ---- T-CAP ----

    [Fact]
    public async Task PerRunCapIsEnforced_AndTheTruncationIsRecordedOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await MakeRunAsync();
        var scope = new AgentTimelineScope(_service, run.Id, stepId: null);

        for (var i = 0; i < 600; i++)
            scope.Emit(ToolGateSurface.Unattended, "write_file", ToolClass.Files, null, ToolGateDecision.GrantedByName, AgentTimelineOutcome.Ok);

        var rows = await _service.GetForRunAsync(run.Id, ct);
        Assert.Equal(AgentTimelineService.MaxEventsPerRun + 1, rows.Count);
        Assert.Equal(AgentTimelineEventKind.TraceTruncated, rows[^1].Kind);
        Assert.Equal(AgentTimelineEventKind.ToolCall, rows[^2].Kind);
        // The marker carries no tool identity: it is a statement about the trace, not about a call.
        Assert.Equal(string.Empty, rows[^1].ToolName);

        // A further 100 events add nothing, and no second marker appears.
        for (var i = 0; i < 100; i++)
            scope.Emit(ToolGateSurface.Unattended, "write_file", ToolClass.Files, null, ToolGateDecision.GrantedByName, AgentTimelineOutcome.Ok);

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
            a.Emit(ToolGateSurface.Unattended, "write_file", ToolClass.Files, null, ToolGateDecision.GrantedByName, AgentTimelineOutcome.Ok);
        for (var i = 0; i < 5; i++)
            b.Emit(ToolGateSurface.Unattended, "write_file", ToolClass.Files, null, ToolGateDecision.GrantedByName, AgentTimelineOutcome.Ok);

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
            scope.Emit(ToolGateSurface.Unattended, "write_file", ToolClass.Files, null, ToolGateDecision.GrantedByName, AgentTimelineOutcome.Ok);
        await _service.DrainAsync();
        _service.Dispose();

        using var second = new AgentTimelineService(_ctx, NullLogger<AgentTimelineService>.Instance);
        new AgentTimelineScope(second, run.Id, stepId: null)
            .Emit(ToolGateSurface.Unattended, "write_file", ToolClass.Files, null, ToolGateDecision.GrantedByName, AgentTimelineOutcome.Ok);

        var rows = await second.GetForRunAsync(run.Id, ct);
        // Count is seeded from the DB, so the cap re-applies instead of restarting at zero — and no second
        // truncation marker is appended.
        Assert.Equal(AgentTimelineService.MaxEventsPerRun + 1, rows.Count);
        Assert.Single(rows, r => r.Kind == AgentTimelineEventKind.TraceTruncated);
    }

    // ---- T-PRUNE ----

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
            .Emit(ToolGateSurface.Unattended, "old_tool", ToolClass.Files, null, ToolGateDecision.GrantedByName, AgentTimelineOutcome.Ok);
        new AgentTimelineScope(_service, newRun.Id, null)
            .Emit(ToolGateSurface.Unattended, "new_tool", ToolClass.Files, null, ToolGateDecision.GrantedByName, AgentTimelineOutcome.Ok);
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
            scope.Emit(ToolGateSurface.Unattended, "write_file", ToolClass.Files, null, ToolGateDecision.GrantedByName, AgentTimelineOutcome.Ok);
        await _service.DrainAsync();
        AgeRow(run.Id, DateTime.UtcNow - TimeSpan.FromDays(2));

        Assert.Equal(3, await _service.PruneOlderThanAsync(DateTime.UtcNow, ct));
        Assert.Equal(0, await _service.PruneOlderThanAsync(DateTime.UtcNow, ct));

        _service.Dispose();
        Assert.Equal(0, await _service.PruneOlderThanAsync(DateTime.UtcNow, ct));
    }

    // ---- T-FK ----

    [Fact]
    public async Task DeletingTheChatCascadesTheTimelineAway()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await MakeRunAsync();
        var scope = new AgentTimelineScope(_service, run.Id, null);
        for (var i = 0; i < 3; i++)
            scope.Emit(ToolGateSurface.Unattended, "write_file", ToolClass.Files, null, ToolGateDecision.GrantedByName, AgentTimelineOutcome.Ok);
        await _service.DrainAsync();

        // MANDATORY control assertion, not belt-and-braces: Emit is fire-and-forget and the RunId FK is
        // enforced, so an undrained queue would mean the delete cascades nothing AND the later insert fails
        // the FK — zero rows, green, proving nothing.
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
            scope.Emit(ToolGateSurface.Unattended, "write_file", ToolClass.Files, null, ToolGateDecision.GrantedByName, AgentTimelineOutcome.Ok);
        await _service.DrainAsync();
        Assert.Equal(3, CountRows());

        // A replan DELETEs every AgentSteps row for the run and re-inserts a fresh plan. StepId is FK-free
        // precisely so the trail of what already ran survives that.
        await _runs.ReplaceStepsAsync(run.Id, [Step(run.Id, Guid.NewGuid(), 0, "replanned")], ct);

        var rows = await _service.GetForRunAsync(run.Id, ct);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal(stepId, r.StepId)); // now dangling, deliberately
    }

    // ---- fixture helpers ----

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
        _ctx.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best effort */ }
    }
}
