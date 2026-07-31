namespace Pia.Models;

/// <summary>
/// Shape of an agent run. Persisted as <c>int</c> — append-only, never reorder.
/// </summary>
public enum RunShape
{
    /// <summary>One implicit step: Running → Completed (today's byte-for-byte turn, wrapped).</summary>
    SingleTurn = 0,

    /// <summary>Decomposed into an ordered plan of <see cref="AgentStep"/>s (built in 1.2).</summary>
    Planned = 1,
}

/// <summary>
/// Provenance of a run. Persisted as <c>int</c> — append-only, never reorder. Provenance
/// metadata only; it is NOT the persist discriminator (that is the execution path — §16 R14).
/// </summary>
public enum AgentRunTrigger
{
    User = 0,
    Schedule = 1,
    Event = 2,
}

/// <summary>
/// Persisted superset of the runtime-only <c>ChatState</c>. Persisted as <c>int</c> —
/// append-only, never reorder.
/// </summary>
public enum AgentRunState
{
    Planning = 0,
    Running = 1,
    Verifying = 2,
    WaitingForInput = 3,
    Paused = 4,
    Completed = 5,
    Failed = 6,
    Cancelled = 7,

    /// <summary>
    /// Batch 07: a PARENT run parked while its child runs execute. NON-TERMINAL. Appended at 8 — never
    /// inserted, never renumbered — and deliberately ABOVE the terminal band, because the startup sweep is
    /// <c>WHERE State &lt; WaitingForInput(3)</c> and a parent awaiting children must survive a restart
    /// rather than be cancelled out from under its children's completed work (07 D8/D14). NOT
    /// <see cref="Paused"/>(4), which is reserved for Batch 08 live-steering. "Waiting on N children" is
    /// NOT stored on the run — the child ROWS are the marker (<c>WHERE ParentRunId=@p AND State &lt; 5</c>,
    /// 07 §0.4), which is why <c>TryBeginResumeAsync</c>'s unconditional <c>ExtraJson=NULL</c> on the claim
    /// is irrelevant here.
    /// <para>
    /// Because it sits above the terminal band, ANY range comparison over this enum now lies about it. Both
    /// of the two that existed are explicit sets instead (07 D8c): <c>AgentRunService.ApplyLedgerClock</c>'s
    /// <c>terminal</c> test, and <c>HeadlessRunLauncher.RunStartupSweepAsync</c>'s workspace-retention test
    /// (Batch 06 G4, which already treats an unknown state as non-terminal — do not add this member to it,
    /// or a live parent's workspace goes on a 7-day deletion clock).
    /// </para>
    /// </summary>
    WaitingForChildren = 8,
}

/// <summary>
/// Lifecycle status of a single step. Persisted as <c>int</c> — append-only, never reorder.
/// </summary>
public enum AgentStepStatus
{
    Pending = 0,
    Running = 1,
    Done = 2,
    Failed = 3,
    Skipped = 4,
}
