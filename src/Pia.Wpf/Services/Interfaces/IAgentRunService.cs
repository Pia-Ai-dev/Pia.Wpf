using Microsoft.Extensions.AI;
using Pia.Models;

namespace Pia.Services.Interfaces;

/// <summary>Parameters for creating a new <see cref="AgentRun"/>.</summary>
/// <param name="PolicyJson">
/// OPTIONAL launch-grant envelope, persisted verbatim into <c>AgentRuns.PolicyJson</c> and handed back
/// by <see cref="IAgentRunService.GetAsync"/>/<see cref="IAgentRunService.GetByChatAsync"/> so a resume
/// can rebuild the launch's grants instead of inventing wide ones. Contract: an OPAQUE string at this
/// layer — the launcher owns the schema, the run service never parses, validates or mutates it (written
/// ONCE at create; there is deliberately no policy-mutation API — per-run autonomy policy is a later
/// batch). It may name granted capabilities, so it is metadata: log its PRESENCE only, never its
/// content (CLAUDE.md privacy-first logging).
/// </param>
/// <param name="ParentRunId">
/// The parent run this run was delegated by, or null for a top-level run. Written ONCE at create; there is
/// deliberately no re-parent API — a run's place in the delegation tree is decided by whoever spawned it and
/// nothing later gets to move it. Indexed by <c>IX_AgentRuns_ParentRunId</c>.
/// <para>
/// TRAILING and defaulted on purpose: every one of this record's construction sites passes at most six
/// arguments positionally and names <c>PolicyJson</c>, so appending here is invisible to all of them
/// (07 D10). The column and its round-trip already existed before this member did — only the producer was
/// missing.
/// </para>
/// </param>
public sealed record AgentRunCreateRequest(
    Guid ChatId,
    RunShape Shape,
    AgentRunTrigger Trigger,
    Guid? TriggerRef = null,
    Guid? OwnerDeviceId = null,
    string? Goal = null,
    string? PolicyJson = null,
    Guid? ParentRunId = null);

/// <summary>Raised after a state-changing run write. The 1.4 UI/Flow event source; no consumers in 1.1.</summary>
public sealed class AgentRunChangedEventArgs : EventArgs
{
    public AgentRunChangedEventArgs(Guid runId, AgentRunState state, Guid? stepId = null)
    {
        RunId = runId;
        State = state;
        StepId = stepId;
    }

    public Guid RunId { get; }

    public AgentRunState State { get; }

    /// <summary>The step the change concerns, when the write was step-scoped; otherwise null.</summary>
    public Guid? StepId { get; }
}

/// <summary>
/// Durable store + lifecycle for <see cref="AgentRun"/>/<see cref="AgentStep"/>. Singleton,
/// thread-safe, non-UI (no <c>SynchronizationContext</c> capture) — callable from the UI thread
/// and background threads alike. See phase1 plan §12.3.
/// </summary>
public interface IAgentRunService
{
    /// <summary>
    /// Insert a new run row. <see cref="AgentRunCreateRequest.PolicyJson"/>, when supplied, is stored
    /// as-is and round-trips through the getters — the create is its only write.
    /// </summary>
    Task<AgentRun> CreateAsync(AgentRunCreateRequest request, CancellationToken ct = default);

    Task SetStateAsync(Guid runId, AgentRunState state, CancellationToken ct = default);

    /// <summary>Accrue token usage. Run-level ledger when <paramref name="stepId"/> is null; the matching per-step entry otherwise (§16 R16).</summary>
    Task AddUsageAsync(Guid runId, Guid? stepId, UsageDetails usage, CancellationToken ct = default);

    /// <summary>Record the run's transcript slice by STABLE message Ids (§16 R3).</summary>
    Task SetRunMessageRangeAsync(Guid runId, Guid firstMessageId, Guid lastMessageId, CancellationToken ct = default);

    /// <summary>Terminal → Completed. A truncated run records <c>{truncated:true,reason}</c> in ExtraJson (§16 R5).</summary>
    Task CompleteAsync(Guid runId, bool truncated = false, string? truncationReason = null, CancellationToken ct = default);

    Task FailAsync(Guid runId, string? error, bool cancelled = false, CancellationToken ct = default);

    /// <summary>
    /// Park a run at its budget: State → <see cref="AgentRunState.WaitingForInput"/>, writes
    /// <c>{paused:true,reason}</c> to ExtraJson. This is NOT a completion (no CompletedAt) — the run sits
    /// parked until <see cref="TryBeginResumeAsync"/> claims it. Raises RunChanged(WaitingForInput).
    /// </summary>
    Task PauseAsync(Guid runId, string? reason, CancellationToken ct = default);

    /// <summary>
    /// Atomically CAS-claim a parked run for resume: <see cref="AgentRunState.WaitingForInput"/> →
    /// <see cref="AgentRunState.Running"/>. Returns <c>true</c> iff THIS caller won the claim (guardrail 2
    /// — never two loops on one run). A non-WaitingForInput run returns <c>false</c> and is a no-op.
    /// Raises RunChanged(Running) only on the win.
    /// </summary>
    Task<bool> TryBeginResumeAsync(Guid runId, CancellationToken ct = default);

    /// <summary>
    /// Batch 08 D1 — the USER pause. CAS an executing run to <see cref="AgentRunState.Paused"/>, writing
    /// <c>{paused:true,reason:"user"}</c> (<c>AgentRunService.UserPausedReason</c>) to ExtraJson. Returns
    /// <c>true</c> iff THIS caller won. Raises RunChanged(Paused) only on the win.
    /// <para>
    /// The source states are an EXPLICIT set — <see cref="AgentRunState.Running"/>,
    /// <see cref="AgentRunState.Verifying"/>, <see cref="AgentRunState.WaitingForChildren"/> — never an
    /// ordinal range (D7): <see cref="AgentRunState.WaitingForChildren"/> is appended ABOVE the terminal
    /// band, so any threshold lies about it. <see cref="AgentRunState.Planning"/> is excluded on purpose: a
    /// resume skips planning, so a run paused mid-plan would come back with no plan at all.
    /// </para>
    /// <para>
    /// Writes NO <c>CompletedAt</c> — that is precisely what distinguishes a pause from
    /// <see cref="FailAsync"/>, which stamps one unconditionally. A paused run must stay RESUMABLE, which is
    /// what <see cref="TryResumeFromPauseAsync"/> then claims. A lost CAS writes nothing at all, not even the
    /// ledger clock: whoever moved the run owns its state (R11).
    /// </para>
    /// </summary>
    Task<bool> TryPauseUserAsync(Guid runId, CancellationToken ct = default);

    /// <summary>
    /// Batch 08 — the resume claim for a USER-paused run: CAS <see cref="AgentRunState.Paused"/> →
    /// <see cref="AgentRunState.Running"/>, re-opening a fresh ledger work segment on the win. The SIBLING of
    /// <see cref="TryBeginResumeAsync"/>, and deliberately a second single-source CAS rather than a widened
    /// one: the two claims are DISJOINT by source state, so the launcher dispatches on the row's state
    /// instead of trying one and then the other.
    /// <para>
    /// Like <see cref="TryBeginResumeAsync"/> — and unlike <see cref="TryEndChildWaitAsync"/> — this DOES
    /// clear <c>ExtraJson</c>: the claim retires the pause marker it is consuming, or a cleanly-completing
    /// resumed run would keep reporting itself paused.
    /// </para>
    /// </summary>
    Task<bool> TryResumeFromPauseAsync(Guid runId, CancellationToken ct = default);

    /// <summary>
    /// Park a PARENT while its child runs execute (Batch 07 D9): State →
    /// <see cref="AgentRunState.WaitingForChildren"/>, and the ledger work segment is CLOSED — the parent is
    /// not working, its children are, and each bills its own time. Raises
    /// RunChanged(WaitingForChildren).
    /// <para>
    /// A BLIND update, deliberately, exactly like <see cref="SetStateAsync"/>: at this instant the parent's
    /// own drain loop is the only writer, having just dispatched the children itself. The unpark
    /// (<see cref="TryEndChildWaitAsync"/>) is the CAS, because by then a second writer can exist.
    /// </para>
    /// <para>
    /// <paramref name="childCount"/> is LOGGED as a count and is NOT persisted — the child rows are the
    /// marker (<see cref="GetChildRunsAsync"/>, 07 §0.4), so there is no counter to decrement and no
    /// lost-update race with a settling child.
    /// </para>
    /// </summary>
    Task BeginChildWaitAsync(Guid runId, int childCount, CancellationToken ct = default);

    /// <summary>
    /// End a parent's child wait: CAS <see cref="AgentRunState.WaitingForChildren"/> →
    /// <see cref="AgentRunState.Running"/>, re-opening a fresh ledger work segment on the win (Batch 07 D9).
    /// Returns <c>false</c> when the parent is no longer waiting — cascade-cancelled, or re-parked as
    /// <see cref="AgentRunState.WaitingForInput"/> by <see cref="FailInterruptedRunsAsync"/> in another
    /// process — in which case the caller must NOT continue the run: whoever moved it owns its terminal
    /// state, and a blind write here would RESURRECT a Cancelled parent.
    /// <para>
    /// Unlike <see cref="TryBeginResumeAsync"/> this does NOT clear <c>ExtraJson</c>: it is not a user
    /// "continue" and there is no pause marker to retire.
    /// </para>
    /// </summary>
    Task<bool> TryEndChildWaitAsync(Guid runId, CancellationToken ct = default);

    /// <summary>
    /// Settle every crash-recoverable run (Planning/Running/Verifying — a crash / forced-exit leftover) to
    /// <see cref="AgentRunState.Cancelled"/> so none dangles <see cref="AgentRunState.Running"/> across app
    /// sessions (§17.5/G-4). <see cref="AgentRunState.WaitingForInput"/>/<see cref="AgentRunState.Paused"/>
    /// are a DELIBERATE parked state (budget pause) and are EXCLUDED — a parked run survives restart
    /// resumable. Bulk, silent (raises no <see cref="RunChanged"/> — these are historical leftovers, not
    /// live transitions, so the Flow surface must not re-publish for them at startup).
    /// <para>
    /// Batch 07 D14 adds a SECOND statement in the same call: a parent left
    /// <see cref="AgentRunState.WaitingForChildren"/> is RE-PARKED as
    /// <see cref="AgentRunState.WaitingForInput"/> with the same <c>{paused:true,reason}</c> marker
    /// <see cref="PauseAsync"/> writes, because statement 1 has just cancelled the very children that were
    /// going to wake it. Returns the SUM of both statements.
    /// </para>
    /// </summary>
    Task<int> FailInterruptedRunsAsync(CancellationToken ct = default);

    Task<AgentRun?> GetAsync(Guid runId, CancellationToken ct = default);

    Task<IReadOnlyList<AgentRun>> GetByChatAsync(Guid chatId, CancellationToken ct = default);

    /// <summary>
    /// The CHILD runs of a parent, ordered by <c>CreatedAt</c> — the delegated runs a fan-out step spawned
    /// (Batch 07 D9). Empty for an ordinary childless run, which is every run a build without a persona
    /// roster produces. Backed by <c>IX_AgentRuns_ParentRunId</c>.
    /// <para>
    /// The <see cref="AgentRun.Plan"/> is deliberately NOT loaded: <see cref="GetAsync"/> pays a second
    /// query per run for that, and both callers here want state and ledger — the parent rolling up a
    /// settled child's tokens, and the panel's children list — never the child's own steps. The child ROWS
    /// are also how a parent counts what it is still waiting on, which is why nothing writes a
    /// "waiting on N children" counter anywhere (07 §0.4).
    /// </para>
    /// </summary>
    Task<IReadOnlyList<AgentRun>> GetChildRunsAsync(Guid parentRunId, CancellationToken ct = default);

    /// <summary>True if the chat has any <see cref="RunShape.Planned"/> run (eviction policy, wired in 1.2).</summary>
    Task<bool> ChatHasPlannedRunAsync(Guid chatId, CancellationToken ct = default);

    // Steps: API present in 1.1, exercised in 1.2 (Planned).
    Task ReplaceStepsAsync(Guid runId, IReadOnlyList<AgentStep> steps, CancellationToken ct = default);

    /// <summary>Re-query the persisted Pending steps each call (never iterate a snapshot — §16 R2).</summary>
    Task<AgentStep?> NextPendingStepAsync(Guid runId, CancellationToken ct = default);

    Task SetStepStatusAsync(Guid stepId, AgentStepStatus status, CancellationToken ct = default);

    /// <summary>Terminal step write + per-step ledger + transcript slice (§16 R16, R3).</summary>
    Task RecordStepResultAsync(Guid stepId, AgentStepStatus status,
        Guid? firstMessageId, Guid? lastMessageId, UsageDetails? usage, CancellationToken ct = default);

    /// <summary>For 1.4 UI/Flow; no consumers in 1.1.</summary>
    event EventHandler<AgentRunChangedEventArgs> RunChanged;
}
