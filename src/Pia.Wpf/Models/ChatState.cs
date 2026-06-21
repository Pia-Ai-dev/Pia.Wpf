namespace Pia.Models;

/// <summary>
/// Runtime state of a single assistant chat. Not persisted/synced — a
/// persisted-but-not-live chat maps to <see cref="Idle"/>.
/// </summary>
public enum ChatState
{
    /// <summary>No turn in flight; default for a persisted-but-not-live chat.</summary>
    Idle,

    /// <summary>A turn is streaming / tool-calling.</summary>
    Running,

    /// <summary>Blocked on an action-card confirmation (no timeout).</summary>
    WaitingForTool,

    /// <summary>A background turn finished with an unread result.</summary>
    Completed,

    /// <summary>The last turn ended in a handled error.</summary>
    Error,
}
