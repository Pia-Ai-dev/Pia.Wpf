using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>
/// SQLite-backed tool-exchange store on its own connection, mirroring <see cref="AgentTimelineService"/>. Rows
/// hold real payloads, so no log line here carries one: ids, counts, char totals and tool names only.
/// </summary>
public sealed class AgentToolExchangeStore : IAgentToolExchangeStore, IDisposable
{
    /// <summary>Call/Result rows retained per run. A batch that would cross it is refused whole.</summary>
    public const int MaxRowsPerRun = 500;

    /// <summary>Call/Result payload chars retained per run.</summary>
    public const int MaxCharsPerRun = 4_000_000;

    private const string ColumnList =
        "Id, SchemaVersion, RunId, StepId, MessageSeq, Seq, Round, Role, Kind, CallId, ToolName, PluginId, " +
        "ArgumentsJson, ArgsOmitted, DisplayArgs, ResultKind, ResultText, Chars, AnchorMessageId, CreatedAt, " +
        "ReplayedAt, SupersededAt";

    private readonly string _connectionString;
    private readonly ILogger<AgentToolExchangeStore> _logger;
    private readonly object _gate = new();

    /// <summary>Runs whose cap has already been reported, so the Information line fires once per run.</summary>
    private readonly HashSet<Guid> _capNoted = [];

    private SqliteConnection? _connection;
    private bool _disposed;

    public AgentToolExchangeStore(SqliteContext context, ILogger<AgentToolExchangeStore> logger)
    {
        _connectionString = context.ConnectionString;
        _logger = logger;

        // Force EnsureSchema (which owns the AgentToolExchanges DDL) before this connection ever opens.
        context.GetConnection();
    }

    /// <summary>Must be called while holding <see cref="_gate"/>.</summary>
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

    public Task RecordAsync(
        Guid runId, Guid? stepId, int round, IReadOnlyList<ChatMessage> messages, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested || messages.Count == 0)
            return Task.CompletedTask;

        try
        {
            lock (_gate)
            {
                if (_disposed) return Task.CompletedTask;

                var connection = Connection();
                using var transaction = connection.BeginTransaction();

                var totals = ReadTotals(connection, transaction, runId);
                var rows = AgentToolExchangeSerializer.ToRows(
                    runId, stepId, round, totals.MaxSeq, totals.MaxMessageSeq, messages, DateTime.UtcNow);
                if (rows.Count == 0)
                    return Task.CompletedTask;

                var batchChars = rows.Sum(r => (long)r.Chars);
                if (totals.CarriedRows + rows.Count > MaxRowsPerRun ||
                    totals.CarriedChars + batchChars > MaxCharsPerRun)
                {
                    // All-or-nothing: a partial batch would store a call with no result.
                    transaction.Rollback();
                    if (_capNoted.Add(runId))
                    {
                        _logger.LogInformation(
                            "Tool-exchange cap reached for run {RunId} at {Rows} rows and {Chars} chars; later rounds are not recorded",
                            runId, totals.CarriedRows, totals.CarriedChars);
                    }

                    return Task.CompletedTask;
                }

                foreach (var row in rows)
                    Insert(connection, transaction, row);

                transaction.Commit();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tool-exchange record failed for run {RunId} round {Round}", runId, round);
        }

        return Task.CompletedTask;
    }

    public Task AppendParkedAsync(IReadOnlyList<AgentToolExchangeRow> rows, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested || rows.Count == 0)
            return Task.CompletedTask;

        try
        {
            lock (_gate)
            {
                if (_disposed) return Task.CompletedTask;

                var connection = Connection();
                using var transaction = connection.BeginTransaction();

                foreach (var group in rows.GroupBy(r => r.RunId))
                {
                    var totals = ReadTotals(connection, transaction, group.Key);
                    var seq = totals.MaxSeq;
                    // One message group per pass: the calls of one round rebuild into one assistant message.
                    var messageSeq = totals.MaxMessageSeq + 1;

                    foreach (var row in group)
                    {
                        // Exempt from the per-run cap: this is the row a Continue press replays.
                        Insert(connection, transaction, row with { Seq = ++seq, MessageSeq = messageSeq });
                    }
                }

                transaction.Commit();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tool-exchange park append failed for {Count} rows", rows.Count);
        }

        return Task.CompletedTask;
    }

    public Task<int> SealStepAsync(Guid runId, Guid? stepId, Guid anchorMessageId, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
            return Task.FromResult(0);

        try
        {
            lock (_gate)
            {
                if (_disposed) return Task.FromResult(0);

                using var cmd = Connection().CreateCommand();
                cmd.CommandText = """
                    UPDATE AgentToolExchanges
                    SET AnchorMessageId = @Anchor
                    WHERE RunId = @RunId
                      AND AnchorMessageId IS NULL
                      AND (StepId = @StepId OR (@StepId IS NULL AND StepId IS NULL));
                    """;
                cmd.Parameters.AddWithValue("@Anchor", anchorMessageId.ToString());
                cmd.Parameters.AddWithValue("@RunId", runId.ToString());
                cmd.Parameters.AddWithValue("@StepId", (object?)stepId?.ToString() ?? DBNull.Value);
                return Task.FromResult(cmd.ExecuteNonQuery());
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tool-exchange seal failed for run {RunId}", runId);
            return Task.FromResult(0);
        }
    }

    public Task<IReadOnlyList<AgentToolExchangeRow>> ReadCarriedAsync(Guid runId, CancellationToken ct = default)
    {
        // Kind IN (1,2) is load-bearing: re-seeding a parked row would send a second call under a CallId the
        // Call row already used.
        return Task.FromResult(Query(
            $"SELECT {ColumnList} FROM AgentToolExchanges WHERE RunId = @RunId AND Kind IN (1, 2) ORDER BY Seq;",
            cmd => cmd.Parameters.AddWithValue("@RunId", runId.ToString()),
            runId,
            ct));
    }

    public Task<IReadOnlyList<AgentToolExchangeRow>> GetReplayableAsync(
        Guid runId, string toolName, CancellationToken ct = default)
    {
        return Task.FromResult(Query(
            $"""
            SELECT {ColumnList} FROM AgentToolExchanges
            WHERE RunId = @RunId AND Kind IN (3, 4) AND ToolName = @ToolName COLLATE NOCASE
              AND ReplayedAt IS NULL AND SupersededAt IS NULL
            ORDER BY Seq;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@RunId", runId.ToString());
                cmd.Parameters.AddWithValue("@ToolName", toolName);
            },
            runId,
            ct));
    }

    public Task<AgentToolExchangeRow?> GetParkedCallAsync(
        Guid runId, string toolName, CancellationToken ct = default)
    {
        var rows = Query(
            $"""
            SELECT {ColumnList} FROM AgentToolExchanges
            WHERE RunId = @RunId AND Kind = 3 AND ToolName = @ToolName COLLATE NOCASE
              AND ReplayedAt IS NULL AND SupersededAt IS NULL
            ORDER BY Seq DESC LIMIT 1;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@RunId", runId.ToString());
                cmd.Parameters.AddWithValue("@ToolName", toolName);
            },
            runId,
            ct);

        return Task.FromResult(rows.Count == 0 ? null : rows[0]);
    }

    public Task<int> SupersedeUnreplayedAsync(
        Guid runId, IReadOnlyCollection<string> toolNames, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested || toolNames.Count == 0)
            return Task.FromResult(0);

        try
        {
            lock (_gate)
            {
                if (_disposed) return Task.FromResult(0);

                var names = toolNames.ToList();
                var placeholders = string.Join(", ", names.Select((_, i) => $"@Tool{i}"));

                using var cmd = Connection().CreateCommand();
                // COLLATE binds to the operand, not to the IN result, so it goes on the left of IN.
                cmd.CommandText = $"""
                    UPDATE AgentToolExchanges
                    SET SupersededAt = @Now
                    WHERE RunId = @RunId AND ToolName COLLATE NOCASE IN ({placeholders})
                      AND Kind IN (3, 4) AND ReplayedAt IS NULL AND SupersededAt IS NULL;
                    """;
                cmd.Parameters.AddWithValue("@Now", DateTime.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("@RunId", runId.ToString());
                for (var i = 0; i < names.Count; i++)
                    cmd.Parameters.AddWithValue($"@Tool{i}", names[i]);

                return Task.FromResult(cmd.ExecuteNonQuery());
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tool-exchange supersede failed for run {RunId}", runId);
            return Task.FromResult(0);
        }
    }

    public Task<bool> TryMarkReplayedAsync(Guid id, DateTime replayedAt, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
            return Task.FromResult(false);

        try
        {
            lock (_gate)
            {
                if (_disposed) return Task.FromResult(false);

                using var cmd = Connection().CreateCommand();
                // Conditional, and the rows-affected result is the answer: this is at-most-once's structure.
                cmd.CommandText =
                    "UPDATE AgentToolExchanges SET ReplayedAt = @At WHERE Id = @Id AND ReplayedAt IS NULL;";
                cmd.Parameters.AddWithValue("@At", replayedAt.ToString("O"));
                cmd.Parameters.AddWithValue("@Id", id.ToString());
                return Task.FromResult(cmd.ExecuteNonQuery() == 1);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tool-exchange replay claim failed for row {RowId}", id);
            return Task.FromResult(false);
        }
    }

    public Task SetResultAsync(Guid id, string? resultText, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
            return Task.CompletedTask;

        try
        {
            lock (_gate)
            {
                if (_disposed) return Task.CompletedTask;

                using var cmd = Connection().CreateCommand();
                cmd.CommandText = """
                    UPDATE AgentToolExchanges
                    SET ResultKind = @ResultKind,
                        ResultText = @ResultText,
                        Chars = COALESCE(LENGTH(ArgumentsJson), 0) + COALESCE(LENGTH(@ResultText), 0)
                    WHERE Id = @Id;
                    """;
                cmd.Parameters.AddWithValue("@ResultKind",
                    (int)(resultText is null ? AgentToolExchangeResult.None : AgentToolExchangeResult.Text));
                cmd.Parameters.AddWithValue("@ResultText", (object?)resultText ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Id", id.ToString());
                cmd.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tool-exchange result update failed for row {RowId}", id);
        }

        return Task.CompletedTask;
    }

    public Task<int> DeleteReplayableAsync(Guid runId, string toolName, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
            return Task.FromResult(0);

        try
        {
            lock (_gate)
            {
                if (_disposed) return Task.FromResult(0);

                using var cmd = Connection().CreateCommand();
                cmd.CommandText = """
                    DELETE FROM AgentToolExchanges
                    WHERE RunId = @RunId AND Kind IN (3, 4) AND ToolName = @ToolName COLLATE NOCASE;
                    """;
                cmd.Parameters.AddWithValue("@RunId", runId.ToString());
                cmd.Parameters.AddWithValue("@ToolName", toolName);
                var deleted = cmd.ExecuteNonQuery();
                if (deleted > 0)
                {
                    _logger.LogInformation(
                        "Declined tool {ToolName} on run {RunId}: dropped {Count} replayable rows",
                        toolName, runId, deleted);
                }

                return Task.FromResult(deleted);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tool-exchange decline delete failed for run {RunId}", runId);
            return Task.FromResult(0);
        }
    }

    public Task<int> PurgeRunAsync(Guid runId, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
            return Task.FromResult(0);

        try
        {
            lock (_gate)
            {
                if (_disposed) return Task.FromResult(0);

                using var cmd = Connection().CreateCommand();
                cmd.CommandText = "DELETE FROM AgentToolExchanges WHERE RunId = @RunId;";
                cmd.Parameters.AddWithValue("@RunId", runId.ToString());
                var deleted = cmd.ExecuteNonQuery();
                _capNoted.Remove(runId);

                if (deleted > 0)
                    _logger.LogInformation("Purged {Count} tool-exchange rows for run {RunId}", deleted, runId);

                return Task.FromResult(deleted);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tool-exchange purge failed for run {RunId}", runId);
            return Task.FromResult(0);
        }
    }

    public Task<int> PruneAsync(DateTime cutoff, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
            return Task.FromResult(0);

        try
        {
            lock (_gate)
            {
                if (_disposed) return Task.FromResult(0);

                using var cmd = Connection().CreateCommand();
                // The terminal set is explicit and never a range: WaitingForChildren sits ABOVE the terminal
                // band. The second clause has no age filter — it is what catches a run whose process died
                // before its own purge ran.
                cmd.CommandText = """
                    DELETE FROM AgentToolExchanges
                    WHERE CreatedAt < @Cutoff
                       OR RunId IN (SELECT Id FROM AgentRuns WHERE State IN (5, 6, 7));
                    """;
                cmd.Parameters.AddWithValue("@Cutoff", cutoff.ToString("O"));
                var deleted = cmd.ExecuteNonQuery();

                if (deleted > 0)
                    _logger.LogInformation("Tool-exchange retention deleted {Count} rows", deleted);

                return Task.FromResult(deleted);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tool-exchange prune failed");
            return Task.FromResult(0);
        }
    }

    /// <summary>Maxima over ALL kinds; counts and char sums over Call/Result only, because the cap is theirs.</summary>
    private static Totals ReadTotals(SqliteConnection connection, SqliteTransaction transaction, Guid runId)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            SELECT COALESCE(MAX(Seq), 0),
                   COALESCE(MAX(MessageSeq), 0),
                   COUNT(CASE WHEN Kind IN (1, 2) THEN 1 END),
                   COALESCE(SUM(CASE WHEN Kind IN (1, 2) THEN Chars END), 0)
            FROM AgentToolExchanges
            WHERE RunId = @RunId;
            """;
        cmd.Parameters.AddWithValue("@RunId", runId.ToString());

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return new Totals(0, 0, 0, 0);

        return new Totals(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetInt64(3));
    }

    private readonly record struct Totals(long MaxSeq, long MaxMessageSeq, int CarriedRows, long CarriedChars);

    private static void Insert(SqliteConnection connection, SqliteTransaction transaction, AgentToolExchangeRow row)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"""
            INSERT INTO AgentToolExchanges ({ColumnList})
            VALUES
                (@Id, @SchemaVersion, @RunId, @StepId, @MessageSeq, @Seq, @Round, @Role, @Kind, @CallId,
                 @ToolName, @PluginId, @ArgumentsJson, @ArgsOmitted, @DisplayArgs, @ResultKind, @ResultText,
                 @Chars, @AnchorMessageId, @CreatedAt, @ReplayedAt, @SupersededAt);
            """;
        cmd.Parameters.AddWithValue("@Id", row.Id.ToString());
        cmd.Parameters.AddWithValue("@SchemaVersion", row.SchemaVersion);
        cmd.Parameters.AddWithValue("@RunId", row.RunId.ToString());
        cmd.Parameters.AddWithValue("@StepId", (object?)row.StepId?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@MessageSeq", row.MessageSeq);
        cmd.Parameters.AddWithValue("@Seq", row.Seq);
        cmd.Parameters.AddWithValue("@Round", (object?)row.Round ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Role", row.Role);
        cmd.Parameters.AddWithValue("@Kind", (int)row.Kind);
        cmd.Parameters.AddWithValue("@CallId", row.CallId);
        cmd.Parameters.AddWithValue("@ToolName", (object?)row.ToolName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@PluginId", (object?)row.PluginId?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ArgumentsJson", (object?)row.ArgumentsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ArgsOmitted", row.ArgsOmitted ? 1 : 0);
        cmd.Parameters.AddWithValue("@DisplayArgs", (object?)row.DisplayArgs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ResultKind", (int)row.ResultKind);
        cmd.Parameters.AddWithValue("@ResultText", (object?)row.ResultText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Chars", row.Chars);
        cmd.Parameters.AddWithValue("@AnchorMessageId", (object?)row.AnchorMessageId?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CreatedAt", row.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@ReplayedAt", (object?)row.ReplayedAt?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@SupersededAt", (object?)row.SupersededAt?.ToString("O") ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private IReadOnlyList<AgentToolExchangeRow> Query(
        string sql, Action<SqliteCommand> bind, Guid runId, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return [];

        try
        {
            lock (_gate)
            {
                if (_disposed) return [];

                using var cmd = Connection().CreateCommand();
                cmd.CommandText = sql;
                bind(cmd);

                var rows = new List<AgentToolExchangeRow>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    rows.Add(Map(reader));

                return rows;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tool-exchange read failed for run {RunId}", runId);
            return [];
        }
    }

    /// <summary>Reader indexes are positions in <see cref="ColumnList"/>; keep the two in the same order.</summary>
    private static AgentToolExchangeRow Map(SqliteDataReader r) => new(
        Id: Guid.Parse(r.GetString(0)),
        RunId: Guid.Parse(r.GetString(2)),
        StepId: r.IsDBNull(3) ? null : Guid.Parse(r.GetString(3)),
        MessageSeq: r.GetInt64(4),
        Seq: r.GetInt64(5),
        Round: r.IsDBNull(6) ? null : r.GetInt32(6),
        Role: r.GetString(7),
        // Not coerced: the enums are append-only, so an ordinal this build does not know survives the read.
        Kind: (AgentToolExchangeKind)r.GetInt32(8),
        CallId: r.GetString(9),
        ToolName: r.IsDBNull(10) ? null : r.GetString(10),
        PluginId: r.IsDBNull(11) ? null : Guid.Parse(r.GetString(11)),
        ArgumentsJson: r.IsDBNull(12) ? null : r.GetString(12),
        ArgsOmitted: r.GetInt32(13) != 0,
        DisplayArgs: r.IsDBNull(14) ? null : r.GetString(14),
        ResultKind: (AgentToolExchangeResult)r.GetInt32(15),
        ResultText: r.IsDBNull(16) ? null : r.GetString(16),
        Chars: r.GetInt32(17),
        AnchorMessageId: r.IsDBNull(18) ? null : Guid.Parse(r.GetString(18)),
        CreatedAt: ParseTimestamp(r.GetString(19)),
        ReplayedAt: r.IsDBNull(20) ? null : ParseTimestamp(r.GetString(20)),
        SupersededAt: r.IsDBNull(21) ? null : ParseTimestamp(r.GetString(21)))
    {
        SchemaVersion = r.GetInt32(1),
    };

    private static DateTime ParseTimestamp(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

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
}
