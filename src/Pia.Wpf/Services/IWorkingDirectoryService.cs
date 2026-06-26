namespace Pia.Services;

/// <summary>
/// Enumerates folders inside the assistant-files sandbox for the per-chat working-directory
/// picker. All paths are RELATIVE to the sandbox root (forward slashes); only sandbox-contained,
/// non-sensitive folders are surfaced.
/// </summary>
public interface IWorkingDirectoryService
{
    /// <summary>
    /// Immediate child folder NAMES under the sandbox root + <paramref name="relativeParent"/>
    /// (ordinal-ignore-case sorted). Names that escape containment or are blocked by
    /// <see cref="Pia.Infrastructure.SensitivePathGuard"/> are filtered out. Returns an empty
    /// list on any error / missing folder / unconfigured sandbox.
    /// </summary>
    IReadOnlyList<string> ListSubfolders(string relativeParent);
}
