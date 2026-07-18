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
