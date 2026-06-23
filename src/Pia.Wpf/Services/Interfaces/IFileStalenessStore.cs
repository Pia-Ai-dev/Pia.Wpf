namespace Pia.Services.Interfaces;

/// <summary>
/// Tracks the last-observed modification time of files read during a coding task
/// so a subsequent write/delete can detect out-of-band changes (stale-edit guard).
/// Keyed by <c>(taskId, canonicalizedResolvedPath)</c>. Callers pass the resolved
/// (canonicalized) path produced by the path resolver, never the model-supplied string.
/// Implementations are thread-safe.
/// </summary>
public interface IFileStalenessStore
{
    /// <summary>
    /// Records the modification time observed for <paramref name="resolvedPath"/> when it was
    /// read during the task identified by <paramref name="taskId"/>. Overwrites any prior record
    /// for the same key.
    /// </summary>
    void RecordRead(Guid taskId, string resolvedPath, DateTime mtimeUtc);

    /// <summary>
    /// Returns <c>true</c> when the file at <paramref name="resolvedPath"/> appears to have changed
    /// since the last <see cref="RecordRead"/> for the same <paramref name="taskId"/> — i.e. the
    /// recorded mtime differs from <paramref name="currentMtimeUtc"/>.
    /// When no read was recorded for the key, returns <c>false</c> (unknown is treated as not-stale):
    /// the staleness guard only fires on a positive change signal, and an unread file is not something
    /// the model is expected to have based an edit on.
    /// </summary>
    bool CheckStaleness(Guid taskId, string resolvedPath, DateTime currentMtimeUtc);
}
