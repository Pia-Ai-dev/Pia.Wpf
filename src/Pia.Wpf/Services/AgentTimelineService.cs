using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>
/// SQLite-backed store for the per-run audit timeline (Batch 03).
/// <para>
/// Infrastructure mirrors <see cref="AgentRunService"/> deliberately: its own dedicated
/// <see cref="SqliteConnection"/> (not the shared <see cref="SqliteContext"/> one, which has UI-initiated
/// thread affinity) with <c>PRAGMA busy_timeout=3000</c>, opened lazily, guarded by a plain lock, and the
/// shared connection forced once in the ctor so <c>EnsureSchema</c> — which owns the
/// <c>AgentTimelineEvents</c> DDL — has run before this one opens.
/// </para>
/// <para>
/// <b>Emit is fire-and-forget by design.</b> <c>Seq</c> is allocated synchronously under
/// <see cref="_gate"/> (a handful of instructions on an in-memory dictionary) and the INSERT is chained onto a
/// serial writer task. A synchronous DB write would block the WPF message pump, because the interactive gate
/// runs on the UI thread — the hazard <c>AssistantChatService</c>'s class remarks were written to document.
/// Ordering therefore does not depend on when the writes land: it depends only on the synchronous <c>Seq</c>
/// allocation.
/// </para>
/// <para>
/// <b>TWO locks, and the split is what makes the paragraph above TRUE.</b> <see cref="_gate"/> guards only the
/// in-memory allocator state (<c>_slots</c>, <c>_writeTail</c>); <see cref="_ioGate"/> guards every use of the
/// connection. With a single lock the fast path would block behind an <c>INSERT</c> — or behind the retention
/// prune's whole-table <c>DELETE</c> — on a file shared with three other connections at
/// <c>busy_timeout=3000</c>, i.e. exactly the message-pump stall the design claims to avoid. Lock ordering is
/// one-way: <see cref="_ioGate"/> may be taken while holding <see cref="_gate"/> (the first-touch seed query,
/// the only nesting), never the reverse.
/// </para>
/// <para>
/// <b>Two bounds.</b> A hard per-run cap of <see cref="MaxEventsPerRun"/> real rows plus one synthetic
/// <see cref="AgentTimelineEventKind.TraceTruncated"/> row, and a retention prune on each row's own
/// <c>CreatedAt</c> driven by the chat-history cutoff. Neither is inherited: nothing in the codebase prunes
/// the run tables, and the one eviction path that reaches them exempts precisely the <c>Planned</c>-run chats
/// a timeline is for.
/// </para>
/// <para>
/// <b>Metadata only</b> — see <see cref="AgentTimelineEvent"/>. No column can hold an argument, a result or a
/// path, and the log lines here carry ids, counts, a tool name and an exception TYPE, nothing else.
/// </para>
/// </summary>
public sealed class AgentTimelineService : IAgentTimelineService, IDisposable
{
    /// <summary>Real rows retained per run. The 501st row is the synthetic truncation marker.</summary>
    public const int MaxEventsPerRun = 500;

    private readonly string _connectionString;
    private readonly ILogger<AgentTimelineService> _logger;

    /// <summary>Guards the in-memory allocator state only: <c>_slots</c> and <c>_writeTail</c>. Held for a few
    /// instructions on the caller's thread — which for the interactive gate is the UI thread.</summary>
    private readonly object _gate = new();

    /// <summary>Guards every use of <c>_connection</c>. Never taken before <see cref="_gate"/>.</summary>
    private readonly object _ioGate = new();

    private readonly Dictionary<Guid, RunSlot> _slots = [];
    private SqliteConnection? _connection;
    private Task _writeTail = Task.CompletedTask;

    /// <summary>Volatile so the fast path can check it without serializing against the writer.</summary>
    private volatile bool _disposed;

    /// <summary>Per-run emit state. Seeded from the DB on first touch, which is a CORRECTNESS case and not an
    /// optimization: a run parked in one process and resumed in another must continue its <c>Seq</c>.</summary>
    private sealed class RunSlot
    {
        public long NextSeq;
        public int Count;
        public bool CapNoted;
    }

    public AgentTimelineService(SqliteContext context, ILogger<AgentTimelineService> logger)
    {
        _connectionString = context.ConnectionString;
        _logger = logger;

        // Force the shared context to open + run EnsureSchema (which creates AgentTimelineEvents) BEFORE our
        // dedicated connection ever opens — same reason AgentRunService does it at composition time.
        context.GetConnection();
    }

    /// <summary>Must be called while holding <see cref="_ioGate"/>.</summary>
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

    public void Emit(AgentTimelineEvent e)
    {
        try
        {
            lock (_gate)
            {
                if (_disposed) return;

                var slot = SeedSlotLocked(e.RunId);
                if (slot.CapNoted) return; // the cap already fired for this run; drop silently

                AgentTimelineEvent row;
                if (slot.Count >= MaxEventsPerRun)
                {
                    slot.CapNoted = true;
                    row = e with
                    {
                        Id = Guid.NewGuid(),
                        Seq = ++slot.NextSeq,
                        StepId = null,
                        Kind = AgentTimelineEventKind.TraceTruncated,
                        Surface = ToolGateSurface.Unknown,
                        Decision = ToolGateDecision.Unknown,
                        Outcome = AgentTimelineOutcome.Unknown,
                        ToolName = string.Empty,
                        ToolClass = ToolClass.Unknown,
                        PluginId = null,
                        ArgsChars = null,
                        ResultChars = null,
                        DurationMs = null,
                        CreatedAt = DateTime.UtcNow,
                    };
                    _logger.LogInformation(
                        "Timeline cap reached for run {RunId} after {Max} events; later events are dropped",
                        e.RunId, MaxEventsPerRun);
                }
                else
                {
                    row = e with { Seq = ++slot.NextSeq };
                }

                slot.Count++;

                // Chain the write UNDER the same lock that allocated Seq, so DrainAsync's tail really is a
                // barrier: an unchained Task.Run would make the barrier a lie and every test that emits then
                // observes intermittent.
                _writeTail = _writeTail.ContinueWith(
                    _ => WriteRow(row),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default);
            }
        }
        catch (Exception ex)
        {
            // Failure isolation: emitting an audit event can never fail a step.
            _logger.LogWarning(ex, "Timeline emit failed for run {RunId} tool {ToolName}", e.RunId, e.ToolName);
        }
    }

    /// <summary>
    /// The serial writer's body. Each row is independently failure-isolated so one bad INSERT cannot break the
    /// chain for the rest of the run.
    /// </summary>
    private void WriteRow(AgentTimelineEvent row)
    {
        if (_disposed) return;

        try
        {
            lock (_ioGate)
            {
                if (_disposed) return;

                using var cmd = Connection().CreateCommand();
                cmd.CommandText = """
                    INSERT INTO AgentTimelineEvents
                        (Id, SchemaVersion, RunId, StepId, Seq, Kind, Surface, Decision, Outcome,
                         ToolName, ToolClass, PluginId, ArgsChars, ResultChars, DurationMs, CreatedAt)
                    VALUES
                        (@Id, @SchemaVersion, @RunId, @StepId, @Seq, @Kind, @Surface, @Decision, @Outcome,
                         @ToolName, @ToolClass, @PluginId, @ArgsChars, @ResultChars, @DurationMs, @CreatedAt);
                    """;
                cmd.Parameters.AddWithValue("@Id", row.Id.ToString());
                cmd.Parameters.AddWithValue("@SchemaVersion", row.SchemaVersion);
                cmd.Parameters.AddWithValue("@RunId", row.RunId.ToString());
                cmd.Parameters.AddWithValue("@StepId", (object?)row.StepId?.ToString() ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Seq", row.Seq);
                cmd.Parameters.AddWithValue("@Kind", (int)row.Kind);
                cmd.Parameters.AddWithValue("@Surface", (int)row.Surface);
                cmd.Parameters.AddWithValue("@Decision", (int)row.Decision);
                cmd.Parameters.AddWithValue("@Outcome", (int)row.Outcome);
                cmd.Parameters.AddWithValue("@ToolName", row.ToolName);
                cmd.Parameters.AddWithValue("@ToolClass", (int)row.ToolClass);
                cmd.Parameters.AddWithValue("@PluginId", (object?)row.PluginId?.ToString() ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ArgsChars", (object?)row.ArgsChars ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ResultChars", (object?)row.ResultChars ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@DurationMs", (object?)row.DurationMs ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@CreatedAt", row.CreatedAt.ToString("O"));
                cmd.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            // Exception TYPE and ids only — never a payload, and there is none in this row anyway.
            _logger.LogWarning(ex, "Timeline write failed for run {RunId} seq {Seq}", row.RunId, row.Seq);
        }
    }

    /// <summary>
    /// First touch of a run seeds <c>NextSeq</c>/<c>Count</c> from the table itself, so a resume in a new
    /// process continues the sequence instead of restarting it (and re-applies the cap instead of appending a
    /// second truncation marker).
    /// <para>
    /// This is the ONE place that touches the connection while holding <see cref="_gate"/>, and it is the
    /// allowed nesting direction. It costs one indexed aggregate per RUN, not per event — the steady-state
    /// emit never reaches <see cref="_ioGate"/> at all, which is the whole point of the split.
    /// </para>
    /// </summary>
    private RunSlot SeedSlotLocked(Guid runId)
    {
        if (_slots.TryGetValue(runId, out var existing))
            return existing;

        var slot = new RunSlot();
        try
        {
            lock (_ioGate)
            {
                using var cmd = Connection().CreateCommand();
                cmd.CommandText =
                    "SELECT COALESCE(MAX(Seq), 0), COUNT(*) FROM AgentTimelineEvents WHERE RunId = @RunId;";
                cmd.Parameters.AddWithValue("@RunId", runId.ToString());
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    slot.NextSeq = reader.GetInt64(0);
                    slot.Count = reader.GetInt32(1);
                }
            }
        }
        catch (Exception ex)
        {
            // A seeding fault must not fail the step either: an in-memory-only sequence still orders THIS
            // segment correctly, which is strictly better than refusing to record.
            _logger.LogWarning(ex, "Timeline seq seeding failed for run {RunId}", runId);
        }

        // A row count above the cap can only mean the marker is already in the table (rows come from here
        // alone), so do not append a second one.
        slot.CapNoted = slot.Count > MaxEventsPerRun;
        _slots[runId] = slot;
        return slot;
    }

    public async Task<IReadOnlyList<AgentTimelineEvent>> GetForRunAsync(Guid runId, CancellationToken ct = default)
    {
        // Drain first: the writer is asynchronous, so a read that skipped the barrier would report a
        // partially-written trace as the whole trace.
        await DrainAsync().ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        lock (_ioGate)
        {
            if (_disposed) return [];

            var rows = new List<AgentTimelineEvent>();
            try
            {
                using var cmd = Connection().CreateCommand();
                cmd.CommandText = """
                    SELECT Id, SchemaVersion, RunId, StepId, Seq, Kind, Surface, Decision, Outcome,
                           ToolName, ToolClass, PluginId, ArgsChars, ResultChars, DurationMs, CreatedAt
                    FROM AgentTimelineEvents
                    WHERE RunId = @RunId
                    ORDER BY Seq;
                    """;
                cmd.Parameters.AddWithValue("@RunId", runId.ToString());
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    rows.Add(Map(reader));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Timeline read failed for run {RunId}", runId);
                return [];
            }

            return rows;
        }
    }

    public async Task<int> PruneOlderThanAsync(DateTime cutoff, CancellationToken ct = default)
    {
        await DrainAsync().ConfigureAwait(false);

        int deleted;
        // Under _ioGate only — a whole-table range DELETE must not hold the lock a UI-thread Emit takes.
        lock (_ioGate)
        {
            if (_disposed) return 0;

            try
            {
                using var cmd = Connection().CreateCommand();
                cmd.CommandText = "DELETE FROM AgentTimelineEvents WHERE CreatedAt < @Cutoff;";
                cmd.Parameters.AddWithValue("@Cutoff", cutoff.ToString("O"));
                deleted = cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Timeline prune failed");
                return 0;
            }
        }

        if (deleted > 0)
        {
            // Every in-memory slot's Count is now a lie; drop them so the next emit re-seeds. NextSeq is
            // re-derived from MAX(Seq), so a pruned run's ordering still never collides. OUTSIDE _ioGate,
            // because taking _gate while holding it would be the forbidden direction.
            lock (_gate)
                _slots.Clear();

            _logger.LogInformation("Timeline retention deleted {Count} events older than the cutoff", deleted);
        }

        return deleted;
    }

    /// <summary>
    /// Test/diagnostic barrier: the serial writer's tail. Awaiting it guarantees every row emitted so far has
    /// been attempted. Exposed because <c>Emit</c> is fire-and-forget — a <c>Task.Delay</c>-based test would be
    /// a wall-clock flake, and this batch's tests emit then observe roughly fifteen times.
    /// </summary>
    internal Task DrainAsync()
    {
        lock (_gate)
        {
            return _writeTail;
        }
    }

    private static AgentTimelineEvent Map(SqliteDataReader r) => new(
        Id: Guid.Parse(r.GetString(0)),
        RunId: Guid.Parse(r.GetString(2)),
        StepId: r.IsDBNull(3) ? null : Guid.Parse(r.GetString(3)),
        Seq: r.GetInt64(4),
        // Unknown ordinals are NOT coerced here: the enums are append-only and a value this build does not
        // know must survive the read and render as "unknown" rather than be rewritten to 0 on the way out.
        Kind: (AgentTimelineEventKind)r.GetInt32(5),
        Surface: (ToolGateSurface)r.GetInt32(6),
        Decision: (ToolGateDecision)r.GetInt32(7),
        Outcome: (AgentTimelineOutcome)r.GetInt32(8),
        ToolName: r.GetString(9),
        ToolClass: (ToolClass)r.GetInt32(10),
        PluginId: r.IsDBNull(11) ? null : Guid.Parse(r.GetString(11)),
        ArgsChars: r.IsDBNull(12) ? null : r.GetInt32(12),
        ResultChars: r.IsDBNull(13) ? null : r.GetInt32(13),
        DurationMs: r.IsDBNull(14) ? null : r.GetInt64(14),
        CreatedAt: DateTime.Parse(r.GetString(15), null, System.Globalization.DateTimeStyles.RoundtripKind))
    {
        SchemaVersion = r.GetInt32(1),
    };

    public void Dispose()
    {
        Task tail;
        lock (_gate)
        {
            if (_disposed) return;
            tail = _writeTail;
        }

        // Let queued rows land before the connection closes. Bounded so a stuck writer cannot hang shutdown.
        // Awaited while holding NO lock, so the writer can still take _ioGate to finish.
        try { tail.Wait(TimeSpan.FromSeconds(2)); }
        catch (Exception ex) { _logger.LogWarning(ex, "Timeline writer did not drain cleanly on dispose"); }

        _disposed = true;
        lock (_ioGate)
        {
            _connection?.Dispose();
            _connection = null;
        }
    }
}
