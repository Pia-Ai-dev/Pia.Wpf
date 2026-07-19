using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Durable-spine coverage for <see cref="AgentRunService"/> (phase1 plan §12.8): schema
/// idempotency, lifecycle transitions, ledger accrual, the R1 write-order/FK-cascade rules, the
/// eviction predicate, and the R2 re-query semantics of <see cref="AgentRunService.NextPendingStepAsync"/>.
/// Written to run on Windows/CI — the WPF-targeted test assembly cannot execute on macOS.
/// </summary>
public sealed class AgentRunServiceTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string _dbPath;
    private readonly SqliteContext _ctx;
    private readonly AssistantChatService _chats;
    private readonly AgentRunService _service;

    public AgentRunServiceTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "PiaTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        _dbPath = Path.Combine(_tmpDir, "history.db");
        _ctx = new SqliteContext(_dbPath);
        _service = new AgentRunService(_ctx, NullLogger<AgentRunService>.Instance);
        _chats = new AssistantChatService(_ctx, _service);
    }

    [Fact]
    public void Schema_CreatesAgentTables_AndIsIdempotentOnReopen()
    {
        Assert.True(TableExists(_ctx.GetConnection(), "AgentRuns"));
        Assert.True(TableExists(_ctx.GetConnection(), "AgentSteps"));

        // Reopening the same file re-runs EnsureSchema over existing tables (CREATE TABLE IF NOT
        // EXISTS) — a no-op that must not throw.
        using var reopened = new SqliteContext(_dbPath);
        var conn = reopened.GetConnection();
        Assert.True(TableExists(conn, "AgentRuns"));
        Assert.True(TableExists(conn, "AgentSteps"));
    }

    [Fact]
    public async Task CreateAsync_SingleTurn_StartsRunning_WithStartedAt()
    {
        var chatId = await MakeChatAsync();

        var run = await _service.CreateAsync(new AgentRunCreateRequest(
            chatId, RunShape.SingleTurn, AgentRunTrigger.User, Goal: "do the thing"), TestContext.Current.CancellationToken);

        Assert.Equal(AgentRunState.Running, run.State);
        Assert.NotNull(run.StartedAt);

        var fetched = await _service.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(fetched);
        Assert.Equal(AgentRunState.Running, fetched!.State);
        Assert.Equal(chatId, fetched.ChatId);
        Assert.NotNull(fetched.StartedAt);
    }

    [Fact]
    public async Task CreateAsync_Planned_StartsPlanning()
    {
        var chatId = await MakeChatAsync();

        var run = await _service.CreateAsync(new AgentRunCreateRequest(
            chatId, RunShape.Planned, AgentRunTrigger.User), TestContext.Current.CancellationToken);

        Assert.Equal(AgentRunState.Planning, run.State);
    }

    [Fact]
    public async Task CreateAsync_PersistsTriggerProvenance()
    {
        var chatId = await MakeChatAsync();
        var jobId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();

        var run = await _service.CreateAsync(new AgentRunCreateRequest(
            chatId, RunShape.SingleTurn, AgentRunTrigger.Schedule, jobId, deviceId, "goal"), TestContext.Current.CancellationToken);

        var fetched = await _service.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunTrigger.Schedule, fetched!.TriggerKind);
        Assert.Equal(jobId, fetched.TriggerRef);
        Assert.Equal(deviceId, fetched.OwnerDeviceId);
    }

    [Fact]
    public async Task AddUsageAsync_AccruesRunLevelLedger()
    {
        var chatId = await MakeChatAsync();
        var run = await _service.CreateAsync(new AgentRunCreateRequest(chatId, RunShape.SingleTurn, AgentRunTrigger.User), TestContext.Current.CancellationToken);

        await _service.AddUsageAsync(run.Id, null, new UsageDetails { InputTokenCount = 10, OutputTokenCount = 4 }, TestContext.Current.CancellationToken);
        await _service.AddUsageAsync(run.Id, null, new UsageDetails { InputTokenCount = 5, OutputTokenCount = 1 }, TestContext.Current.CancellationToken);

        var fetched = await _service.GetAsync(run.Id, TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(fetched!.LedgerJson!);
        Assert.Equal(15, doc.RootElement.GetProperty("inputTokens").GetInt64());
        Assert.Equal(5, doc.RootElement.GetProperty("outputTokens").GetInt64());
    }

    [Fact]
    public async Task AddUsageAsync_WithStepId_AccruesPerStepAndGrandTotal()
    {
        // Exercises the non-null-stepId branch of AddUsageAsync (AgentRunService.cs ~170-180),
        // which AddUsageAsync_AccruesRunLevelLedger (stepId: null) never hits.
        var chatId = await MakeChatAsync();
        var run = await _service.CreateAsync(new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.User), TestContext.Current.CancellationToken);
        var step = new AgentStep { Id = Guid.NewGuid(), Ordinal = 0, Title = "A", Status = AgentStepStatus.Pending };
        await _service.ReplaceStepsAsync(run.Id, new[] { step }, TestContext.Current.CancellationToken);

        await _service.AddUsageAsync(run.Id, step.Id, new UsageDetails { InputTokenCount = 10, OutputTokenCount = 4 }, TestContext.Current.CancellationToken);
        await _service.AddUsageAsync(run.Id, step.Id, new UsageDetails { InputTokenCount = 5, OutputTokenCount = 1 }, TestContext.Current.CancellationToken);

        var fetched = await _service.GetAsync(run.Id, TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(fetched!.LedgerJson!);

        // Grand total accrues across both calls.
        Assert.Equal(15, doc.RootElement.GetProperty("inputTokens").GetInt64());
        Assert.Equal(5, doc.RootElement.GetProperty("outputTokens").GetInt64());

        // The per-step entry for that StepId also accrues across both calls.
        var perStep = doc.RootElement.GetProperty("perStep");
        Assert.Equal(1, perStep.GetArrayLength());
        Assert.Equal(step.Id.ToString(), perStep[0].GetProperty("stepId").GetString());
        Assert.Equal(15, perStep[0].GetProperty("inputTokens").GetInt64());
        Assert.Equal(5, perStep[0].GetProperty("outputTokens").GetInt64());
    }

    [Fact]
    public async Task CompleteAsync_TransitionsToCompleted_WithCompletedAt()
    {
        var chatId = await MakeChatAsync();
        var run = await _service.CreateAsync(new AgentRunCreateRequest(chatId, RunShape.SingleTurn, AgentRunTrigger.User), TestContext.Current.CancellationToken);

        await _service.CompleteAsync(run.Id, ct: TestContext.Current.CancellationToken);

        var fetched = await _service.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, fetched!.State);
        Assert.NotNull(fetched.CompletedAt);
    }

    [Fact]
    public async Task CompleteAsync_Truncated_WritesTruncatedMarker()
    {
        var chatId = await MakeChatAsync();
        var run = await _service.CreateAsync(new AgentRunCreateRequest(chatId, RunShape.SingleTurn, AgentRunTrigger.User), TestContext.Current.CancellationToken);

        await _service.CompleteAsync(run.Id, truncated: true, truncationReason: "budget", ct: TestContext.Current.CancellationToken);

        var fetched = await _service.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Completed, fetched!.State);
        using var doc = JsonDocument.Parse(fetched.ExtraJson!);
        Assert.True(doc.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal("budget", doc.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task FailAsync_TransitionsToFailed()
    {
        var chatId = await MakeChatAsync();
        var run = await _service.CreateAsync(new AgentRunCreateRequest(chatId, RunShape.SingleTurn, AgentRunTrigger.User), TestContext.Current.CancellationToken);

        await _service.FailAsync(run.Id, "boom", ct: TestContext.Current.CancellationToken);

        var fetched = await _service.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Failed, fetched!.State);
        Assert.NotNull(fetched.CompletedAt);
    }

    [Fact]
    public async Task FailAsync_Cancelled_TransitionsToCancelled()
    {
        var chatId = await MakeChatAsync();
        var run = await _service.CreateAsync(new AgentRunCreateRequest(chatId, RunShape.SingleTurn, AgentRunTrigger.User), TestContext.Current.CancellationToken);

        await _service.FailAsync(run.Id, null, cancelled: true, ct: TestContext.Current.CancellationToken);

        var fetched = await _service.GetAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunState.Cancelled, fetched!.State);
    }

    [Fact]
    public async Task FailInterruptedRunsAsync_SettlesNonTerminalRuns_LeavesTerminalUntouched()
    {
        // G-4: a crash / forced-exit leaves runs non-terminal (Planning/Running/Verifying/...); the startup
        // sweep settles exactly those to Cancelled and never touches already-terminal runs.
        var ct = TestContext.Current.CancellationToken;

        var planning = await _service.CreateAsync(new AgentRunCreateRequest(await MakeChatAsync(), RunShape.Planned, AgentRunTrigger.User), ct);
        var running = await _service.CreateAsync(new AgentRunCreateRequest(await MakeChatAsync(), RunShape.Planned, AgentRunTrigger.User), ct);
        await _service.SetStateAsync(running.Id, AgentRunState.Running, ct);
        var verifying = await _service.CreateAsync(new AgentRunCreateRequest(await MakeChatAsync(), RunShape.Planned, AgentRunTrigger.User), ct);
        await _service.SetStateAsync(verifying.Id, AgentRunState.Verifying, ct);

        var completed = await _service.CreateAsync(new AgentRunCreateRequest(await MakeChatAsync(), RunShape.Planned, AgentRunTrigger.User), ct);
        await _service.CompleteAsync(completed.Id, ct: ct);
        var failed = await _service.CreateAsync(new AgentRunCreateRequest(await MakeChatAsync(), RunShape.SingleTurn, AgentRunTrigger.User), ct);
        await _service.FailAsync(failed.Id, "boom", ct: ct);

        var settled = await _service.FailInterruptedRunsAsync(ct);

        Assert.Equal(3, settled);
        Assert.Equal(AgentRunState.Cancelled, (await _service.GetAsync(planning.Id, ct))!.State);
        Assert.Equal(AgentRunState.Cancelled, (await _service.GetAsync(running.Id, ct))!.State);
        Assert.Equal(AgentRunState.Cancelled, (await _service.GetAsync(verifying.Id, ct))!.State);
        Assert.NotNull((await _service.GetAsync(planning.Id, ct))!.CompletedAt);
        Assert.Equal(AgentRunState.Completed, (await _service.GetAsync(completed.Id, ct))!.State);
        Assert.Equal(AgentRunState.Failed, (await _service.GetAsync(failed.Id, ct))!.State);

        // Idempotent: a second sweep settles nothing (all runs are now terminal).
        Assert.Equal(0, await _service.FailInterruptedRunsAsync(ct));
    }

    [Fact]
    public async Task CreateAsync_BeforeChatRow_ThrowsFkConstraint()
    {
        // R1: FK enforcement is ON — a run row cannot precede its AssistantChats parent.
        var orphanChatId = Guid.NewGuid();

        await Assert.ThrowsAsync<SqliteException>(async () =>
            await _service.CreateAsync(new AgentRunCreateRequest(orphanChatId, RunShape.SingleTurn, AgentRunTrigger.User), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeletingChat_CascadesRunsAndSteps()
    {
        var chatId = await MakeChatAsync();
        var run = await _service.CreateAsync(new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.User), TestContext.Current.CancellationToken);
        await _service.ReplaceStepsAsync(run.Id, new[]
        {
            new AgentStep { Ordinal = 0, Title = "a", Status = AgentStepStatus.Pending },
            new AgentStep { Ordinal = 1, Title = "b", Status = AgentStepStatus.Pending },
        }, TestContext.Current.CancellationToken);

        Assert.Equal(1, RawCount("AgentRuns", "ChatId", chatId));
        Assert.Equal(2, RawCount("AgentSteps", "RunId", run.Id));

        await _chats.DeleteAsync(chatId, TestContext.Current.CancellationToken);

        Assert.Equal(0, RawCount("AgentRuns", "ChatId", chatId));
        Assert.Equal(0, RawCount("AgentSteps", "RunId", run.Id));
        Assert.Empty(await _service.GetByChatAsync(chatId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ChatHasPlannedRunAsync_TrueOnlyForPlanned()
    {
        var chatId = await MakeChatAsync();
        await _service.CreateAsync(new AgentRunCreateRequest(chatId, RunShape.SingleTurn, AgentRunTrigger.User), TestContext.Current.CancellationToken);
        Assert.False(await _service.ChatHasPlannedRunAsync(chatId, TestContext.Current.CancellationToken));

        await _service.CreateAsync(new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.User), TestContext.Current.CancellationToken);
        Assert.True(await _service.ChatHasPlannedRunAsync(chatId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetByChatAsync_ReturnsAllRunsForChat_InCreationOrder()
    {
        var chatId = await MakeChatAsync();
        var r1 = await _service.CreateAsync(new AgentRunCreateRequest(chatId, RunShape.SingleTurn, AgentRunTrigger.User), TestContext.Current.CancellationToken);
        var r2 = await _service.CreateAsync(new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.User), TestContext.Current.CancellationToken);

        var runs = await _service.GetByChatAsync(chatId, TestContext.Current.CancellationToken);
        Assert.Equal(2, runs.Count);
        Assert.Equal(r1.Id, runs[0].Id);
        Assert.Equal(r2.Id, runs[1].Id);
    }

    [Fact]
    public async Task NextPendingStepAsync_ReQueriesPersistedSteps_NotASnapshot()
    {
        // R2: the loop must pick up steps written by a later ReplaceStepsAsync (replan), not iterate
        // a stale snapshot.
        var chatId = await MakeChatAsync();
        var run = await _service.CreateAsync(new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.User), TestContext.Current.CancellationToken);

        var stepA = new AgentStep { Id = Guid.NewGuid(), Ordinal = 0, Title = "A", Status = AgentStepStatus.Pending };
        var stepB = new AgentStep { Id = Guid.NewGuid(), Ordinal = 1, Title = "B", Status = AgentStepStatus.Pending };
        await _service.ReplaceStepsAsync(run.Id, new[] { stepA, stepB }, TestContext.Current.CancellationToken);

        var next = await _service.NextPendingStepAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal("A", next!.Title);

        await _service.SetStepStatusAsync(stepA.Id, AgentStepStatus.Done, TestContext.Current.CancellationToken);
        next = await _service.NextPendingStepAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal("B", next!.Title);

        // Replan: replace the remaining plan with an entirely new step set.
        var stepC = new AgentStep { Id = Guid.NewGuid(), Ordinal = 0, Title = "C", Status = AgentStepStatus.Pending };
        await _service.ReplaceStepsAsync(run.Id, new[] { stepC }, TestContext.Current.CancellationToken);

        next = await _service.NextPendingStepAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.Equal("C", next!.Title);
    }

    [Fact]
    public async Task RecordStepResultAsync_AccruesPerStepLedger()
    {
        var chatId = await MakeChatAsync();
        var run = await _service.CreateAsync(new AgentRunCreateRequest(chatId, RunShape.Planned, AgentRunTrigger.User), TestContext.Current.CancellationToken);
        var step = new AgentStep { Id = Guid.NewGuid(), Ordinal = 0, Title = "A", Status = AgentStepStatus.Pending };
        await _service.ReplaceStepsAsync(run.Id, new[] { step }, TestContext.Current.CancellationToken);

        await _service.RecordStepResultAsync(step.Id, AgentStepStatus.Done, Guid.NewGuid(), Guid.NewGuid(),
            new UsageDetails { InputTokenCount = 7, OutputTokenCount = 3 }, TestContext.Current.CancellationToken);

        var fetched = await _service.GetAsync(run.Id, TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(fetched!.LedgerJson!);
        Assert.Equal(7, doc.RootElement.GetProperty("inputTokens").GetInt64());
        var perStep = doc.RootElement.GetProperty("perStep");
        Assert.Equal(1, perStep.GetArrayLength());
        Assert.Equal(7, perStep[0].GetProperty("inputTokens").GetInt64());

        var doneStep = Assert.Single(fetched.Plan);
        Assert.Equal(AgentStepStatus.Done, doneStep.Status);
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

    private long RawCount(string table, string column, Guid id)
    {
        var conn = _ctx.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {column} = @Id";
        cmd.Parameters.AddWithValue("@Id", id.ToString());
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static bool TableExists(SqliteConnection conn, string name)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @Name";
        cmd.Parameters.AddWithValue("@Name", name);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    public void Dispose()
    {
        _service.Dispose();
        _ctx.Dispose();
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best effort */ }
    }
}
