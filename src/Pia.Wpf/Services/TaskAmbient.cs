namespace Pia.Services;

/// <summary>
/// Ambient context for the current logical async turn, carrying the facts tool handlers
/// (the reader is <c>FilesToolHandler</c>) need without any parameter plumbing — the
/// turn's task id and its per-chat working subpath, set together so the two can never
/// desync. <c>ChatSession.Id</c> is nullable (the manager assigns it before dispatch, but
/// direct test callers bypass that path), so <see cref="TaskContext.TaskId"/> stays
/// <c>Guid?</c>; readers treat a null TaskId as <see cref="System.Guid.Empty"/>.
/// </summary>
/// <param name="TaskId">The current turn's task (chat) id, or null on direct test callers.</param>
/// <param name="WorkingSubpath">
/// The active chat's working directory, RELATIVE to the assistant-files sandbox root
/// (forward slashes); null/empty = sandbox root.
/// </param>
public readonly record struct TaskContext(Guid? TaskId, string? WorkingSubpath);

/// <summary>
/// Flows the current turn's <see cref="TaskContext"/> down a single logical async turn via
/// <see cref="AsyncLocal{T}"/>. Each <c>RunTurnAsync</c> sets this for the duration of its
/// turn so tool handlers can key per-task state and resolve the effective working root —
/// even when two turns interleave on the shared UI thread (each <c>await</c> continuation
/// carries its own <c>ExecutionContext</c>, so the value is isolated per logical turn).
/// </summary>
public static class TaskAmbient
{
    private static readonly AsyncLocal<TaskContext?> _current = new();

    /// <summary>The current logical turn's context, or null outside any turn.</summary>
    public static TaskContext? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }
}
