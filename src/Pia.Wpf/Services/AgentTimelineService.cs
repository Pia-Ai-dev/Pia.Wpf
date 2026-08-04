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
/// <b>TWO locks.</b> <see cref="_gate"/> guards only the
/// in-memory allocator state (<c>_slots</c>, <c>_writeTail</c>); <see cref="_ioGate"/> guards every use of the
/// connection. With a single lock EVERY emit would block behind an <c>INSERT</c> — or behind the retention
/// prune's whole-table <c>DELETE</c> — on a file shared with three other connections at
/// <c>busy_timeout=3000</c>. Lock ordering is
/// one-way: <see cref="_ioGate"/> may be taken while holding <see cref="_gate"/> (the first-touch seed query,
/// the only nesting), never the reverse.
/// </para>
/// <para>
/// <b>What the split does NOT do, stated rather than implied.</b> It makes the STEADY-STATE emit free of I/O
/// locks; it does not make the fast path lock-free at FIRST TOUCH. <c>SeedSlotLocked</c> takes
/// <see cref="_ioGate"/> — and on the very first use opens the connection and runs a <c>PRAGMA</c> —
/// synchronously on the CALLER's thread, which for the interactive gate is the UI thread. The bound is one
/// indexed aggregate per RUN (plus one retry per emit while a seed keeps failing), and the worst case is
/// waiting out whatever the writer or the prune currently holds. Moving the seed onto the writer thread would
/// mean allocating <c>Seq</c> before knowing where a parked segment stopped — the exact correctness property
/// the seed exists for — so the trade is declined, not hidden.
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
/// <para>
/// <b>THREE chains, not two</b> (T2-G1). Alongside <c>_writeTail</c> (the SQLite writer) there is
/// <see cref="_observerTail"/>, a second serial chain that notifies <see cref="IRunObserver"/>s. Both are
/// ENQUEUED under <see cref="_gate"/> in the same critical section that allocated <c>Seq</c>, which is what
/// gives observers exactly the table's order; both EXECUTE off it. They are kept separate on purpose: chaining
/// notifications onto <c>_writeTail</c> would let one blocking observer stall the audit INSERTs, hang
/// <c>DrainAsync</c> and trip <c>Dispose</c>'s 2 s bound — the audit trail held hostage by a bystander.
/// <c>Dispose</c> therefore waits on <c>_writeTail</c> ONLY.
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

    /// <summary>
    /// The registered bystanders, materialized once. An ARRAY rather than the injected
    /// <c>IEnumerable&lt;&gt;</c> so the notify path cannot re-enumerate a lazy sequence (MS.DI's
    /// <c>IEnumerable&lt;T&gt;</c> is already an array, but a test or a future decorator's need not be) and so
    /// the zero-observer check is a field read.
    /// </summary>
    private readonly IRunObserver[] _observers;

    /// <summary>
    /// The observer notification chain. Guarded by <see cref="_gate"/> exactly as <c>_writeTail</c> is, and
    /// deliberately NOT the same chain — see the class remarks. Never awaited by <c>Dispose</c>: shutdown must
    /// not be hostage to a stuck observer, whose documented consequence is that a queued notification may run
    /// after <c>Dispose</c> returns. That is harmless because notification touches no connection and no slot.
    /// </summary>
    private Task _observerTail = Task.CompletedTask;

    /// <summary>Count of notifications ENQUEUED (one per accepted event, not one per observer). Guarded by
    /// <see cref="_gate"/>. Exists for the tests, which would otherwise have to prove a negative with a
    /// <c>Task.Delay</c>.</summary>
    private long _notifyDispatches;

    /// <summary>
    /// Set while an observer callback is on the stack, so an observer that calls back into <see cref="Emit"/>
    /// gets its row written and capped as usual but produces NO further notification. What this prevents is an
    /// unbounded notification chain, NOT a deadlock: notification runs holding no lock, and a re-entrant
    /// <c>ContinueWith</c> would merely queue behind the callback that scheduled it.
    /// <para>
    /// <c>AsyncLocal</c> rather than <c>[ThreadStatic]</c> because an observer is free to <c>await</c>
    /// internally, which moves it to another thread; the flag has to follow the logical call, and the
    /// continuation's <c>ExecutionContext</c> is restored on exit so the value cannot leak to the pool thread's
    /// next work item.
    /// </para>
    /// </summary>
    private static readonly AsyncLocal<bool> _inNotify = new();

    /// <summary>Volatile so the fast path can check it without serializing against the writer.</summary>
    private volatile bool _disposed;

    /// <summary>Per-run emit state. Seeded from the DB on first touch, which is a CORRECTNESS case and not an
    /// optimization: a run parked in one process and resumed in another must continue its <c>Seq</c>.</summary>
    private sealed class RunSlot
    {
        public long NextSeq;
        public int Count;
        public bool CapNoted;

        /// <summary>
        /// <c>StepOrdinal</c>'s allocator: the last value handed out per STEP, keyed by <c>StepId</c>.
        /// Incremented inside the SAME <c>lock (_gate)</c> critical section as <see cref="NextSeq"/> — that
        /// shared lock is the whole reason a step's ordinals are monotonic and gap-free.
        /// <para>
        /// Rows with a NULL <c>StepId</c> get NO entry here and NO ordinal: a shared null-bucket counter would
        /// invent a step that does not exist, and <c>Seq</c> already orders run-level rows. Lives inside the
        /// slot so <c>PruneOlderThanAsync</c>'s <c>_slots.Clear()</c> drops it with everything else and the
        /// re-seed reconstructs it from <c>MAX(StepOrdinal)</c> — a per-step dictionary hanging off the
        /// SERVICE would survive that clear holding counts the table no longer backs.
        /// </para>
        /// </summary>
        public readonly Dictionary<Guid, long> StepSeq = [];

        /// <summary>The seed query threw. The slot is still cached (so the segment keeps a usable sequence) but
        /// it is NOT trusted: the next emit re-attempts the aggregate.</summary>
        public bool SeedFailed;
    }

    /// <param name="observers">The telemetry bystanders (T2-G1). OPTIONAL with an empty default: zero
    /// registrations is the normal state, MS.DI injects an empty sequence for it, and leaving the parameter
    /// defaulted keeps every direct <c>new AgentTimelineService(ctx, logger)</c> in the suites compiling.
    /// Never a "default no-op observer" — the whole point is that no observers costs no work.</param>
    public AgentTimelineService(
        SqliteContext context,
        ILogger<AgentTimelineService> logger,
        IEnumerable<IRunObserver>? observers = null)
    {
        _connectionString = context.ConnectionString;
        _logger = logger;
        _observers = observers?.ToArray() ?? [];

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
                        // The marker is NOT a tool call, so it inherits none of the correlation fields the
                        // capped event happened to carry. Spelled out because the compiler cannot catch a
                        // `with` block that silently keeps a field: a marker claiming round 4 and the last
                        // real call's CallId would read as a gated call that never happened. StepOrdinal is
                        // nulled alongside StepId for the same reason, and no per-step counter is touched.
                        ToolCallId = null,
                        Round = null,
                        StepOrdinal = null,
                        RequestedAt = null,
                        DecidedAt = null,
                    };
                    _logger.LogInformation(
                        "Timeline cap reached for run {RunId} after {Max} events; later events are dropped",
                        e.RunId, MaxEventsPerRun);
                }
                else
                {
                    // Seq (per RUN) and StepOrdinal (per STEP) are allocated together, under the one lock, so
                    // a step's ordinals cannot interleave or gap. A run-level row (StepId null) takes Seq
                    // only — see RunSlot.StepSeq for why there is no null bucket.
                    long? stepOrdinal = null;
                    if (e.StepId is { } stepId)
                    {
                        slot.StepSeq.TryGetValue(stepId, out var lastForStep);
                        stepOrdinal = lastForStep + 1;
                        slot.StepSeq[stepId] = stepOrdinal.Value;
                    }

                    row = e with { Seq = ++slot.NextSeq, StepOrdinal = stepOrdinal };
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

                // T2-G1: the bystanders, ENQUEUED here — under the same lock, in the same order the table gets
                // — and EXECUTED on their own chain. With no observers registered this is a field read and
                // nothing else: no delegate, no Task, no ContinueWith. Notifying `row` and not `e` matters:
                // `row` is what the INSERT above carries, service-assigned Seq and StepOrdinal included.
                if (_observers.Length > 0 && !_inNotify.Value)
                {
                    _notifyDispatches++;
                    _observerTail = _observerTail.ContinueWith(
                        _ => Notify(row),
                        CancellationToken.None,
                        TaskContinuationOptions.None,
                        TaskScheduler.Default);
                }
            }
        }
        catch (Exception ex)
        {
            // Failure isolation: emitting an audit event can never fail a step.
            _logger.LogWarning(ex, "Timeline emit failed for run {RunId} tool {ToolName}", e.RunId, e.ToolName);
        }
    }

    /// <summary>
    /// The observer chain's body (T2-G1). Runs on a pool thread holding NEITHER lock, so an observer may call
    /// back into <see cref="Emit"/> without deadlocking.
    /// <para>
    /// Each callback is individually try/caught: one throwing observer must not cost the row (already queued
    /// on the other chain), the run, or the NEXT observer. The log line carries the observer's type, the run id
    /// and the seq — plus the exception type via <c>ILogger</c> — and nothing from the event, because the row
    /// being metadata is not a licence to start logging its contents.
    /// </para>
    /// </summary>
    private void Notify(AgentTimelineEvent row)
    {
        _inNotify.Value = true;
        try
        {
            foreach (var observer in _observers)
            {
                try
                {
                    observer.OnTimelineEvent(row);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Timeline observer {Observer} threw for run {RunId} seq {Seq}",
                        observer.GetType().Name, row.RunId, row.Seq);
                }
            }
        }
        finally
        {
            _inNotify.Value = false;
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
                         ToolName, ToolClass, PluginId, ArgsChars, ResultChars, DurationMs, CreatedAt,
                         ToolCallId, Round, StepOrdinal, RequestedAt, DecidedAt)
                    VALUES
                        (@Id, @SchemaVersion, @RunId, @StepId, @Seq, @Kind, @Surface, @Decision, @Outcome,
                         @ToolName, @ToolClass, @PluginId, @ArgsChars, @ResultChars, @DurationMs, @CreatedAt,
                         @ToolCallId, @Round, @StepOrdinal, @RequestedAt, @DecidedAt);
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
                cmd.Parameters.AddWithValue("@ToolCallId", (object?)row.ToolCallId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Round", (object?)row.Round ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@StepOrdinal", (object?)row.StepOrdinal ?? DBNull.Value);
                // "O" round-trip on write + DateTimeStyles.RoundtripKind on read, exactly as CreatedAt does,
                // so the three instants on a row are comparable without a format caveat.
                cmd.Parameters.AddWithValue("@RequestedAt", (object?)row.RequestedAt?.ToString("O") ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@DecidedAt", (object?)row.DecidedAt?.ToString("O") ?? DBNull.Value);
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
        if (_slots.TryGetValue(runId, out var existing) && !existing.SeedFailed)
            return existing;

        // Reuse the slot on a RETRY: it may already have handed out Seq values from its in-memory-only
        // sequence, and those cannot be walked back.
        var slot = existing ?? new RunSlot();
        try
        {
            lock (_ioGate)
            {
                using var cmd = Connection().CreateCommand();
                // GROUPED BY StepId so ONE aggregate seeds the run value AND every per-step ordinal. Still one
                // indexed scan per RUN — IX_AgentTimelineEvents_RunId(RunId, Seq) — not one query per step:
                // grouping turns a single row into a handful, it does not add I/O.
                cmd.CommandText = """
                    SELECT StepId, COALESCE(MAX(Seq), 0), COALESCE(MAX(StepOrdinal), 0), COUNT(*)
                    FROM AgentTimelineEvents
                    WHERE RunId = @RunId
                    GROUP BY StepId;
                    """;
                cmd.Parameters.AddWithValue("@RunId", runId.ToString());
                using var reader = cmd.ExecuteReader();

                // The run's values are folded ACROSS the groups: MAX for Seq (the largest of the per-group
                // maxima is the run's maximum) but SUM for Count — Math.Max per group would seed the run's
                // count with its biggest STEP, and a run at 500 rows spread over five steps would then keep
                // emitting past the cap.
                long maxSeq = 0;
                var totalRows = 0;
                while (reader.Read())
                {
                    maxSeq = Math.Max(maxSeq, reader.GetInt64(1));
                    totalRows += reader.GetInt32(3);

                    // The NULL-StepId group (run-level turns, truncation markers) contributes to the run
                    // values above and gets no StepSeq entry: those rows carry no ordinal to continue.
                    if (reader.IsDBNull(0)) continue;

                    var stepId = Guid.Parse(reader.GetString(0));
                    var persisted = reader.GetInt64(2);
                    // Math.Max per STEP for exactly the reason the run value uses it: on a retry this slot may
                    // already have handed out ordinals from its in-memory sequence, and rows carrying them may
                    // not have been written yet. The reconciled value must never move a step BACKWARDS.
                    slot.StepSeq[stepId] = slot.StepSeq.TryGetValue(stepId, out var inMemory)
                        ? Math.Max(inMemory, persisted)
                        : persisted;
                }

                // Math.Max, not assignment, for the retry case above: the reconciled value must never move
                // the sequence BACKWARDS over rows this segment already emitted.
                slot.NextSeq = Math.Max(slot.NextSeq, maxSeq);
                slot.Count = Math.Max(slot.Count, totalRows);

                slot.SeedFailed = false;
            }
        }
        catch (Exception ex)
        {
            // A seeding fault must not fail the step. But it must not be cached AS A SUCCESSFUL SEED either:
            // a run parked at 40 rows and resumed after a failed seed would restart Seq at 1, duplicating the
            // parked segment's values (ORDER BY Seq then interleaves the two segments by rowid tie-break) and
            // resetting a per-run cap it had already reached. Flagged, so the next emit re-attempts it.
            slot.SeedFailed = true;
            _logger.LogWarning(ex, "Timeline seq seeding failed for run {RunId}; retrying on the next emit", runId);
        }

        // A row count above the cap can only mean the marker is already in the table (rows come from here
        // alone), so do not append a second one. ORed, never re-derived: a retry must not clear a cap that
        // already fired against the in-memory count this session.
        slot.CapNoted = slot.CapNoted || slot.Count > MaxEventsPerRun;
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
                           ToolName, ToolClass, PluginId, ArgsChars, ResultChars, DurationMs, CreatedAt,
                           ToolCallId, Round, StepOrdinal, RequestedAt, DecidedAt
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

    /// <summary>
    /// The same barrier for the OBSERVER chain (T2-G1), mirroring <see cref="DrainAsync"/> so no observer test
    /// needs a <c>Task.Delay</c>. Separate from <see cref="DrainAsync"/> for the same reason the chains are
    /// separate: a test that wants "the row landed" must not be made to wait on a bystander.
    /// </summary>
    internal Task ObserverDrainAsync()
    {
        lock (_gate)
        {
            return _observerTail;
        }
    }

    /// <summary>
    /// How many notifications have been ENQUEUED — one per accepted event, regardless of observer count, and
    /// zero when none are registered. The observable half of "no observers costs nothing": a test can assert
    /// this stayed at 0 while the rows still landed, which no timing-based check could.
    /// </summary>
    internal long NotifyDispatches
    {
        get { lock (_gate) { return _notifyDispatches; } }
    }

    /// <summary>
    /// The "O" round-trip format read back with its matching style, shared by <see cref="Map"/>'s three instant
    /// columns (<c>CreatedAt</c>, <c>RequestedAt</c>, <c>DecidedAt</c>) so all three parse identically instead
    /// of three inlined copies of the same two arguments.
    /// </summary>
    private static DateTime ParseTimestamp(string value) =>
        DateTime.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind);

    /// <summary>
    /// Reader indexes are hardcoded POSITIONS in <c>GetForRunAsync</c>'s SELECT list, so the five T2-14
    /// columns were appended at its END (16..20): inserting one mid-list would silently shift every later
    /// read and mis-type the row. Keep the two lists in the same order.
    /// </summary>
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
        CreatedAt: ParseTimestamp(r.GetString(15)),
        ToolCallId: r.IsDBNull(16) ? null : r.GetString(16),
        Round: r.IsDBNull(17) ? null : r.GetInt32(17),
        StepOrdinal: r.IsDBNull(18) ? null : r.GetInt64(18),
        RequestedAt: r.IsDBNull(19) ? null : ParseTimestamp(r.GetString(19)),
        DecidedAt: r.IsDBNull(20) ? null : ParseTimestamp(r.GetString(20)))
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
