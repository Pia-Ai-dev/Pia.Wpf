namespace Pia.Services;

/// <summary>
/// Ambient "current turn's task id", flowing down a single logical async turn
/// via <see cref="AsyncLocal{T}"/>. Each <c>RunTurnAsync</c> sets this to its own
/// session's <see cref="System.Guid"/> <c>Id</c> for the duration of the turn so
/// tool handlers (the reader is <c>FilesToolHandler</c>) can key per-task state
/// without any parameter plumbing — even when two turns interleave on the shared
/// UI thread (each <c>await</c> continuation carries its own <c>ExecutionContext</c>,
/// so the value is isolated per logical turn).
///
/// The payload is <c>Guid?</c> because <c>ChatSession.Id</c> is nullable: the manager
/// assigns it before dispatch, but direct test callers bypass that path, so a
/// non-nullable payload would NRE. Readers treat null as <c>Guid.Empty</c>.
/// </summary>
public static class TaskAmbient
{
    private static readonly AsyncLocal<Guid?> _current = new();

    /// <summary>The current logical turn's task id, or null outside any turn.</summary>
    public static Guid? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }
}
