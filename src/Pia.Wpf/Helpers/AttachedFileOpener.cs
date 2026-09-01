using Pia.Models;
using Pia.Services;

namespace Pia.Helpers;

/// <summary>Opens a saved message attachment. Shared by the live chat and the history inspector, which
/// each own their own command but resolve the sandbox-relative path the same way.</summary>
public static class AttachedFileOpener
{
    public static void Open(AttachedFileRef? file, IAttachedFileStore? store) =>
        ShellLauncher.OpenFile(Resolve(file, store));

    public static void Reveal(AttachedFileRef? file, IAttachedFileStore? store) =>
        ShellLauncher.RevealInExplorer(Resolve(file, store));

    private static string? Resolve(AttachedFileRef? file, IAttachedFileStore? store) =>
        file?.SavedRelativePath is { } relative ? store?.ResolveAbsolute(relative) : null;
}
