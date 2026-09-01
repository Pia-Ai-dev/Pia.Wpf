namespace Pia.Services;

/// <summary>
/// Copies a composer attachment into the assistant-files sandbox so it outlives the send, and maps
/// the stored relative path back to disk. Paths in and out are RELATIVE to the sandbox root
/// (forward slashes) — an attachment's original location is never persisted.
/// </summary>
public interface IAttachedFileStore
{
    /// <summary>
    /// Copies <paramref name="sourcePath"/> into <paramref name="workingDirectory"/> under the sandbox
    /// root and returns the copy's normalized relative path, or null when it could not be saved
    /// (sandbox unconfigured, target blocked or in the vault, source missing/unreadable). A source
    /// already inside the sandbox is not duplicated — its own relative path is returned. A name
    /// collision is suffixed; an existing file is never overwritten.
    /// </summary>
    string? SaveIntoWorkingDirectory(string sourcePath, string? workingDirectory);

    /// <summary>
    /// Absolute path for a sandbox-relative path. Composed, not probed: a since-deleted or
    /// since-relocated file still yields a path (the open is best-effort). Null only when the
    /// sandbox is unconfigured or the path fails containment.
    /// </summary>
    string? ResolveAbsolute(string? relativePath);
}
