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

    /// <summary>
    /// Validates <paramref name="relativePath"/> as a sandbox-contained subfolder, CREATES it if
    /// missing, and returns the normalized relative path (forward slashes). Used both to resolve
    /// the configured default working directory for new chats and to validate the value typed into
    /// the settings picker.
    /// <list type="bullet">
    /// <item>null / whitespace input =&gt; <c>""</c> (the sandbox root; nothing is created).</item>
    /// <item>a valid, contained, non-sensitive relative path =&gt; the folder is created and its
    /// normalized relative path returned.</item>
    /// <item><c>null</c> when the input is rooted/UNC, escapes the sandbox via <c>..</c>, resolves
    /// to a blocked/sensitive folder, the sandbox is unconfigured, or creation fails.</item>
    /// </list>
    /// </summary>
    string? EnsureSubfolder(string? relativePath);
}
