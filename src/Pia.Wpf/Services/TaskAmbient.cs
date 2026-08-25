namespace Pia.Services;

/// <summary>How a file was touched during a turn — reported through <see cref="TaskContext.OnFileTouched"/>.</summary>
public enum FileTouchKind { Read, Created, Updated }

/// <summary>A single file the turn's tools touched, carrying the resolved absolute path.</summary>
public readonly record struct FileTouch(string AbsolutePath, FileTouchKind Kind);

/// <summary>What a read tool put in front of the model — reported through <see cref="TaskContext.OnSourceCited"/>.</summary>
public enum SourceCitationKind { VaultPage, Chat }

/// <summary>
/// One vault page or past chat a turn's read tools grounded the answer in. <paramref name="Target"/> is the
/// wikilink target (no <c>memory/</c> prefix) or the chat id — what a chip click has to resolve.
/// </summary>
public readonly record struct SourceCitation(
    SourceCitationKind Kind, string Target, string Label, string Meta);

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
/// <param name="ChatId">
/// The turn's CHAT id. Distinct from <paramref name="TaskId"/>, which is the RUN id on every run
/// surface, so a reader that must know which conversation it is in cannot use TaskId. Null = unknown.
/// </param>
/// <param name="OnSourceCited">
/// Optional per-turn sink, the <paramref name="OnFileTouched"/> twin for the vault and chat-history read
/// tools, so the pages and conversations an answer drew on surface as source chips. Null = collect nothing.
/// </param>
public readonly record struct TaskContext(
    Guid? TaskId,
    string? WorkingSubpath,
    Action<FileTouch>? OnFileTouched = null,
    string? WorkspaceRoot = null,
    Guid? ChatId = null,
    Action<SourceCitation>? OnSourceCited = null);

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
