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
/// <c>ClarificationsJson</c> is BOTH at once — opaque here and user content — so it is logged as a count only.
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

    /// <summary>
    /// Write-time caps on user-authored step text (Batch 08 D3 item 9), applied by
    /// <see cref="ApplyPlanMutationAsync"/>. The title cap is deliberately the same 200 as
    /// <c>AgentVerifier</c>'s own <c>MaxDeclarationChars</c> (private there, so it cannot be referenced): a
    /// title that arrives already within the verifier's bound is never truncated a second time with a second
    /// ellipsis. Intent is capped nowhere else in the codebase at all, which is why it is capped here.
    /// </summary>
    internal const int MaxStepTitleChars = 200, MaxStepIntentChars = 400, MaxStepArtifactChars = 200;

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
            PersonaId = request.PersonaId,
            ReasoningEffort = request.ReasoningEffort,
            EffortPinRecorded = request.EffortPinRecorded,
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
                     CreatedAt, UpdatedAt, StartedAt, CompletedAt, ExtraJson, PersonaId, ReasoningEffort,
                     EffortPinRecorded)
                VALUES
                    (@Id, @SchemaVersion, @ChatId, @RunShape, @State, @TriggerKind, @TriggerRef, @ParentRunId,
                     @OwnerDeviceId, @Goal, @FirstMessageId, @LastMessageId, @PolicyJson, @LedgerJson,
                     @CreatedAt, @UpdatedAt, @StartedAt, @CompletedAt, @ExtraJson, @PersonaId, @ReasoningEffort,
                     @EffortPinRecorded)
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
            cmd.Parameters.AddWithValue("@PersonaId", ToParam(run.PersonaId));
            cmd.Parameters.AddWithValue("@ReasoningEffort", ToParam(run.ReasoningEffort));
            cmd.Parameters.AddWithValue("@EffortPinRecorded", run.EffortPinRecorded ? 1 : 0);
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

    public Task FailAsync(
        Guid runId, string? error, bool cancelled = false, CancellationToken ct = default,
        PiaFailure? failure = null)
    {
        var state = cancelled ? AgentRunState.Cancelled : AgentRunState.Failed;
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;

            MoveLedgerClock(runId, LedgerClock.CloseSegment);

            // The descriptor is ADDITIVE: the free-text reason is written exactly as before, so an
            // unmapped message still reaches the card unchanged.
            var extraJson = error is not null
                ? JsonSerializer.Serialize(new { error }, JsonOptions)
                : null;
            var failureJson = failure?.ToJson();

            using var cmd = Connection().CreateCommand();
            cmd.CommandText =
                "UPDATE AgentRuns SET State=@State, CompletedAt=@Now, UpdatedAt=@Now, ExtraJson=@Extra, " +
                "FailureJson=@Failure WHERE Id=@Id";
            cmd.Parameters.AddWithValue("@State", (int)state);
            cmd.Parameters.AddWithValue("@Now", now.ToString("O"));
            cmd.Parameters.AddWithValue("@Extra", ToParam(extraJson));
            cmd.Parameters.AddWithValue("@Failure", ToParam(failureJson));
            cmd.Parameters.AddWithValue("@Id", runId.ToString());
            cmd.ExecuteNonQuery();
        }

        _logger.LogInformation("Run {RunId} → {State}", runId, state);
        RunChanged?.Invoke(this, new AgentRunChangedEventArgs(runId, state));
        return Task.CompletedTask;
    }

    public Task PauseAsync(Guid runId, string? reason, CancellationToken ct = default, string? approvalTool = null,
        string? approvalArgs = null)
    {
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;

            // Close the work segment before parking (mirrors CompleteAsync): the parked gap that starts
            // here must NOT count as worked time, and the next resume opens a fresh segment (G1).
            MoveLedgerClock(runId, LedgerClock.CloseSegment);

            // hermes #16: TWO shapes, not one shape with a nullable member. A null approvalTool serializes
            // the ORIGINAL anonymous type and is therefore byte-identical to every pause this service has
            // ever written — no `"tool":null` appears on a budget park, a children park or a resume re-park,
            // and no existing envelope pin moves. The reader tolerates either shape.
            // The args member joins the tool the same way the tool member joined the pair: written only when
            // there is one, so a budget park and a re-park stay byte-identical to every envelope ever written.
            var extraJson = approvalTool is null
                ? JsonSerializer.Serialize(new { paused = true, reason }, JsonOptions)
                : approvalArgs is null
                    ? JsonSerializer.Serialize(new { paused = true, reason, tool = approvalTool }, JsonOptions)
                    : JsonSerializer.Serialize(new { paused = true, reason, tool = approvalTool, args = approvalArgs }, JsonOptions);

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

    public Task UpdatePolicyJsonAsync(Guid runId, string? policyJson, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;

            using var cmd = Connection().CreateCommand();
            cmd.CommandText = "UPDATE AgentRuns SET PolicyJson=@Policy, UpdatedAt=@Now WHERE Id=@Id";
            cmd.Parameters.AddWithValue("@Policy", ToParam(policyJson));
            cmd.Parameters.AddWithValue("@Now", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@Id", runId.ToString());
            cmd.ExecuteNonQuery();
        }

        // Presence only, never the document: it names tools and tool classes, and this line lands in a
        // support-attachable log. Same discipline as the create-time line above.
        _logger.LogInformation("Run {RunId} grant envelope updated (present={Present})", runId, policyJson is not null);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> AppendClarificationAsync(Guid runId, string? answer, CancellationToken ct = default)
    {
        IReadOnlyList<string> answers;
        lock (_gate)
        {
            if (_disposed) return Task.FromResult<IReadOnlyList<string>>([]);

            // The whole read-modify-write is inside one _gate hold, so two callers appending concurrently
            // (e.g. the panel's Continue and a Flow ContinueRun on the same run) can't drop each other's answer.
            using var read = Connection().CreateCommand();
            read.CommandText = "SELECT ClarificationsJson FROM AgentRuns WHERE Id=@Id";
            read.Parameters.AddWithValue("@Id", runId.ToString());
            var existing = read.ExecuteScalar();
            // No row (a deleted run) reads as no document and appends nothing.
            if (existing is null)
                return Task.FromResult<IReadOnlyList<string>>([]);

            var current = existing as string;
            var updated = RunClarifications.Append(current, answer);
            if (updated is null)
                return Task.FromResult(RunClarifications.Read(current)); // blank answer: nothing to write

            using var cmd = Connection().CreateCommand();
            cmd.CommandText = "UPDATE AgentRuns SET ClarificationsJson=@Clarifications, UpdatedAt=@Now WHERE Id=@Id";
            cmd.Parameters.AddWithValue("@Clarifications", ToParam(updated));
            cmd.Parameters.AddWithValue("@Now", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@Id", runId.ToString());
            cmd.ExecuteNonQuery();
            answers = RunClarifications.Read(updated);
        }

        // Count only — the answers are user-typed content, so the text goes out on SensitiveDebug below, never
        // at this level. Deliberately no RunChanged: this write changes nothing the panel renders.
        _logger.LogInformation("Run {RunId} recorded a clarification answer (total={Count})", runId, answers.Count);
        _logger.SensitiveDebug("Run {RunId} clarification answers: {Answers}", runId, string.Join(" | ", answers));
        return Task.FromResult(answers);
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
            // Planning is DELIBERATELY excluded: a resume runs RunAsync(resume: true), which skips planning,
            // so a run paused mid-plan would come back with NO plan, drain zero steps and settle Completed
            // having done nothing. A needs-goal resume is the one exception that re-plans, but the CAS below
            // writes the `user-paused` token instead, so a user pause mid-plan still comes back with no plan.
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

    public Task<bool> TryRejectParkedPlanAsync(Guid runId, CancellationToken ct = default)
    {
        int affected;
        lock (_gate)
        {
            if (_disposed) return Task.FromResult(false);

            var connection = Connection();
            AgentRun? run;
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $"SELECT {RunColumns} FROM AgentRuns WHERE Id=@Id";
                cmd.Parameters.AddWithValue("@Id", runId.ToString());
                using var reader = cmd.ExecuteReader();
                run = reader.Read() ? MapRun(reader) : null;
            }

            // Reason-gated rather than a bare `WHERE State=@Expected` CAS: state alone cannot tell this plan's
            // park from a run that resumed and re-parked on a different question since.
            if (run is null || run.State != AgentRunState.WaitingForInput
                || RunPauseEnvelope.ReadReason(run) != AgentRunOrchestrator.PlanApprovalReason)
            {
                affected = 0;
            }
            else
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText =
                    "UPDATE AgentRuns SET State=@New, CompletedAt=@Now, UpdatedAt=@Now, ExtraJson=NULL WHERE Id=@Id AND State=@Expected";
                cmd.Parameters.AddWithValue("@New", (int)AgentRunState.Cancelled);
                cmd.Parameters.AddWithValue("@Now", DateTime.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("@Id", runId.ToString());
                cmd.Parameters.AddWithValue("@Expected", (int)AgentRunState.WaitingForInput);
                affected = cmd.ExecuteNonQuery();
                // No MoveLedgerClock: the park already closed the work segment and this opens none.
            }
        }

        if (affected > 0)
        {
            _logger.LogInformation("Run {RunId} plan rejected → Cancelled", runId);
            RunChanged?.Invoke(this, new AgentRunChangedEventArgs(runId, AgentRunState.Cancelled));
        }
        else
        {
            _logger.LogInformation("Run {RunId} plan-reject not applied — no longer parked on plan-approval", runId);
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

    /// <summary>
    /// The state values <see cref="AnyExecutingRunForTriggerAsync"/> excludes, DERIVED from
    /// <see cref="AgentRunStates.IsExecuting"/> rather than restated as a literal list, so the predicate and this
    /// query cannot drift apart the way a hand-copied set would. Declared before the SQL below because static
    /// field initializers run in declaration order.
    /// </summary>
    private static readonly int[] NonExecutingStates =
        Enum.GetValues<AgentRunState>().Where(s => !AgentRunStates.IsExecuting(s)).Select(s => (int)s).ToArray();

    /// <summary>
    /// Seeks <c>IX_AgentRuns_TriggerRef</c>. An explicit exclusion set, never a <c>State &lt; x</c> range (D7:
    /// <see cref="AgentRunState.WaitingForChildren"/> sits ABOVE the terminal band, so a range lies about it).
    /// The ints are interpolated rather than parameterized because they come from an enum, not from a caller.
    /// </summary>
    private static readonly string AnyExecutingForTriggerSql =
        "SELECT COUNT(*) FROM AgentRuns WHERE TriggerRef=@Trigger AND State NOT IN (" +
        string.Join(",", NonExecutingStates) + ")";

    public Task<bool> AnyExecutingRunForTriggerAsync(Guid triggerRef, CancellationToken ct = default)
    {
        lock (_gate)
        {
            // A disposed service answers "nothing is executing". The caller is a guard whose miss costs a
            // duplicate dispatch, and the only way to reach it disposed is app shutdown, where no tick runs.
            if (_disposed) return Task.FromResult(false);

            using var cmd = Connection().CreateCommand();
            cmd.CommandText = AnyExecutingForTriggerSql;
            // Bound exactly as AgentRunService writes it (ToParam → Guid.ToString(), lowercase "D"), or the
            // index seek matches nothing and the guard silently never fires.
            cmd.Parameters.AddWithValue("@Trigger", triggerRef.ToString());
            var count = Convert.ToInt64(cmd.ExecuteScalar());
            return Task.FromResult(count > 0);
        }
    }

    /// <summary>
    /// The SETTLED states, derived from the two existing predicates rather than restated as a literal list, for
    /// the same anti-drift reason <see cref="NonExecutingStates"/> is derived from one. Today exactly
    /// {Completed, Failed, Cancelled}; an appended state counts as EXECUTING (see
    /// <see cref="AgentRunStates.IsExecuting"/>'s double negation) and so falls OUT of this set, which is the
    /// safe direction here — a firing this build cannot classify is simply not booked, rather than booked as a
    /// failure the job never had.
    /// </summary>
    private static readonly int[] SettledStates =
        Enum.GetValues<AgentRunState>()
            .Where(s => !AgentRunStates.IsExecuting(s) && !AgentRunStates.IsParked(s))
            .Select(s => (int)s).ToArray();

    /// <summary>
    /// Seeks <c>IX_AgentRuns_TriggerRef</c>. SQLITE-SPECIFIC by design: the bare <c>Id</c>/<c>ChatId</c>/
    /// <c>State</c> columns beside <c>MAX(CompletedAt)</c> are resolved from the row that produced the maximum —
    /// SQLite's documented bare-column rule for a min/max aggregate. This repo has one engine and no ORM
    /// (hand-rolled DDL in <c>SqliteContext.EnsureSchema</c>), so the rule is a fact about the code, not an
    /// assumption about a portable dialect.
    /// <para>
    /// <c>MAX</c> over a TEXT column is a STRING max, and it is chronological here only because
    /// <c>CompletedAt</c> is uniformly <c>DateTime.UtcNow.ToString("O")</c> — fixed width, zero-padded,
    /// always <c>Z</c>, so lexicographic order is instant order. That uniformity is exactly what
    /// <c>ScheduledJobs.LastFiredAt</c> does NOT have (it is local time WITH an offset), which is why the
    /// reconcile joins these two columns in C# after normalizing, and never in SQL.
    /// </para>
    /// </summary>
    private static readonly string LatestSettledFiringsSql =
        """
        SELECT TriggerRef, Id, ChatId, State, MAX(CompletedAt)
        FROM AgentRuns
        WHERE TriggerRef IS NOT NULL AND CompletedAt IS NOT NULL AND State IN (
        """
        + string.Join(",", SettledStates) + ") GROUP BY TriggerRef";

    public Task<IReadOnlyList<ScheduledFiringOutcome>> GetLatestSettledFiringsAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_disposed) return Task.FromResult<IReadOnlyList<ScheduledFiringOutcome>>(Array.Empty<ScheduledFiringOutcome>());

            var list = new List<ScheduledFiringOutcome>();
            using var cmd = Connection().CreateCommand();
            cmd.CommandText = LatestSettledFiringsSql;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                // TriggerRef is free-form TEXT at the schema level; a value that is not a Guid belongs to no
                // scheduled job and is skipped rather than thrown on — this runs at startup, where a single
                // malformed row must not cost every other job its booking.
                if (!Guid.TryParse(reader.GetString(0), out var jobId)) continue;

                list.Add(new ScheduledFiringOutcome(
                    jobId,
                    Guid.Parse(reader.GetString(1)),
                    Guid.Parse(reader.GetString(2)),
                    // Same parse+normalize as MapRun's CompletedAt: the stored string is UTC, but
                    // DateTime.Parse hands back a LOCAL-kind value, and this record promises UTC.
                    DateTime.Parse(reader.GetString(4)).ToUniversalTime(),
                    (AgentRunState)reader.GetInt32(3)));
            }

            return Task.FromResult<IReadOnlyList<ScheduledFiringOutcome>>(list);
        }
    }

    /// <summary>
    /// T2-18. Seeks <c>IX_AgentRuns_TriggerRef</c> and orders by the same <c>CompletedAt</c> TEXT column the
    /// aggregate above relies on — chronological because every writer stamps it
    /// <c>DateTime.UtcNow.ToString("O")</c> (fixed width, zero-padded, always <c>Z</c>), which is the property
    /// that makes a string sort an instant sort. No <c>GROUP BY</c> here: this is the LIST, not the latest.
    /// </summary>
    private static readonly string FiringsForTriggerSql =
        """
        SELECT TriggerRef, Id, ChatId, State, CompletedAt
        FROM AgentRuns
        WHERE TriggerRef=@Trigger AND CompletedAt IS NOT NULL AND State IN (
        """
        + string.Join(",", SettledStates) + ") ORDER BY CompletedAt DESC LIMIT @Limit";

    public Task<IReadOnlyList<ScheduledFiringOutcome>> GetFiringsForTriggerAsync(
        Guid triggerRef, int limit, CancellationToken ct = default)
    {
        // Clamped rather than trusted: this reaches SQL as a LIMIT, and a caller passing 0 or a negative would
        // silently return nothing (SQLite treats a negative limit as "no limit", which is the opposite mistake).
        var take = Math.Clamp(limit, 1, 100);

        lock (_gate)
        {
            if (_disposed) return Task.FromResult<IReadOnlyList<ScheduledFiringOutcome>>(Array.Empty<ScheduledFiringOutcome>());

            var list = new List<ScheduledFiringOutcome>();
            using var cmd = Connection().CreateCommand();
            cmd.CommandText = FiringsForTriggerSql;
            cmd.Parameters.AddWithValue("@Trigger", triggerRef.ToString());
            cmd.Parameters.AddWithValue("@Limit", take);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new ScheduledFiringOutcome(
                    triggerRef, // the WHERE already pinned it; re-parsing the column would only add a failure mode
                    Guid.Parse(reader.GetString(1)),
                    Guid.Parse(reader.GetString(2)),
                    // Same parse+normalize as above: the stored string is UTC, DateTime.Parse hands back a
                    // LOCAL-kind value, and this record promises UTC.
                    DateTime.Parse(reader.GetString(4)).ToUniversalTime(),
                    (AgentRunState)reader.GetInt32(3)));
            }

            return Task.FromResult<IReadOnlyList<ScheduledFiringOutcome>>(list);
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
                InsertStepRow(connection, transaction, runId, step, step.Ordinal, now);

            transaction.Commit();
        }

        return Task.CompletedTask;
    }

    public Task<PlanMutationResult> ApplyPlanMutationAsync(
        Guid runId, IReadOnlyList<PlanStepEdit> pendingSteps, CancellationToken ct = default)
    {
        int applied;
        int inserted = 0, skipped = 0;
        List<AgentStep> rows;
        lock (_gate)
        {
            if (_disposed) return Task.FromResult(new PlanMutationResult(PlanMutationOutcome.WriteFailed, 0));

            // The WHOLE operation is inside the _gate hold: NextPendingStepAsync is a method of this same
            // class behind this same lock, so a drain can never observe a half-rewritten plan, and the
            // Paused read below cannot go stale between the gate and the write.
            var connection = Connection();

            // THE GATE (D3): one state, never a set and never a range (D7). A missing run reads as NotPaused
            // rather than a separate outcome — from the caller's side "there is nothing pausable to mutate"
            // is the same answer.
            AgentRunState state;
            using (var read = connection.CreateCommand())
            {
                read.CommandText = "SELECT State FROM AgentRuns WHERE Id=@Id";
                read.Parameters.AddWithValue("@Id", runId.ToString());
                var raw = read.ExecuteScalar();
                if (raw is null or DBNull)
                    return Task.FromResult(new PlanMutationResult(PlanMutationOutcome.NotPaused, 0));
                state = (AgentRunState)Convert.ToInt32(raw);
            }

            var persisted = LoadSteps(connection, runId);
            if (state != AgentRunState.Paused)
                return Task.FromResult(new PlanMutationResult(PlanMutationOutcome.NotPaused, persisted.Count));

            // The immutable prefix: everything already settled — Done, Skipped AND Failed. Kept in persisted
            // ordinal order with its ORIGINAL Ids, which is what keeps its per-step ledger entries (keyed by
            // step id) and its timeline rows attached to something.
            //
            // Batch 08 F15, KNOWN AND DELIBERATE: a SKIPPED step sorts with the settled work, so the NEXT
            // mutation hoists it above every still-pending step. Plan [0 Done, 1 Done, 2 Pending, 3 Pending,
            // 4 Pending], skip 4 (order preserved — the skipped row rides in that submission's tail), then
            // edit 2: prefix [0,1,4], tail [2,3], persisted order [0,1,4,2,3]. The panel repaints it
            // faithfully, so the user sees a plan order they never arranged.
            //
            // NOT FIXED, on the review's own adjudicated recommendation, because the tempting one-line patch
            // (narrow this filter to `Done or Failed`) is FOUR-SIDED and half of it is silently destructive:
            // (a) `editable` below would have to admit Skipped rows or every resubmission of one returns
            // UnknownStep; (b) the VM's five verbs build their submissions from Pending rows only, so a
            // Skipped row would be DROPPED from the plan rather than reordered; (c) un-skipping must still be
            // refused; and (d) it weakens the stated property below from "no settled row can move" to "no
            // Done/Failed row can move". The defect is cosmetic — a Skipped step never drains, so execution
            // order is unaffected — and the proportionate action is to say so here. Changing it is an owner
            // call: "plan order must read as the user arranged it" is a legitimate requirement, it is just not
            // one worth trading that invariant for unasked.
            var prefix = persisted.Where(s => s.Status != AgentStepStatus.Pending).OrderBy(s => s.Ordinal).ToList();
            var editable = persisted.Where(s => s.Status == AgentStepStatus.Pending).ToDictionary(s => s.Id);

            var tail = new List<AgentStep>(pendingSteps.Count);
            var claimed = new HashSet<Guid>();
            foreach (var edit in pendingSteps)
            {
                AgentStep? original = null;
                if (edit.StepId is { } id && (!editable.TryGetValue(id, out original) || !claimed.Add(id)))
                    return Task.FromResult(new PlanMutationResult(PlanMutationOutcome.UnknownStep, persisted.Count));

                // Normalize BEFORE the blank check, so a title of only whitespace and newlines is
                // TitleRequired rather than a persisted row with an empty Title.
                var title = NormalizeStepText(edit.Title, MaxStepTitleChars);
                if (title.Length == 0)
                    return Task.FromResult(new PlanMutationResult(PlanMutationOutcome.TitleRequired, persisted.Count));

                if (original is null) inserted++;
                if (edit.Skip) skipped++;

                // Only Title/Intent/ExpectedArtifact/Status are the user's to change. Every other column of an
                // EDITED step is carried from the persisted row — ExtraJson above all, because it is where the
                // planner writes {"parallelGroup":N} and clobbering it quietly makes a fan-out plan sequential
                // again; AssignedPersonaId for the same class of reason (the step would silently change
                // persona). An INSERT gets the model's defaults, which is what a step nobody planned has.
                //
                // Batch 08 F7: INTENT FALLS BACK TO THE TITLE, it is not separately validated. The validated
                // field was Title, but Intent is the only field either executor sends — ChatSession builds
                // `Execute step {n}: {Intent}.` and HeadlessTurnExecutor's BuildInstruction takes
                // `step.Intent ?? ""`; neither ever reads Title. AgentPlanner drops a planner step whose Intent
                // is blank, so this method was the FIRST writer in the codebase able to persist a Pending step
                // with a null Intent — and the panel's "Insert step below" minted exactly that, so an inserted
                // step shipped the literal turn "Execute step 3: .", burned a step against the budget, billed
                // the tokens and then entered the verify prompt as completed work. A fallback beats a second
                // required field: the title is already required, flattened and capped, and an intent-less step
                // then reads as "do what the title says".
                tail.Add(new AgentStep
                {
                    Id = original?.Id ?? Guid.Empty,        // Guid.Empty ⇒ the insert mints one
                    RunId = runId,
                    Title = title,
                    Intent = NullIfBlank(NormalizeStepText(edit.Intent, MaxStepIntentChars)) ?? title,
                    ExpectedArtifact = NullIfBlank(NormalizeStepText(edit.ExpectedArtifact, MaxStepArtifactChars)),
                    Status = edit.Skip ? AgentStepStatus.Skipped : AgentStepStatus.Pending,
                    AssignedPersonaId = original?.AssignedPersonaId,
                    DependsOnJson = original?.DependsOnJson,
                    ReRunnable = original?.ReRunnable ?? true,
                    FirstMessageId = original?.FirstMessageId,
                    LastMessageId = original?.LastMessageId,
                    CreatedAt = original?.CreatedAt ?? default,
                    ExtraJson = original?.ExtraJson,
                });
            }

            rows = new List<AgentStep>(prefix.Count + tail.Count);
            rows.AddRange(prefix);
            rows.AddRange(tail);

            if (rows.Count == 0)
                return Task.FromResult(new PlanMutationResult(PlanMutationOutcome.EmptyPlan, persisted.Count));
            if (rows.Count > RunProfile.MaxStepsCap)
                return Task.FromResult(new PlanMutationResult(PlanMutationOutcome.TooLong, persisted.Count));

            try
            {
                var now = DateTime.UtcNow;
                using var transaction = connection.BeginTransaction();

                using (var delete = connection.CreateCommand())
                {
                    delete.Transaction = transaction;
                    delete.CommandText = "DELETE FROM AgentSteps WHERE RunId=@RunId";
                    delete.Parameters.AddWithValue("@RunId", runId.ToString());
                    delete.ExecuteNonQuery();
                }

                // Ordinals are assigned HERE, prefix first, contiguous from 0 — never taken from the caller.
                // That is what makes a duplicate, negative, non-contiguous or across-the-settled-boundary
                // ordinal unrepresentable instead of merely rejected.
                for (var i = 0; i < rows.Count; i++)
                {
                    // A settled step keeps the UpdatedAt that says when it settled; the rewritten tail is
                    // stamped now.
                    var isPrefix = i < prefix.Count;
                    InsertStepRow(connection, transaction, runId, rows[i], i, now,
                        updatedAt: isPrefix ? rows[i].UpdatedAt : null);
                }

                transaction.Commit();
            }
            catch (Exception ex)
            {
                // `using var transaction` disposes without a commit, so SQLite rolls the whole rewrite back:
                // the plan is exactly what it was, which is the property that lets a caller retry.
                _logger.LogWarning(ex, "Plan mutation of run {RunId} faulted and was rolled back", runId);
                return Task.FromResult(new PlanMutationResult(PlanMutationOutcome.WriteFailed, persisted.Count));
            }

            applied = rows.Count;
        }

        // Counts only — titles and intents are user content and go to SensitiveDebug on their own line, never
        // interpolated into a release-visible one.
        _logger.LogInformation(
            "Plan of run {RunId} mutated by the user: {Total} step(s), {Inserted} new, {Skipped} skipped",
            runId, applied, inserted, skipped);
        _logger.SensitiveDebug("Plan of run {RunId} mutated to: {Titles}", runId, string.Join(" | ", rows.Select(s => s.Title)));

        // The panel refreshes from RunChanged and from nothing else (ReplaceStepsAsync raises none, which is
        // why the replan path cannot repaint a row either). Step-less: the change is the plan, not a step.
        RunChanged?.Invoke(this, new AgentRunChangedEventArgs(runId, AgentRunState.Paused));
        return Task.FromResult(new PlanMutationResult(PlanMutationOutcome.Applied, applied));
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
        Guid? firstMessageId, Guid? lastMessageId, UsageDetails? usage, CancellationToken ct = default,
        string? artifactRef = null)
    {
        Guid runId;
        AgentRunState runState;
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;

            // Isolated: losing the artifact must not cost the step status or ledger write it shares.
            string? mergedExtras = null;
            if (!string.IsNullOrWhiteSpace(artifactRef))
            {
                try
                {
                    using var read = Connection().CreateCommand();
                    read.CommandText = "SELECT ExtraJson FROM AgentSteps WHERE Id=@Id";
                    read.Parameters.AddWithValue("@Id", stepId.ToString());
                    mergedExtras = StepExtraJson.WithArtifactRef(read.ExecuteScalar() as string, artifactRef);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Persisting the step artifact reference failed for {StepId}", stepId);
                }
            }

            using (var cmd = Connection().CreateCommand())
            {
                // COALESCE: a step with no reported artifact leaves ExtraJson byte-identical.
                cmd.CommandText = """
                    UPDATE AgentSteps
                    SET Status=@Status, FirstMessageId=@First, LastMessageId=@Last, UpdatedAt=@Now,
                        ExtraJson=COALESCE(@Extra, ExtraJson)
                    WHERE Id=@Id
                    """;
                cmd.Parameters.AddWithValue("@Status", (int)status);
                cmd.Parameters.AddWithValue("@First", ToParam(firstMessageId));
                cmd.Parameters.AddWithValue("@Last", ToParam(lastMessageId));
                cmd.Parameters.AddWithValue("@Now", DateTime.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("@Extra", ToParam(mergedExtras));
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

    // Columns are appended at the end rather than slotted in where the table declares them — MapRun reads by
    // ordinal, so inserting one mid-list would silently re-index every field after it.
    private const string RunColumns =
        "Id, SchemaVersion, ChatId, RunShape, State, TriggerKind, TriggerRef, ParentRunId, OwnerDeviceId, " +
        "Goal, FirstMessageId, LastMessageId, PolicyJson, LedgerJson, CreatedAt, UpdatedAt, StartedAt, CompletedAt, ExtraJson, " +
        "ClarificationsJson, PersonaId, ReasoningEffort, EffortPinRecorded, FailureJson";

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
        // Opaque here: RunClarifications owns the shape.
        ClarificationsJson = r.IsDBNull(19) ? null : r.GetString(19),
        PersonaId = ParseNullableGuid(r, 20),
        ReasoningEffort = r.IsDBNull(21) ? null : ParseReasoningEffort(r.GetString(21)),
        EffortPinRecorded = r.GetInt32(22) == 1,
        FailureJson = r.IsDBNull(23) ? null : r.GetString(23),
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

    /// <summary>
    /// One <c>AgentSteps</c> INSERT, shared by <see cref="ReplaceStepsAsync"/> (which supplies the step's own
    /// ordinal) and <see cref="ApplyPlanMutationAsync"/> (which assigns them). Both paths DELETE the run's
    /// rows first, so this is only ever an insert.
    /// </summary>
    /// <param name="updatedAt">Null ⇒ <paramref name="now"/>. A plan mutation passes the settled prefix's own
    /// <c>UpdatedAt</c> so re-writing the plan around a Done step does not restamp when it finished.</param>
    private static void InsertStepRow(
        SqliteConnection connection, SqliteTransaction transaction, Guid runId, AgentStep step, int ordinal,
        DateTime now, DateTime? updatedAt = null)
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
        insert.Parameters.AddWithValue("@Ordinal", ordinal);
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
        insert.Parameters.AddWithValue("@UpdatedAt", (updatedAt ?? now).ToString("O"));
        insert.Parameters.AddWithValue("@ExtraJson", ToParam(step.ExtraJson));
        insert.ExecuteNonQuery();
    }

    /// <summary>
    /// Flatten CR/LF/TAB to spaces, trim, then cap with a trailing ellipsis (Batch 08 D3 item 9). Applied at
    /// WRITE time, to user-authored step text, which bounds every prompt that later interpolates it —
    /// <c>AgentVerifier</c>'s fact lines, the replan's plan listing and both executors' step instruction — at
    /// one seam instead of five. The flatten is the load-bearing half: a title containing a newline plus a
    /// leading "- " can otherwise FORGE a fact line inside a prompt built by appending lines.
    /// </summary>
    private static string NormalizeStepText(string? text, int cap)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var flat = text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
        return flat.Length <= cap ? flat : flat[..cap] + "…";
    }

    private static string? NullIfBlank(string text) => text.Length == 0 ? null : text;

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

    /// <summary>The enum NAME, like ScheduledJobs stores its pin, so a hand-read row is legible.</summary>
    private static object ToParam(Pia.Models.ReasoningEffort? value) => value is { } e ? e.ToString() : DBNull.Value;

    /// <summary>An unknown name or an out-of-range ordinal means unset; TryParse alone would accept the ordinal.</summary>
    private static Pia.Models.ReasoningEffort? ParseReasoningEffort(string raw) =>
        Enum.TryParse<Pia.Models.ReasoningEffort>(raw, out var effort) && Enum.IsDefined(effort) ? effort : null;

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
