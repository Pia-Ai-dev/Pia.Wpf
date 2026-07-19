using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure;
using Pia.Logging;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>
/// SQLite-backed durable store + lifecycle for <see cref="AgentRun"/>/<see cref="AgentStep"/>.
/// Uses its own dedicated connection (not the shared <see cref="SqliteContext"/> connection)
/// guarded by a lock, because runs are written from background turn threads and the shared
/// connection has UI-initiated thread affinity. The tables themselves live in
/// <see cref="SqliteContext"/>'s canonical schema (not redefined here — §16 R19); the ctor forces
/// the shared connection once at composition time so that schema exists before this service opens.
/// <para>
/// <c>Goal</c>/step <c>Title</c>/<c>Intent</c> are user content — logged only via
/// <c>SensitiveDebug</c>, never at Information (CLAUDE.md / §12.7).
/// </para>
/// </summary>
public sealed class AgentRunService : IAgentRunService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _connectionString;
    private readonly ILogger<AgentRunService> _logger;
    private readonly object _gate = new();
    private SqliteConnection? _connection;
    private bool _disposed;

    public event EventHandler<AgentRunChangedEventArgs>? RunChanged;

    public AgentRunService(SqliteContext context, ILogger<AgentRunService> logger)
    {
        _connectionString = context.ConnectionString;
        _logger = logger;

        // Force the shared context to open + run EnsureSchema (which creates AgentRuns/AgentSteps)
        // BEFORE our dedicated connection ever opens. Done at composition time rather than lazily
        // from a background thread because SqliteContext.GetConnection() is not itself synchronized.
        context.GetConnection();
    }

    private SqliteConnection Connection()
    {
        if (_connection is null)
        {
            _connection = new SqliteConnection(_connectionString);
            _connection.Open();

            using var pragma = _connection.CreateCommand();
            pragma.CommandText = "PRAGMA busy_timeout=3000;";
            pragma.ExecuteNonQuery();
        }
        else if (_connection.State != System.Data.ConnectionState.Open)
        {
            _connection.Open();
        }

        return _connection;
    }

    public Task<AgentRun> CreateAsync(AgentRunCreateRequest request, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var run = new AgentRun
        {
            Id = Guid.NewGuid(),
            SchemaVersion = 1,
            ChatId = request.ChatId,
            RunShape = request.Shape,
            // SingleTurn skips Planning (§12.6); Planned starts in Planning.
            State = request.Shape == RunShape.Planned ? AgentRunState.Planning : AgentRunState.Running,
            TriggerKind = request.Trigger,
            TriggerRef = request.TriggerRef,
            OwnerDeviceId = request.OwnerDeviceId,
            Goal = request.Goal,
            LedgerJson = JsonSerializer.Serialize(new Ledger(), JsonOptions),
            CreatedAt = now,
            UpdatedAt = now,
            StartedAt = now,
        };

        lock (_gate)
        {
            if (_disposed) return Task.FromResult(run);

            using var cmd = Connection().CreateCommand();
            cmd.CommandText = """
                INSERT INTO AgentRuns
                    (Id, SchemaVersion, ChatId, RunShape, State, TriggerKind, TriggerRef, ParentRunId,
                     OwnerDeviceId, Goal, FirstMessageId, LastMessageId, PolicyJson, LedgerJson,
                     CreatedAt, UpdatedAt, StartedAt, CompletedAt, ExtraJson)
                VALUES
                    (@Id, @SchemaVersion, @ChatId, @RunShape, @State, @TriggerKind, @TriggerRef, @ParentRunId,
                     @OwnerDeviceId, @Goal, @FirstMessageId, @LastMessageId, @PolicyJson, @LedgerJson,
                     @CreatedAt, @UpdatedAt, @StartedAt, @CompletedAt, @ExtraJson)
                """;
            cmd.Parameters.AddWithValue("@Id", run.Id.ToString());
            cmd.Parameters.AddWithValue("@SchemaVersion", run.SchemaVersion);
            cmd.Parameters.AddWithValue("@ChatId", run.ChatId.ToString());
            cmd.Parameters.AddWithValue("@RunShape", (int)run.RunShape);
            cmd.Parameters.AddWithValue("@State", (int)run.State);
            cmd.Parameters.AddWithValue("@TriggerKind", (int)run.TriggerKind);
            cmd.Parameters.AddWithValue("@TriggerRef", ToParam(run.TriggerRef));
            cmd.Parameters.AddWithValue("@ParentRunId", ToParam(run.ParentRunId));
            cmd.Parameters.AddWithValue("@OwnerDeviceId", ToParam(run.OwnerDeviceId));
            cmd.Parameters.AddWithValue("@Goal", ToParam(run.Goal));
            cmd.Parameters.AddWithValue("@FirstMessageId", DBNull.Value);
            cmd.Parameters.AddWithValue("@LastMessageId", DBNull.Value);
            cmd.Parameters.AddWithValue("@PolicyJson", DBNull.Value);
            cmd.Parameters.AddWithValue("@LedgerJson", ToParam(run.LedgerJson));
            cmd.Parameters.AddWithValue("@CreatedAt", run.CreatedAt.ToString("O"));
            cmd.Parameters.AddWithValue("@UpdatedAt", run.UpdatedAt.ToString("O"));
            cmd.Parameters.AddWithValue("@StartedAt", run.StartedAt!.Value.ToString("O"));
            cmd.Parameters.AddWithValue("@CompletedAt", DBNull.Value);
            cmd.Parameters.AddWithValue("@ExtraJson", DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        _logger.LogInformation("Created run {RunId} shape={Shape} state={State} trigger={Trigger}",
            run.Id, run.RunShape, run.State, run.TriggerKind);
        _logger.SensitiveDebug("Run {RunId} goal: {Goal}", run.Id, run.Goal);
        RunChanged?.Invoke(this, new AgentRunChangedEventArgs(run.Id, run.State));
        return Task.FromResult(run);
    }

    public Task SetStateAsync(Guid runId, AgentRunState state, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;

            using var cmd = Connection().CreateCommand();
            cmd.CommandText = "UPDATE AgentRuns SET State=@State, UpdatedAt=@Now WHERE Id=@Id";
            cmd.Parameters.AddWithValue("@State", (int)state);
            cmd.Parameters.AddWithValue("@Now", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@Id", runId.ToString());
            cmd.ExecuteNonQuery();
        }

        _logger.LogInformation("Run {RunId} → state {State}", runId, state);
        RunChanged?.Invoke(this, new AgentRunChangedEventArgs(runId, state));
        return Task.CompletedTask;
    }

    public Task AddUsageAsync(Guid runId, Guid? stepId, UsageDetails usage, CancellationToken ct = default)
    {
        AgentRunState state;
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;

            if (!TryLoadRunLedger(runId, out var ledger, out var startedAt, out state))
                return Task.CompletedTask;

            var input = usage.InputTokenCount ?? 0;
            var output = usage.OutputTokenCount ?? 0;

            // Top-level totals are the grand total (always accrue); a non-null step also accrues
            // into its per-step entry (§16 R16).
            ledger.InputTokens += input;
            ledger.OutputTokens += output;
            if (stepId is { } sid)
            {
                var entry = ledger.PerStep.FirstOrDefault(s => s.StepId == sid.ToString());
                if (entry is null)
                {
                    entry = new StepLedger { StepId = sid.ToString() };
                    ledger.PerStep.Add(entry);
                }
                entry.InputTokens += input;
                entry.OutputTokens += output;
            }
            ledger.WallClockMs = ElapsedMs(startedAt);

            WriteLedger(runId, ledger);
        }

        _logger.LogInformation("Run {RunId} usage accrued (step={StepId}, in={In}, out={Out})",
            runId, stepId, usage.InputTokenCount ?? 0, usage.OutputTokenCount ?? 0);
        RunChanged?.Invoke(this, new AgentRunChangedEventArgs(runId, state, stepId));
        return Task.CompletedTask;
    }

    public Task SetRunMessageRangeAsync(Guid runId, Guid firstMessageId, Guid lastMessageId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;

            using var cmd = Connection().CreateCommand();
            cmd.CommandText = "UPDATE AgentRuns SET FirstMessageId=@First, LastMessageId=@Last, UpdatedAt=@Now WHERE Id=@Id";
            cmd.Parameters.AddWithValue("@First", firstMessageId.ToString());
            cmd.Parameters.AddWithValue("@Last", lastMessageId.ToString());
            cmd.Parameters.AddWithValue("@Now", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@Id", runId.ToString());
            cmd.ExecuteNonQuery();
        }

        return Task.CompletedTask;
    }

    public Task CompleteAsync(Guid runId, bool truncated = false, string? truncationReason = null, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;

            RefreshLedgerWallClock(runId);

            string? extraJson = null;
            if (truncated)
                extraJson = JsonSerializer.Serialize(new { truncated = true, reason = truncationReason }, JsonOptions);

            using var cmd = Connection().CreateCommand();
            cmd.CommandText = truncated
                ? "UPDATE AgentRuns SET State=@State, CompletedAt=@Now, UpdatedAt=@Now, ExtraJson=@Extra WHERE Id=@Id"
                : "UPDATE AgentRuns SET State=@State, CompletedAt=@Now, UpdatedAt=@Now WHERE Id=@Id";
            cmd.Parameters.AddWithValue("@State", (int)AgentRunState.Completed);
            cmd.Parameters.AddWithValue("@Now", now.ToString("O"));
            if (truncated)
                cmd.Parameters.AddWithValue("@Extra", ToParam(extraJson));
            cmd.Parameters.AddWithValue("@Id", runId.ToString());
            cmd.ExecuteNonQuery();
        }

        _logger.LogInformation("Run {RunId} → Completed (truncated={Truncated})", runId, truncated);
        RunChanged?.Invoke(this, new AgentRunChangedEventArgs(runId, AgentRunState.Completed));
        return Task.CompletedTask;
    }

    public Task FailAsync(Guid runId, string? error, bool cancelled = false, CancellationToken ct = default)
    {
        var state = cancelled ? AgentRunState.Cancelled : AgentRunState.Failed;
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;

            RefreshLedgerWallClock(runId);

            var extraJson = error is not null
                ? JsonSerializer.Serialize(new { error }, JsonOptions)
                : null;

            using var cmd = Connection().CreateCommand();
            cmd.CommandText = "UPDATE AgentRuns SET State=@State, CompletedAt=@Now, UpdatedAt=@Now, ExtraJson=@Extra WHERE Id=@Id";
            cmd.Parameters.AddWithValue("@State", (int)state);
            cmd.Parameters.AddWithValue("@Now", now.ToString("O"));
            cmd.Parameters.AddWithValue("@Extra", ToParam(extraJson));
            cmd.Parameters.AddWithValue("@Id", runId.ToString());
            cmd.ExecuteNonQuery();
        }

        _logger.LogInformation("Run {RunId} → {State}", runId, state);
        RunChanged?.Invoke(this, new AgentRunChangedEventArgs(runId, state));
        return Task.CompletedTask;
    }

    public Task PauseAsync(Guid runId, string? reason, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;

            // Freeze accrued wall-clock into the persisted ledger before parking (mirrors CompleteAsync).
            RefreshLedgerWallClock(runId);

            var extraJson = JsonSerializer.Serialize(new { paused = true, reason }, JsonOptions);

            using var cmd = Connection().CreateCommand();
            cmd.CommandText = "UPDATE AgentRuns SET State=@State, UpdatedAt=@Now, ExtraJson=@Extra WHERE Id=@Id";
            cmd.Parameters.AddWithValue("@State", (int)AgentRunState.WaitingForInput);
            cmd.Parameters.AddWithValue("@Now", now.ToString("O"));
            cmd.Parameters.AddWithValue("@Extra", ToParam(extraJson));
            cmd.Parameters.AddWithValue("@Id", runId.ToString());
            cmd.ExecuteNonQuery();
        }

        _logger.LogInformation("Run {RunId} → WaitingForInput (paused)", runId);        // scalar, safe
        _logger.SensitiveDebug("Run {RunId} pause reason: {Reason}", runId, reason);    // guardrail 8
        RunChanged?.Invoke(this, new AgentRunChangedEventArgs(runId, AgentRunState.WaitingForInput));
        return Task.CompletedTask;
    }

    public Task<bool> TryBeginResumeAsync(Guid runId, CancellationToken ct = default)
    {
        int affected;
        lock (_gate)
        {
            if (_disposed) return Task.FromResult(false);

            // Single-connection + _gate makes `WHERE State=@Expected` an atomic CAS — the only writer.
            // A second racer (double-click, panel+Flow) finds State != WaitingForInput → 0 rows → loses.
            using var cmd = Connection().CreateCommand();
            cmd.CommandText = "UPDATE AgentRuns SET State=@New, UpdatedAt=@Now WHERE Id=@Id AND State=@Expected";
            cmd.Parameters.AddWithValue("@New", (int)AgentRunState.Running);
            cmd.Parameters.AddWithValue("@Now", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@Id", runId.ToString());
            cmd.Parameters.AddWithValue("@Expected", (int)AgentRunState.WaitingForInput);
            affected = cmd.ExecuteNonQuery();
        }

        if (affected > 0)
        {
            _logger.LogInformation("Run {RunId} resume claimed → Running", runId);
            RunChanged?.Invoke(this, new AgentRunChangedEventArgs(runId, AgentRunState.Running));
        }
        return Task.FromResult(affected > 0);
    }

    public Task<int> FailInterruptedRunsAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        int affected;
        lock (_gate)
        {
            if (_disposed) return Task.FromResult(0);

            using var cmd = Connection().CreateCommand();
            // States 0..2 (Planning/Running/Verifying) are crash-recoverable — settle to Cancelled.
            // 3/4 (WaitingForInput/Paused) are a DELIBERATE parked state (budget pause) and MUST survive
            // restart resumable — never swept. 5-7 (Completed/Failed/Cancelled) are terminal.
            // No per-row RunChanged: these are not live transitions (the Flow surface would otherwise
            // re-publish stale items at startup).
            cmd.CommandText = "UPDATE AgentRuns SET State=@State, CompletedAt=@Now, UpdatedAt=@Now WHERE State < @Terminal";
            cmd.Parameters.AddWithValue("@State", (int)AgentRunState.Cancelled);
            cmd.Parameters.AddWithValue("@Now", now.ToString("O"));
            cmd.Parameters.AddWithValue("@Terminal", (int)AgentRunState.WaitingForInput);
            affected = cmd.ExecuteNonQuery();
        }

        if (affected > 0)
            _logger.LogInformation("Settled {Count} interrupted agent run(s) to Cancelled at startup", affected);
        return Task.FromResult(affected);
    }

    public Task<AgentRun?> GetAsync(Guid runId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_disposed) return Task.FromResult<AgentRun?>(null);

            var connection = Connection();
            AgentRun? run;
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $"SELECT {RunColumns} FROM AgentRuns WHERE Id=@Id";
                cmd.Parameters.AddWithValue("@Id", runId.ToString());
                using var reader = cmd.ExecuteReader();
                if (!reader.Read()) return Task.FromResult<AgentRun?>(null);
                run = MapRun(reader);
            }

            run.Plan = LoadSteps(connection, runId);
            return Task.FromResult<AgentRun?>(run);
        }
    }

    public Task<IReadOnlyList<AgentRun>> GetByChatAsync(Guid chatId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_disposed) return Task.FromResult<IReadOnlyList<AgentRun>>(Array.Empty<AgentRun>());

            var connection = Connection();
            var runs = new List<AgentRun>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $"SELECT {RunColumns} FROM AgentRuns WHERE ChatId=@ChatId ORDER BY CreatedAt ASC";
                cmd.Parameters.AddWithValue("@ChatId", chatId.ToString());
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    runs.Add(MapRun(reader));
            }

            foreach (var run in runs)
                run.Plan = LoadSteps(connection, run.Id);

            return Task.FromResult<IReadOnlyList<AgentRun>>(runs);
        }
    }

    public Task<bool> ChatHasPlannedRunAsync(Guid chatId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_disposed) return Task.FromResult(false);

            using var cmd = Connection().CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM AgentRuns WHERE ChatId=@ChatId AND RunShape=@Shape";
            cmd.Parameters.AddWithValue("@ChatId", chatId.ToString());
            cmd.Parameters.AddWithValue("@Shape", (int)RunShape.Planned);
            var count = Convert.ToInt64(cmd.ExecuteScalar());
            return Task.FromResult(count > 0);
        }
    }

    public Task ReplaceStepsAsync(Guid runId, IReadOnlyList<AgentStep> steps, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;

            var connection = Connection();
            using var transaction = connection.BeginTransaction();

            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM AgentSteps WHERE RunId=@RunId";
                delete.Parameters.AddWithValue("@RunId", runId.ToString());
                delete.ExecuteNonQuery();
            }

            var now = DateTime.UtcNow;
            foreach (var step in steps)
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO AgentSteps
                        (Id, RunId, Ordinal, Title, Intent, Status, ExpectedArtifact, AssignedPersonaId,
                         DependsOnJson, ReRunnable, FirstMessageId, LastMessageId, CreatedAt, UpdatedAt, ExtraJson)
                    VALUES
                        (@Id, @RunId, @Ordinal, @Title, @Intent, @Status, @ExpectedArtifact, @AssignedPersonaId,
                         @DependsOnJson, @ReRunnable, @FirstMessageId, @LastMessageId, @CreatedAt, @UpdatedAt, @ExtraJson)
                    """;
                insert.Parameters.AddWithValue("@Id", (step.Id == Guid.Empty ? Guid.NewGuid() : step.Id).ToString());
                insert.Parameters.AddWithValue("@RunId", runId.ToString());
                insert.Parameters.AddWithValue("@Ordinal", step.Ordinal);
                insert.Parameters.AddWithValue("@Title", step.Title);
                insert.Parameters.AddWithValue("@Intent", ToParam(step.Intent));
                insert.Parameters.AddWithValue("@Status", (int)step.Status);
                insert.Parameters.AddWithValue("@ExpectedArtifact", ToParam(step.ExpectedArtifact));
                insert.Parameters.AddWithValue("@AssignedPersonaId", ToParam(step.AssignedPersonaId));
                insert.Parameters.AddWithValue("@DependsOnJson", ToParam(step.DependsOnJson));
                insert.Parameters.AddWithValue("@ReRunnable", step.ReRunnable ? 1 : 0);
                insert.Parameters.AddWithValue("@FirstMessageId", ToParam(step.FirstMessageId));
                insert.Parameters.AddWithValue("@LastMessageId", ToParam(step.LastMessageId));
                insert.Parameters.AddWithValue("@CreatedAt", (step.CreatedAt == default ? now : step.CreatedAt).ToString("O"));
                insert.Parameters.AddWithValue("@UpdatedAt", now.ToString("O"));
                insert.Parameters.AddWithValue("@ExtraJson", ToParam(step.ExtraJson));
                insert.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        return Task.CompletedTask;
    }

    public Task<AgentStep?> NextPendingStepAsync(Guid runId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_disposed) return Task.FromResult<AgentStep?>(null);

            // Re-query the persisted Pending steps on every call (never iterate a snapshot — §16 R2).
            using var cmd = Connection().CreateCommand();
            cmd.CommandText = $"""
                SELECT {StepColumns} FROM AgentSteps
                WHERE RunId=@RunId AND Status=@Pending
                ORDER BY Ordinal ASC
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue("@RunId", runId.ToString());
            cmd.Parameters.AddWithValue("@Pending", (int)AgentStepStatus.Pending);
            using var reader = cmd.ExecuteReader();
            return Task.FromResult(reader.Read() ? MapStep(reader) : null);
        }
    }

    public Task SetStepStatusAsync(Guid stepId, AgentStepStatus status, CancellationToken ct = default)
    {
        Guid runId;
        AgentRunState runState;
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;

            using (var cmd = Connection().CreateCommand())
            {
                cmd.CommandText = "UPDATE AgentSteps SET Status=@Status, UpdatedAt=@Now WHERE Id=@Id";
                cmd.Parameters.AddWithValue("@Status", (int)status);
                cmd.Parameters.AddWithValue("@Now", DateTime.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("@Id", stepId.ToString());
                cmd.ExecuteNonQuery();
            }

            if (!TryLoadStepRun(stepId, out runId, out runState))
                return Task.CompletedTask;
        }

        RunChanged?.Invoke(this, new AgentRunChangedEventArgs(runId, runState, stepId));
        return Task.CompletedTask;
    }

    public Task RecordStepResultAsync(Guid stepId, AgentStepStatus status,
        Guid? firstMessageId, Guid? lastMessageId, UsageDetails? usage, CancellationToken ct = default)
    {
        Guid runId;
        AgentRunState runState;
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;

            using (var cmd = Connection().CreateCommand())
            {
                cmd.CommandText = """
                    UPDATE AgentSteps
                    SET Status=@Status, FirstMessageId=@First, LastMessageId=@Last, UpdatedAt=@Now
                    WHERE Id=@Id
                    """;
                cmd.Parameters.AddWithValue("@Status", (int)status);
                cmd.Parameters.AddWithValue("@First", ToParam(firstMessageId));
                cmd.Parameters.AddWithValue("@Last", ToParam(lastMessageId));
                cmd.Parameters.AddWithValue("@Now", DateTime.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("@Id", stepId.ToString());
                cmd.ExecuteNonQuery();
            }

            if (!TryLoadStepRun(stepId, out runId, out runState))
                return Task.CompletedTask;

            if (usage is not null && TryLoadRunLedger(runId, out var ledger, out var startedAt, out _))
            {
                var input = usage.InputTokenCount ?? 0;
                var output = usage.OutputTokenCount ?? 0;
                ledger.InputTokens += input;
                ledger.OutputTokens += output;
                var entry = ledger.PerStep.FirstOrDefault(s => s.StepId == stepId.ToString());
                if (entry is null)
                {
                    entry = new StepLedger { StepId = stepId.ToString() };
                    ledger.PerStep.Add(entry);
                }
                entry.InputTokens += input;
                entry.OutputTokens += output;
                ledger.WallClockMs = ElapsedMs(startedAt);
                WriteLedger(runId, ledger);
            }
        }

        RunChanged?.Invoke(this, new AgentRunChangedEventArgs(runId, runState, stepId));
        return Task.CompletedTask;
    }

    // ---- helpers (all invoked under _gate) ----

    private const string RunColumns =
        "Id, SchemaVersion, ChatId, RunShape, State, TriggerKind, TriggerRef, ParentRunId, OwnerDeviceId, " +
        "Goal, FirstMessageId, LastMessageId, PolicyJson, LedgerJson, CreatedAt, UpdatedAt, StartedAt, CompletedAt, ExtraJson";

    private const string StepColumns =
        "Id, RunId, Ordinal, Title, Intent, Status, ExpectedArtifact, AssignedPersonaId, DependsOnJson, " +
        "ReRunnable, FirstMessageId, LastMessageId, CreatedAt, UpdatedAt, ExtraJson";

    private static AgentRun MapRun(SqliteDataReader r) => new()
    {
        Id = Guid.Parse(r.GetString(0)),
        SchemaVersion = r.GetInt32(1),
        ChatId = Guid.Parse(r.GetString(2)),
        RunShape = (RunShape)r.GetInt32(3),
        State = (AgentRunState)r.GetInt32(4),
        TriggerKind = (AgentRunTrigger)r.GetInt32(5),
        TriggerRef = ParseNullableGuid(r, 6),
        ParentRunId = ParseNullableGuid(r, 7),
        OwnerDeviceId = ParseNullableGuid(r, 8),
        Goal = r.IsDBNull(9) ? null : r.GetString(9),
        FirstMessageId = ParseNullableGuid(r, 10),
        LastMessageId = ParseNullableGuid(r, 11),
        PolicyJson = r.IsDBNull(12) ? null : r.GetString(12),
        LedgerJson = r.IsDBNull(13) ? null : r.GetString(13),
        CreatedAt = DateTime.Parse(r.GetString(14)).ToUniversalTime(),
        UpdatedAt = DateTime.Parse(r.GetString(15)).ToUniversalTime(),
        StartedAt = r.IsDBNull(16) ? null : DateTime.Parse(r.GetString(16)).ToUniversalTime(),
        CompletedAt = r.IsDBNull(17) ? null : DateTime.Parse(r.GetString(17)).ToUniversalTime(),
        ExtraJson = r.IsDBNull(18) ? null : r.GetString(18),
    };

    private static AgentStep MapStep(SqliteDataReader r) => new()
    {
        Id = Guid.Parse(r.GetString(0)),
        RunId = Guid.Parse(r.GetString(1)),
        Ordinal = r.GetInt32(2),
        Title = r.GetString(3),
        Intent = r.IsDBNull(4) ? null : r.GetString(4),
        Status = (AgentStepStatus)r.GetInt32(5),
        ExpectedArtifact = r.IsDBNull(6) ? null : r.GetString(6),
        AssignedPersonaId = ParseNullableGuid(r, 7),
        DependsOnJson = r.IsDBNull(8) ? null : r.GetString(8),
        ReRunnable = r.GetInt32(9) == 1,
        FirstMessageId = ParseNullableGuid(r, 10),
        LastMessageId = ParseNullableGuid(r, 11),
        CreatedAt = DateTime.Parse(r.GetString(12)).ToUniversalTime(),
        UpdatedAt = DateTime.Parse(r.GetString(13)).ToUniversalTime(),
        ExtraJson = r.IsDBNull(14) ? null : r.GetString(14),
    };

    private static IReadOnlyList<AgentStep> LoadSteps(SqliteConnection connection, Guid runId)
    {
        var steps = new List<AgentStep>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {StepColumns} FROM AgentSteps WHERE RunId=@RunId ORDER BY Ordinal ASC";
        cmd.Parameters.AddWithValue("@RunId", runId.ToString());
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            steps.Add(MapStep(reader));
        return steps;
    }

    private bool TryLoadRunLedger(Guid runId, out Ledger ledger, out DateTime? startedAt, out AgentRunState state)
    {
        ledger = new Ledger();
        startedAt = null;
        state = AgentRunState.Running;

        using var cmd = Connection().CreateCommand();
        cmd.CommandText = "SELECT LedgerJson, StartedAt, State FROM AgentRuns WHERE Id=@Id";
        cmd.Parameters.AddWithValue("@Id", runId.ToString());
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return false;

        if (!reader.IsDBNull(0))
        {
            var parsed = TryDeserializeLedger(reader.GetString(0));
            if (parsed is not null) ledger = parsed;
        }
        startedAt = reader.IsDBNull(1) ? null : DateTime.Parse(reader.GetString(1)).ToUniversalTime();
        state = (AgentRunState)reader.GetInt32(2);
        return true;
    }

    private bool TryLoadStepRun(Guid stepId, out Guid runId, out AgentRunState runState)
    {
        runId = Guid.Empty;
        runState = AgentRunState.Running;

        using var cmd = Connection().CreateCommand();
        cmd.CommandText = """
            SELECT s.RunId, r.State FROM AgentSteps s
            JOIN AgentRuns r ON r.Id = s.RunId
            WHERE s.Id=@Id
            """;
        cmd.Parameters.AddWithValue("@Id", stepId.ToString());
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return false;
        runId = Guid.Parse(reader.GetString(0));
        runState = (AgentRunState)reader.GetInt32(1);
        return true;
    }

    private void RefreshLedgerWallClock(Guid runId)
    {
        if (!TryLoadRunLedger(runId, out var ledger, out var startedAt, out _)) return;
        ledger.WallClockMs = ElapsedMs(startedAt);
        WriteLedger(runId, ledger);
    }

    private void WriteLedger(Guid runId, Ledger ledger)
    {
        using var cmd = Connection().CreateCommand();
        cmd.CommandText = "UPDATE AgentRuns SET LedgerJson=@Ledger, UpdatedAt=@Now WHERE Id=@Id";
        cmd.Parameters.AddWithValue("@Ledger", JsonSerializer.Serialize(ledger, JsonOptions));
        cmd.Parameters.AddWithValue("@Now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@Id", runId.ToString());
        cmd.ExecuteNonQuery();
    }

    private static Ledger? TryDeserializeLedger(string json)
    {
        try { return JsonSerializer.Deserialize<Ledger>(json, JsonOptions); }
        catch (JsonException) { return null; }
    }

    private static long ElapsedMs(DateTime? startedAt) =>
        startedAt is { } s ? Math.Max(0, (long)(DateTime.UtcNow - s).TotalMilliseconds) : 0;

    private static Guid? ParseNullableGuid(SqliteDataReader r, int ordinal) =>
        r.IsDBNull(ordinal) ? null : Guid.Parse(r.GetString(ordinal));

    private static object ToParam(string? value) => value is null ? DBNull.Value : value;

    private static object ToParam(Guid? value) => value is { } g ? g.ToString() : DBNull.Value;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _connection?.Dispose();
            _connection = null;
        }
    }

    private sealed class Ledger
    {
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public double? CostUsd { get; set; }
        public long WallClockMs { get; set; }
        public List<StepLedger> PerStep { get; set; } = [];
    }

    private sealed class StepLedger
    {
        public string StepId { get; set; } = string.Empty;
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
    }
}
