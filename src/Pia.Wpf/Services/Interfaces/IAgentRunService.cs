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

/// <summary>
/// One submitted PENDING step of a plan mutation (Batch 08 D3), as the user left it in the run panel.
/// </summary>
/// <param name="StepId">
/// Null INSERTS a new step; a non-null id must name a step that is currently
/// <see cref="AgentStepStatus.Pending"/> on this run, and carries that step forward with its Id — and
/// therefore with its per-step ledger entry and its timeline rows — intact. A settled step's id here is
/// <see cref="PlanMutationOutcome.UnknownStep"/>, and so is the same id twice.
/// </param>
/// <param name="Title">SENSITIVE (user content). Required; flattened, trimmed and capped by the service.</param>
/// <param name="Intent">SENSITIVE (user content). Optional; blank after normalization ⇒ null.</param>
/// <param name="ExpectedArtifact">Optional artifact declaration; blank after normalization ⇒ null.</param>
/// <param name="Skip">
/// True writes <see cref="AgentStepStatus.Skipped"/> instead of <see cref="AgentStepStatus.Pending"/>: the
/// drain never returns it, and — unlike every other non-Done status — a later replan PRESERVES it, because a
/// skip is the user's decision and a replan must not quietly re-add work they removed.
/// </param>
/// <remarks>
/// There is deliberately no <c>Ordinal</c>: the service assigns ordinals from the submitted ORDER, which is
/// what makes a whole class of ordinal defects unrepresentable rather than merely rejected.
/// </remarks>
public sealed record PlanStepEdit(
    Guid? StepId,
    string Title,
    string? Intent,
    string? ExpectedArtifact,
    bool Skip = false);

/// <summary>
/// Why <see cref="IAgentRunService.ApplyPlanMutationAsync"/> did or did not write. Everything except
/// <see cref="Applied"/> leaves the persisted plan byte-identical.
/// </summary>
public enum PlanMutationOutcome
{
    /// <summary>The plan was rewritten and <c>RunChanged</c> was raised.</summary>
    Applied = 0,

    /// <summary>The run is missing, or is not <see cref="AgentRunState.Paused"/> — the gate (D3).</summary>
    NotPaused = 1,

    /// <summary>An entry names a step that is not a Pending step of this run, or names one twice.</summary>
    UnknownStep = 2,

    /// <summary>A title was blank once flattened and trimmed.</summary>
    TitleRequired = 3,

    /// <summary>
    /// Zero rows in total. Unreachable while no verb deletes the settled prefix, and kept because the
    /// consequence is silent: an empty plan makes the drain return null immediately, the run verifies with no
    /// completed steps, the critic degrades to ACCEPT and the run settles <c>Completed</c> having done nothing.
    /// </summary>
    EmptyPlan = 4,

    /// <summary>More rows than <see cref="RunProfile.MaxStepsCap"/> — the only run-independent bound, since a
    /// run's own <c>MaxSteps</c> lives in an ephemeral profile and a resume is granted a fresh budget.</summary>
    TooLong = 5,

    /// <summary>The transaction faulted and rolled back; the plan is unchanged.</summary>
    WriteFailed = 6,
}

/// <summary>
/// The verdict of a plan mutation plus the plan's row count — the NEW count on
/// <see cref="PlanMutationOutcome.Applied"/>, and the UNCHANGED persisted count on every rejection (0 when the
/// run could not be read at all), so a caller can repaint from the number it gets either way.
/// </summary>
public readonly record struct PlanMutationResult(PlanMutationOutcome Outcome, int StepCount);

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
    /// <param name="approvalTool">
    /// hermes #16, and the ONLY caller that supplies it is the approval park
    /// (<c>AgentRunOrchestrator.ToolApprovalReason</c>): the tool name the human is being asked to approve,
    /// written as a third <c>tool</c> member of the same envelope. Null — every other park — writes the
    /// envelope byte-for-byte as before, so no existing document or pin changes shape.
    /// <para>
    /// It is a TOOL name: app/plugin-defined, never user content, which is why it may sit in a document the
    /// panel and the Flow card both render and why it may be logged as a scalar.
    /// </para>
    /// </param>
    Task PauseAsync(Guid runId, string? reason, CancellationToken ct = default, string? approvalTool = null);

    /// <summary>
    /// hermes #16. Replace the run's opaque launch-grant envelope (<c>AgentRuns.PolicyJson</c>). The service
    /// still never parses the string — this is a verbatim overwrite, and <c>HeadlessRunLauncher</c> remains
    /// the only owner of the shape.
    /// <para>
    /// THE ONE DELIBERATE POST-LAUNCH WIDENING, and it contradicts the rule stated at the resume's own grant
    /// restore ("a resume must never widen them") on purpose: that rule exists so a SETTINGS flip, or a lost
    /// envelope, cannot silently hand a parked run more authority than it launched with. A human pressing
    /// Continue on a card that names the tool is the opposite of silent — it is the informed act the
    /// approval park exists to collect — and persisting it is what stops a run that needs two tools from
    /// ping-ponging between two parks forever, each resume granting one and forgetting the other.
    /// </para>
    /// <para>
    /// Blind write, no CAS: the caller has already won the resume claim on this run, and the value is
    /// idempotent (the same envelope re-serialized). A run id that does not exist updates nothing.
    /// </para>
    /// </summary>
    Task UpdatePolicyJsonAsync(Guid runId, string? policyJson, CancellationToken ct = default);

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

    /// <summary>
    /// Rewrite the PENDING tail of a paused run's plan — the edit / insert / reorder / skip of Batch 08 D3 —
    /// as ONE atomic, validated write. <paramref name="pendingSteps"/> is the COMPLETE new tail in its new
    /// order: an entry with a non-null <see cref="PlanStepEdit.StepId"/> carries an existing Pending step
    /// forward, a null one inserts, the order of the list IS the new order, and a Pending step the caller
    /// omits is DROPPED (there is no delete verb — dropping is what omission means, and D3's UI submits the
    /// whole list).
    /// <para>
    /// GATED ON <see cref="AgentRunState.Paused"/> — one state, never a set and never a range (D7). That gate
    /// is the design, not an accident of the UI: it removes D3's mutation-versus-drain race BY CONSTRUCTION,
    /// because the only writer of a paused run's plan is the user. A mutation landing between the loop's
    /// <c>NextPendingStepAsync</c> and its step execution would otherwise run a step that is no longer in the
    /// plan, and one landing during the step's terminal write would delete the in-flight row so the write
    /// silently updates 0 rows — no ledger entry, no event, no log.
    /// </para>
    /// <para>
    /// The IMMUTABLE PREFIX is every step whose status is not <see cref="AgentStepStatus.Pending"/> —
    /// <see cref="AgentStepStatus.Done"/>, <see cref="AgentStepStatus.Skipped"/> AND
    /// <see cref="AgentStepStatus.Failed"/> (a paused run genuinely can carry one: a step fails, its Failed
    /// row is recorded, and the user pauses during the failure replan's provider call, so no replan ever
    /// pruned it). It is preserved with its ORIGINAL Ids, which is what keeps its per-step ledger entries and
    /// its timeline rows attached, and re-ordinaled <c>0..k-1</c>. Because a skipped step is in the prefix, a
    /// skip is ONE-WAY: a later mutation cannot un-skip it.
    /// </para>
    /// <para>
    /// ORDINALS ARE NEVER SUPPLIED BY THE CALLER — the service assigns them, prefix first. That is what makes
    /// a duplicate ordinal, a negative ordinal, a non-contiguous ordinal and a reorder across the settled
    /// boundary structurally impossible rather than validated, and it is why the validator below is short.
    /// </para>
    /// <para>
    /// Title/Intent/ExpectedArtifact are USER CONTENT: they are flattened, trimmed and capped at WRITE time
    /// (D3 item 9), which bounds every downstream prompt that interpolates them — the verify prompt, the
    /// replan prompt and both executors' step instruction — at one seam instead of five. Log them only via
    /// <c>SensitiveDebug</c>.
    /// </para>
    /// Raises <see cref="RunChanged"/> (step-less) ONLY on <see cref="PlanMutationOutcome.Applied"/>; the
    /// panel refreshes from that event and from nothing else, which is why this member lives here rather
    /// than on a separate validating service.
    /// </summary>
    Task<PlanMutationResult> ApplyPlanMutationAsync(
        Guid runId, IReadOnlyList<PlanStepEdit> pendingSteps, CancellationToken ct = default);

    /// <summary>For 1.4 UI/Flow; no consumers in 1.1.</summary>
    event EventHandler<AgentRunChangedEventArgs> RunChanged;
}
