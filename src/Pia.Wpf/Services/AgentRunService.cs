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
/// <c>SensitiveDebug</c>, never at Information (CLAUDE.md / §12.7). <c>PolicyJson</c> is an opaque
/// launch envelope: stored/returned verbatim, never parsed here and never logged beyond its presence.
/// </para>
/// <para>
/// The ledger's <c>wallClockMs</c> is the run's accumulated ACTIVE time (segments opened at
/// create/resume, closed at pause/terminal) — NOT <c>UtcNow - StartedAt</c>, which would bill the time
/// a run sat parked. The ENFORCED budget clock is a separate fresh <c>Stopwatch</c> per
/// <see cref="RunContext"/>; the two are intentionally different clocks.
/// </para>
/// </summary>
public sealed class AgentRunService : IAgentRunService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// The pause <c>reason</c> written when the startup reconcile re-parks a parent that was awaiting
    /// children (07 D14). An app-owned token from the same fixed vocabulary as <c>"step-cap"</c> /
    /// <c>"wall-clock"</c> / <c>"children-parked"</c> — never user content, and read by
    /// <c>RunProgressViewModel.DescribeTruncation</c>-style mappings, which is why it is a named constant.
    /// </summary>
    internal const string ChildrenInterruptedReason = "children-interrupted";

    /// <summary>
    /// The pause <c>reason</c> written by <see cref="TryPauseUserAsync"/> — a run the USER paused from the run
    /// panel (Batch 08 D1), as opposed to the loop parking itself at a budget. Same closed, app-owned
    /// vocabulary as <c>"step-cap"</c> / <c>"wall-clock"</c> / <c>"children-parked"</c> /
    /// <see cref="ChildrenInterruptedReason"/>: never user content, so it may be logged and may key copy.
    /// <para>
    /// Adding a token to that vocabulary obliges an arm in BOTH readers, or a user-paused run announces
    /// itself as "Stopped at budget": <c>RunProgressViewModel.DescribePause</c> and
    /// <see cref="AgentRunNotificationSurface.PausedBodyKey"/>. Both default to the budget wording on purpose.
    /// </para>
    /// </summary>
    internal const string UserPausedReason = "user";

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
            // The delegation link (07 D10). This assignment is what makes the IN-MEMORY run correct, and the
            // in-memory object is the one a fresh launch hands to AgentRunOrchestrator.RunAsync — the row is
            // never re-read first. The INSERT below and MapRun have always carried the column; only this line
            // was missing, and missing it fails SILENTLY: every child would read ParentRunId == null and so
            // would every guard that asks "am I a child?".
            ParentRunId = request.ParentRunId,
            OwnerDeviceId = request.OwnerDeviceId,
            Goal = request.Goal,
            // Opaque launch envelope — stored verbatim, never parsed here, never logged (D1).
            PolicyJson = request.PolicyJson,
            // The run starts working now → open the ledger's first work segment (G1). ActiveMs is set
            // explicitly (not left default) so this ledger is never mistaken for a legacy one.
            LedgerJson = JsonSerializer.Serialize(new Ledger { ActiveMs = 0, SegmentStartedAt = now }, JsonOptions),
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
            cmd.Parameters.AddWithValue("@PolicyJson", ToParam(run.PolicyJson));
            cmd.Parameters.AddWithValue("@LedgerJson", ToParam(run.LedgerJson));
            cmd.Parameters.AddWithValue("@CreatedAt", run.CreatedAt.ToString("O"));
            cmd.Parameters.AddWithValue("@UpdatedAt", run.UpdatedAt.ToString("O"));
            cmd.Parameters.AddWithValue("@StartedAt", run.StartedAt!.Value.ToString("O"));
            cmd.Parameters.AddWithValue("@CompletedAt", DBNull.Value);
            cmd.Parameters.AddWithValue("@ExtraJson", DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        // policy= is PRESENCE only: the envelope may name granted capabilities → never log its content.
        // parent= is a BOOLEAN for the same reason the policy flag is: a run id would be safe to log, but this
        // line answers "is this a delegated run?" and a stable format is worth more than the id, which every
        // other run-scoped line already carries.
        _logger.LogInformation(
            "Created run {RunId} shape={Shape} state={State} trigger={Trigger} policy={HasPolicy} parent={HasParent}",
            run.Id, run.RunShape, run.State, run.TriggerKind, run.PolicyJson is not null, run.ParentRunId is not null);
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
            ApplyLedgerClock(ledger, startedAt, state, LedgerClock.Refresh);

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

            // Close the open work segment so the reported wall clock freezes here (G1).
            MoveLedgerClock(runId, LedgerClock.CloseSegment);

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

            MoveLedgerClock(runId, LedgerClock.CloseSegment);

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

            // Close the work segment before parking (mirrors CompleteAsync): the parked gap that starts
            // here must NOT count as worked time, and the next resume opens a fresh segment (G1).
            MoveLedgerClock(runId, LedgerClock.CloseSegment);

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
            // Clear the {paused:true} marker on the claim so a cleanly-completing resumed run (whose
            // non-truncated CompleteAsync leaves ExtraJson untouched) does not retain stale pause state.
            using var cmd = Connection().CreateCommand();
            cmd.CommandText = "UPDATE AgentRuns SET State=@New, UpdatedAt=@Now, ExtraJson=NULL WHERE Id=@Id AND State=@Expected";
            cmd.Parameters.AddWithValue("@New", (int)AgentRunState.Running);
            cmd.Parameters.AddWithValue("@Now", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@Id", runId.ToString());
            cmd.Parameters.AddWithValue("@Expected", (int)AgentRunState.WaitingForInput);
            affected = cmd.ExecuteNonQuery();

            // Open a fresh work segment ONLY for the CAS winner, in the same _gate hold so the loser
            // never re-opens a clock (G1). Separate statement on purpose: the CAS must stay one
            // self-contained UPDATE, and this is bookkeeping (MoveLedgerClock swallows its own faults).
            if (affected > 0)
                MoveLedgerClock(runId, LedgerClock.OpenSegment);
        }

        if (affected > 0)
        {
            _logger.LogInformation("Run {RunId} resume claimed → Running", runId);
            RunChanged?.Invoke(this, new AgentRunChangedEventArgs(runId, AgentRunState.Running));
        }
        return Task.FromResult(affected > 0);
    }

    public Task<bool> TryPauseUserAsync(Guid runId, CancellationToken ct = default)
    {
        int affected;
        lock (_gate)
        {
            if (_disposed) return Task.FromResult(false);

            // A CAS, never a blind write, for the reason TryEndChildWaitAsync is one: by the time a user
            // clicks Pause a SECOND writer can already want this run — its own loop settling, the
            // cascade-cancel path, a Stop — and a blind UPDATE would resurrect a run somebody else already
            // settled as Running-but-Paused (R11). A lost race writes NOTHING and says so in its return.
            //
            // The source set is EXPLICIT and never a range (D7): WaitingForChildren = 8 sits ABOVE the
            // terminal band, so any `State < x` predicate lies about it.
            //   Running            — the ordinary case, and also what a fan-out parent presents (the un-park
            //                        CAS has already moved it off WaitingForChildren by the time its caller
            //                        sees AnyParked).
            //   Verifying          — the critic's provider call is as interruptible as a step's.
            //   WaitingForChildren — a pause that lands before the un-park CAS.
            // Planning is DELIBERATELY excluded: a resume runs RunAsync(resume: true), which skips planning
            // entirely, so a run paused mid-plan would come back with NO plan, drain zero steps and settle
            // Completed having done nothing.
            //
            // NO CompletedAt — that is the whole difference between a pause and FailAsync, which stamps one
            // unconditionally. A pause must leave a RESUMABLE run, and a non-null CompletedAt says finished.
            using var cmd = Connection().CreateCommand();
            cmd.CommandText = "UPDATE AgentRuns SET State=@New, UpdatedAt=@Now, ExtraJson=@Extra WHERE Id=@Id AND State IN (@S1,@S2,@S3)";
            cmd.Parameters.AddWithValue("@New", (int)AgentRunState.Paused);
            cmd.Parameters.AddWithValue("@Now", DateTime.UtcNow.ToString("O"));
            // Serialized through the SAME shape + options PauseAsync and the startup re-park use, never
            // hand-written JSON: RunPauseEnvelope is the single reader and a naming-policy change must move
            // every writer together.
            cmd.Parameters.AddWithValue("@Extra",
                JsonSerializer.Serialize(new { paused = true, reason = UserPausedReason }, JsonOptions));
            cmd.Parameters.AddWithValue("@Id", runId.ToString());
            cmd.Parameters.AddWithValue("@S1", (int)AgentRunState.Running);
            cmd.Parameters.AddWithValue("@S2", (int)AgentRunState.Verifying);
            cmd.Parameters.AddWithValue("@S3", (int)AgentRunState.WaitingForChildren);
            affected = cmd.ExecuteNonQuery();

            // Close the work segment ONLY for the winner, in the same _gate hold (mirrors
            // TryBeginResumeAsync's OpenSegment): the pause gap must not count as worked time, and the loser
            // must never touch the clock of a run it does not own. Separate statement on purpose — the CAS
            // stays one self-contained UPDATE and this is bookkeeping (MoveLedgerClock swallows its faults).
            if (affected > 0)
                MoveLedgerClock(runId, LedgerClock.CloseSegment);
        }

        if (affected > 0)
        {
            // The reason token is app-owned, so it may be logged in full (unlike the run's Goal).
            _logger.LogInformation("Run {RunId} → Paused (reason={Reason})", runId, UserPausedReason);
            RunChanged?.Invoke(this, new AgentRunChangedEventArgs(runId, AgentRunState.Paused));
        }
        else
        {
            _logger.LogInformation("Run {RunId} user pause not applied — another writer owns this run", runId);
        }
        return Task.FromResult(affected > 0);
    }

    public Task<bool> TryResumeFromPauseAsync(Guid runId, CancellationToken ct = default)
    {
        int affected;
        lock (_gate)
        {
            if (_disposed) return Task.FromResult(false);

            // The SIBLING of TryBeginResumeAsync, deliberately a second single-source CAS rather than a
            // widened one: keeping `@Expected` a single state is what makes the two claims provably DISJOINT
            // (a WaitingForInput run is not claimable here and a Paused run is not claimable there), and it
            // is what lets the launcher dispatch on the row's state instead of "try one, then the other".
            //
            // ExtraJson=NULL is deliberate and is the same reasoning TryBeginResumeAsync gives: the claim
            // RETIRES the pause marker it just consumed, so a resumed run that completes cleanly (whose
            // non-truncated CompleteAsync leaves the column alone) does not keep telling the panel and the
            // Flow surface that it is paused.
            using var cmd = Connection().CreateCommand();
            cmd.CommandText = "UPDATE AgentRuns SET State=@New, UpdatedAt=@Now, ExtraJson=NULL WHERE Id=@Id AND State=@Expected";
            cmd.Parameters.AddWithValue("@New", (int)AgentRunState.Running);
            cmd.Parameters.AddWithValue("@Now", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@Id", runId.ToString());
            cmd.Parameters.AddWithValue("@Expected", (int)AgentRunState.Paused);
            affected = cmd.ExecuteNonQuery();

            // A FRESH work segment for the winner only (guardrail 2 — never two loops on one run).
            if (affected > 0)
                MoveLedgerClock(runId, LedgerClock.OpenSegment);
        }

        if (affected > 0)
        {
            _logger.LogInformation("Run {RunId} resume claimed from Paused → Running", runId);
            RunChanged?.Invoke(this, new AgentRunChangedEventArgs(runId, AgentRunState.Running));
        }
        return Task.FromResult(affected > 0);
    }

    public Task BeginChildWaitAsync(Guid runId, int childCount, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;

            // Close the work segment before parking (mirrors PauseAsync): the parent is not working while
            // its children are, and each child bills its own wall clock into its own ledger. The unpark CAS
            // re-opens a fresh segment, so a repeated fan-out accumulates worked time correctly (07 D15).
            MoveLedgerClock(runId, LedgerClock.CloseSegment);

            // BLIND, like SetStateAsync and for the same reason: the caller is the parent's own drain loop,
            // which has just dispatched these children itself and is the only writer at this instant. No
            // CompletedAt — this is not a completion; no ExtraJson — the child ROWS are the marker (§0.4).
            using var cmd = Connection().CreateCommand();
            cmd.CommandText = "UPDATE AgentRuns SET State=@State, UpdatedAt=@Now WHERE Id=@Id";
            cmd.Parameters.AddWithValue("@State", (int)AgentRunState.WaitingForChildren);
            cmd.Parameters.AddWithValue("@Now", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@Id", runId.ToString());
            cmd.ExecuteNonQuery();
        }

        // A COUNT, never a goal or a step title — those are user content (CLAUDE.md privacy logging).
        _logger.LogInformation("Run {RunId} → WaitingForChildren ({ChildCount} child run(s))", runId, childCount);
        RunChanged?.Invoke(this, new AgentRunChangedEventArgs(runId, AgentRunState.WaitingForChildren));
        return Task.CompletedTask;
    }

    public Task<bool> TryEndChildWaitAsync(Guid runId, CancellationToken ct = default)
    {
        int affected;
        lock (_gate)
        {
            if (_disposed) return Task.FromResult(false);

            // A CAS for the same reason TryBeginResumeAsync is one: by now a SECOND writer can want this
            // run — the cascade-cancel path (ChatSession.Cancel / chat delete / shutdown) and, across a
            // process death, FailInterruptedRunsAsync's re-park. SetStateAsync would happily flip a
            // Cancelled parent back to Running (R11). Deliberately NOT `ExtraJson=NULL`, unlike the resume
            // claim: this is not a user "continue" and there is no pause marker to retire.
            using var cmd = Connection().CreateCommand();
            cmd.CommandText = "UPDATE AgentRuns SET State=@New, UpdatedAt=@Now WHERE Id=@Id AND State=@Expected";
            cmd.Parameters.AddWithValue("@New", (int)AgentRunState.Running);
            cmd.Parameters.AddWithValue("@Now", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@Id", runId.ToString());
            cmd.Parameters.AddWithValue("@Expected", (int)AgentRunState.WaitingForChildren);
            affected = cmd.ExecuteNonQuery();

            // Re-open the work segment ONLY for the winner, inside the same _gate hold (mirrors
            // TryBeginResumeAsync): the loser must never re-open a clock on a run it does not own.
            if (affected > 0)
                MoveLedgerClock(runId, LedgerClock.OpenSegment);
        }

        if (affected > 0)
        {
            _logger.LogInformation("Run {RunId} child wait ended → Running", runId);
            RunChanged?.Invoke(this, new AgentRunChangedEventArgs(runId, AgentRunState.Running));
        }
        return Task.FromResult(affected > 0);
    }

    public Task<int> FailInterruptedRunsAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        int cancelled;
        int reparked;
        int redispatchable;
        lock (_gate)
        {
            if (_disposed) return Task.FromResult(0);

            var connection = Connection();

            // Statement 1. States 0..2 (Planning/Running/Verifying) are crash-recoverable — settle to
            // Cancelled. 3/4 (WaitingForInput/Paused) are a DELIBERATE parked state (budget pause) and MUST
            // survive restart resumable — never swept. 5-7 (Completed/Failed/Cancelled) are terminal.
            // 8 (WaitingForChildren) is NOT swept either: `8 < 3` is false, which is exactly why Batch 07
            // appended it ABOVE the terminal band rather than beside Running — a parent must not be
            // cancelled out from under its children's completed work. Statement 2 reconciles it instead.
            // No per-row RunChanged for either statement: these are not live transitions (the Flow surface
            // would otherwise re-publish stale items at startup).
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "UPDATE AgentRuns SET State=@State, CompletedAt=@Now, UpdatedAt=@Now WHERE State < @Terminal";
                cmd.Parameters.AddWithValue("@State", (int)AgentRunState.Cancelled);
                cmd.Parameters.AddWithValue("@Now", now.ToString("O"));
                cmd.Parameters.AddWithValue("@Terminal", (int)AgentRunState.WaitingForInput);
                cancelled = cmd.ExecuteNonQuery();
            }

            // Statement 1b. The crash path's half of the fan-out's Pending invariant. TryFanOutAsync sets every
            // DISPATCHED sibling step to Running the moment it hands the child off, and the only writers that
            // move a step off Running are the result recorder and the parked arm's explicit
            // SetStepStatus(sibling, Pending) — in-process code that cannot run if the process dies. The resume
            // drain is NextPendingStepAsync, whose predicate is `Status=Pending`, so a step left Running is
            // INVISIBLE to it: without this statement a re-parked parent would skip its whole delegated group,
            // execute the steps AFTER it out of order against inputs that were never produced, and settle
            // Completed while the panel still rendered those steps as active — permanently and silently.
            // Same invariant the in-process park establishes for itself, given to the path where no code runs.
            //
            // ORDER MATTERS twice over: after statement 1 (a cancelled CHILD's own Running steps are not ours
            // to reset — its row is terminal), and BEFORE statement 2, which changes the very state this
            // selects on.
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    UPDATE AgentSteps SET Status=@Pending, UpdatedAt=@Now
                    WHERE Status=@Running AND RunId IN (SELECT Id FROM AgentRuns WHERE State=@Waiting)
                    """;
                cmd.Parameters.AddWithValue("@Pending", (int)AgentStepStatus.Pending);
                cmd.Parameters.AddWithValue("@Running", (int)AgentStepStatus.Running);
                cmd.Parameters.AddWithValue("@Now", now.ToString("O"));
                cmd.Parameters.AddWithValue("@Waiting", (int)AgentRunState.WaitingForChildren);
                redispatchable = cmd.ExecuteNonQuery();
            }

            // Statement 2 (07 D14). A parent that was awaiting children when the process died: statement 1
            // has just Cancelled those children, so no completing child can ever wake it and it would sit
            // WaitingForChildren forever. Re-park it as WaitingForInput — the ONE state
            // TryBeginResumeAsync can claim — carrying the SAME {paused:true,reason} envelope PauseAsync
            // writes, so the panel's existing WaitingForInput projection and its Continue button bring it
            // back with no new resume vocabulary. Statement 1b has just put its delegated steps back to
            // Pending along with the rest of the remainder, so the resume drains them in ordinal order (D1)
            // and the fan-out group re-dispatches a fresh generation.
            //
            // NOT Cancelled: its earlier steps are Done and its children's finished work is durable, so
            // presenting that as a cancelled run throws away recoverable work. And NO CompletedAt, unlike
            // statement 1 — a re-parked run is not finished, and a non-null CompletedAt would say it was.
            //
            // ORDER MATTERS: statement 1 must run first. Re-parking first would leave the parent at
            // WaitingForInput, which statement 1 does not touch — harmless, but the children would then be
            // cancelled after the parent had already been declared resumable. Written in this order on
            // purpose.
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "UPDATE AgentRuns SET State=@State, UpdatedAt=@Now, ExtraJson=@Extra WHERE State=@Waiting";
                cmd.Parameters.AddWithValue("@State", (int)AgentRunState.WaitingForInput);
                cmd.Parameters.AddWithValue("@Now", now.ToString("O"));
                // Serialized through the same shape + options PauseAsync uses, never hand-written JSON: a
                // naming-policy change must move both together or the WaitingForInput projection breaks.
                cmd.Parameters.AddWithValue("@Extra",
                    JsonSerializer.Serialize(new { paused = true, reason = ChildrenInterruptedReason }, JsonOptions));
                cmd.Parameters.AddWithValue("@Waiting", (int)AgentRunState.WaitingForChildren);
                reparked = cmd.ExecuteNonQuery();
            }
        }

        // Two lines, not one: a support log must distinguish "cancelled" from "re-parked and resumable".
        // Counts only.
        if (cancelled > 0)
            _logger.LogInformation("Settled {Count} interrupted agent run(s) to Cancelled at startup", cancelled);
        if (reparked > 0)
            _logger.LogInformation(
                "Re-parked {Count} interrupted parent run(s) awaiting children at startup, with {StepCount} delegated step(s) back to Pending",
                reparked, redispatchable);
        return Task.FromResult(cancelled + reparked);
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

    public Task<IReadOnlyList<AgentRun>> GetChildRunsAsync(Guid parentRunId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_disposed) return Task.FromResult<IReadOnlyList<AgentRun>>(Array.Empty<AgentRun>());

            var runs = new List<AgentRun>();
            using var cmd = Connection().CreateCommand();
            // Indexed by IX_AgentRuns_ParentRunId (Batch 07 G9). No LoadSteps pass, unlike GetByChatAsync
            // above: both callers read state + ledger, and a 4-child roll-up does not need 4 plans.
            cmd.CommandText = $"SELECT {RunColumns} FROM AgentRuns WHERE ParentRunId=@Parent ORDER BY CreatedAt ASC";
            cmd.Parameters.AddWithValue("@Parent", parentRunId.ToString());
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                runs.Add(MapRun(reader));

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

            if (usage is not null && TryLoadRunLedger(runId, out var ledger, out var startedAt, out var ledgerState))
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
                ApplyLedgerClock(ledger, startedAt, ledgerState, LedgerClock.Refresh);
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

    /// <summary>What a ledger write does to the run's work-segment clock (G1).</summary>
    private enum LedgerClock
    {
        /// <summary>Recompute the reported total; do not open or close a segment.</summary>
        Refresh,

        /// <summary>The run starts working (create / resume claim).</summary>
        OpenSegment,

        /// <summary>The run stops working (pause / complete / fail).</summary>
        CloseSegment,
    }

    /// <summary>
    /// THE single place ledger wall-clock is computed (G1). <c>WallClockMs</c> is the reported total
    /// WORKED time — accumulated closed segments plus the open one — so a run parked overnight and
    /// resumed no longer bills the parked gap (the old <c>UtcNow - StartedAt</c> did, because
    /// <c>StartedAt</c> is written once at create and never advanced). Correct across repeated
    /// pause→resume→pause cycles: each cycle closes one segment into <see cref="Ledger.ActiveMs"/>.
    /// <para>
    /// Distinct from the ENFORCED budget clock, which is a fresh <c>Stopwatch</c> per
    /// <see cref="RunContext"/> (a resume deliberately grants a fresh wall-clock budget) — this one is
    /// the durable, cumulative accounting number.
    /// </para>
    /// </summary>
    private static void ApplyLedgerClock(Ledger ledger, DateTime? startedAt, AgentRunState state, LedgerClock action)
    {
        var now = DateTime.UtcNow;
        // Explicit, NOT `state >= Completed` (07 D8c): WaitingForChildren(8) is appended ABOVE the terminal
        // band — the startup sweep's `State < WaitingForInput` requires it — so an ordinal range would call
        // a parked parent terminal, freeze its ledger and drop its open segment. "Terminal" here means
        // "can never work again", which is a set, not a threshold.
        var terminal = state is AgentRunState.Completed or AgentRunState.Failed or AgentRunState.Cancelled;

        // Legacy ledger: written before active-time tracking, so it has NEITHER field. Seed the
        // accumulator once from its last reported total (falling back to StartedAt when it never
        // accrued), then accumulate normally. A terminal legacy run is frozen and returns untouched —
        // it can never work again, and re-deriving from StartedAt would inflate an archived run.
        if (ledger.ActiveMs is null && ledger.SegmentStartedAt is null)
        {
            if (terminal) return;
            ledger.ActiveMs = ledger.WallClockMs > 0 ? ledger.WallClockMs : ElapsedMs(startedAt);
        }

        var active = ledger.ActiveMs ?? 0;
        // An ALREADY-terminal run cannot be working, so a segment still open on it is stale (a crashed
        // run later swept to Cancelled, whose bulk sweep does not touch ledgers). Drop it instead of
        // accruing it — the frozen total must never absorb downtime.
        var openMs = terminal ? 0 : OpenSegmentMs(ledger, now);

        if (action == LedgerClock.Refresh && !terminal)
        {
            // The segment is still running: report it on top WITHOUT folding it into the accumulator,
            // so the next Refresh does not count it twice.
            ledger.ActiveMs = active;
            ledger.WallClockMs = active + openMs;
            return;
        }

        // Open/Close (and any write landing on a terminal run) settle the segment here. Folding the
        // open part in before re-opening keeps a redundant open from dropping its elapsed time.
        ledger.ActiveMs = active + openMs;
        ledger.SegmentStartedAt = action == LedgerClock.OpenSegment && !terminal ? now : null;
        ledger.WallClockMs = ledger.ActiveMs.Value;
    }

    /// <summary>Elapsed ms of the open segment (0 when none). Clamped at 0 against clock skew.</summary>
    private static long OpenSegmentMs(Ledger ledger, DateTime now) =>
        ledger.SegmentStartedAt is { } s ? Math.Max(0, (long)(now - AsUtc(s)).TotalMilliseconds) : 0;

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        // We only ever write UTC, so a zone-less value is UTC — reading it as local would shift it.
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    /// <summary>
    /// Applies <paramref name="action"/> to the persisted ledger of <paramref name="runId"/>.
    /// Bookkeeping only: a fault here is logged and swallowed so it can never fail the pause/terminal
    /// write it precedes (guardrail 1). Callers must already hold <see cref="_gate"/>.
    /// </summary>
    private void MoveLedgerClock(Guid runId, LedgerClock action)
    {
        try
        {
            if (!TryLoadRunLedger(runId, out var ledger, out var startedAt, out var state)) return;
            ApplyLedgerClock(ledger, startedAt, state, action);
            WriteLedger(runId, ledger);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ledger clock ({Action}) failed for run {RunId}", action, runId);
        }
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

        /// <summary>
        /// REPORTED total worked time = <see cref="ActiveMs"/> + the currently open segment. The only
        /// field the UI ledger strip reads; recomputed exclusively by <see cref="ApplyLedgerClock"/>.
        /// </summary>
        public long WallClockMs { get; set; }

        /// <summary>
        /// Accumulated ACTIVE milliseconds across all closed work segments (G1). Nullable purely to
        /// detect a legacy ledger written before active-time tracking existed (field absent → null);
        /// every write from this service materialises a non-null value.
        /// </summary>
        public long? ActiveMs { get; set; }

        /// <summary>
        /// UTC start of the OPEN work segment, or null when the run is not working (parked/terminal).
        /// Serialized by <c>System.Text.Json</c> in the round-trippable ISO-8601 form.
        /// </summary>
        public DateTime? SegmentStartedAt { get; set; }

        public List<StepLedger> PerStep { get; set; } = [];
    }

    private sealed class StepLedger
    {
        public string StepId { get; set; } = string.Empty;
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
    }
}
