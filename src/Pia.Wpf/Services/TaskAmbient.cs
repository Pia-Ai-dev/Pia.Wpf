namespace Pia.Services;

/// <summary>How a file was touched during a turn — reported through <see cref="TaskContext.OnFileTouched"/>.</summary>
public enum FileTouchKind { Read, Created, Updated }

/// <summary>A single file the turn's tools touched, carrying the resolved absolute path.</summary>
public readonly record struct FileTouch(string AbsolutePath, FileTouchKind Kind);

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
/// <param name="OnFileTouched">
/// Optional per-turn sink wired by <c>ChatSession.RunTurnAsync</c> so the file tools can report each
/// file they read/write to the active assistant message (drives the open-file chips). The write path
/// captures this at prepare time (ambient flow is not guaranteed inside the deferred execute closure).
/// </param>
/// <param name="WorkspaceRoot">
/// Absolute per-run base root for an unattended (headless) run — the run's isolated
/// <c>%LOCALAPPDATA%\Pia\runs\&lt;runId&gt;</c> sandbox (§17.2). When set, <c>FilesToolHandler</c> resolves
/// every file operation against THIS root instead of the interactive assistant-files folder, so all
/// containment (<c>..</c>/absolute/symlink) rejections re-anchor to the run workspace (G-1).
/// Null = the interactive sandbox (existing behavior).
/// </param>
public readonly record struct TaskContext(
    Guid? TaskId,
    string? WorkingSubpath,
    Action<FileTouch>? OnFileTouched = null,
    string? WorkspaceRoot = null);

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
