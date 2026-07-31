using System.IO;

namespace Pia.Infrastructure;

/// <summary>
/// Path constants for the assistant files folder and its derived memory vault. The files folder is
/// the user-relocatable sandbox root (default <see cref="DefaultRoot"/>); the vault always lives at
/// <see cref="VaultRootFor"/> beneath it. <see cref="LegacyWorkdir"/> is retained only so
/// <see cref="SensitivePathGuard"/> keeps carving the pre-relocation default out of the otherwise-
/// blocked <c>%LOCALAPPDATA%\Pia</c> for migrate-in-place users.
/// </summary>
public static class AssistantWorkspace
{
    /// <summary>Vault subfolder name under the assistant files folder.</summary>
    public const string VaultSubfolderName = "Vault";

    /// <summary>
    /// Default assistant files folder for new installs: <c>%USERPROFILE%\Documents\Pia Assistant</c>.
    /// Built from the literal profile + "Documents" (not SpecialFolder.MyDocuments) so an OneDrive-
    /// redirected Documents cannot push the default outside the profile and break the "under
    /// %USERPROFILE%" rule.
    /// </summary>
    public static string DefaultRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Documents", "Pia Assistant");

    /// <summary>
    /// Legacy default workdir (<c>%LOCALAPPDATA%\Pia\workdir</c>). Retained ONLY so
    /// <see cref="SensitivePathGuard"/> keeps carving it out of the otherwise-blocked
    /// <c>%LOCALAPPDATA%\Pia</c> for migrate-in-place users whose folder stays there. New installs use
    /// <see cref="DefaultRoot"/>, which is outside every blocked root and needs no carve-out.
    /// </summary>
    public static string LegacyWorkdir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Pia", "workdir");

    /// <summary>The vault root derived from an assistant files folder: <c>&lt;folder&gt;\Vault</c>.</summary>
    public static string VaultRootFor(string filesFolder) =>
        Path.Combine(filesFolder, VaultSubfolderName);

    /// <summary>
    /// Base directory for every per-run agent workspace: <c>%LOCALAPPDATA%\Pia\runs</c>. Lives here, beside
    /// <see cref="LegacyWorkdir"/>, because it is the SECOND island <see cref="SensitivePathGuard"/> has to
    /// carve out of the otherwise-blocked <c>%LOCALAPPDATA%\Pia</c> tree — the guard and the launcher must not
    /// be able to disagree about where it is (Batch 06 B1). <c>HeadlessRunLauncher</c> uses this as its default
    /// (an injected override keeps tests off the real user folder), and <c>RunWorkspaceService</c> uses it too.
    /// </summary>
    public static string RunsRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pia", "runs");
}
