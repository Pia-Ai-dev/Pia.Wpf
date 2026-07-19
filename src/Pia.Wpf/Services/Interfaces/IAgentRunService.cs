using Microsoft.Extensions.AI;
using Pia.Models;

namespace Pia.Services.Interfaces;

/// <summary>Parameters for creating a new <see cref="AgentRun"/>.</summary>
public sealed record AgentRunCreateRequest(
    Guid ChatId,
    RunShape Shape,
    AgentRunTrigger Trigger,
    Guid? TriggerRef = null,
    Guid? OwnerDeviceId = null,
    string? Goal = null);

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
    /// Settle every crash-recoverable run (Planning/Running/Verifying — a crash / forced-exit leftover) to
    /// <see cref="AgentRunState.Cancelled"/> so none dangles <see cref="AgentRunState.Running"/> across app
    /// sessions (§17.5/G-4). <see cref="AgentRunState.WaitingForInput"/>/<see cref="AgentRunState.Paused"/>
    /// are a DELIBERATE parked state (budget pause) and are EXCLUDED — a parked run survives restart
    /// resumable. Bulk, silent (raises no <see cref="RunChanged"/> — these are historical leftovers, not
    /// live transitions, so the Flow surface must not re-publish for them at startup). Returns the number
    /// of runs settled.
    /// </summary>
    Task<int> FailInterruptedRunsAsync(CancellationToken ct = default);

    Task<AgentRun?> GetAsync(Guid runId, CancellationToken ct = default);

    Task<IReadOnlyList<AgentRun>> GetByChatAsync(Guid chatId, CancellationToken ct = default);

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
