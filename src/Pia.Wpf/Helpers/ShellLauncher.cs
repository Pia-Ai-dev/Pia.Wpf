using System.Diagnostics;
using System.IO;

namespace Pia.Helpers;

/// <summary>
/// Opens local files/folders via the Windows shell. Mirrors <c>PiaSourceChip</c>'s URL-open behavior:
/// best-effort, failures are swallowed (a missing handler or a since-deleted file must never crash the
/// UI), and nothing is logged (file paths are sensitive per CLAUDE.md). Lives in Helpers (not
/// Infrastructure) so ViewModels may call it without breaking the layer rule.
/// </summary>
public static class ShellLauncher
{
    /// <summary>Types whose shell "open" verb runs, installs or follows something instead of showing it; an
    /// assistant-written one is revealed in Explorer. Documents and source (.html, .md, .cs, .py) still open.</summary>
    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".scr", ".pif", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe",
        ".js", ".jse", ".wsf", ".wsh", ".hta", ".msi", ".msp", ".cpl", ".jar", ".lnk",
        ".reg", ".scf", ".inf", ".gadget", ".application", ".msc",
        ".appref-ms", ".url", ".website", ".settingcontent-ms", ".library-ms", ".searchconnector-ms",
        ".chm", ".pyw", ".diagcab", ".rdp", ".theme", ".themepack", ".msu", ".msix", ".msixbundle",
        ".appx", ".appxbundle", ".wsc", ".sct", ".ws", ".psc1", ".xbap", ".jnlp",
    };

    internal static bool IsExecutable(string path) => ExecutableExtensions.Contains(Path.GetExtension(path));

    /// <summary>Opens a file with its default application, or reveals it when <see cref="IsExecutable"/>.</summary>
    public static void OpenFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        if (IsExecutable(path))
        {
            RevealInExplorer(path);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            // No default handler / file vanished — swallow; the chip stays so the user can retry.
        }
    }

    /// <summary>
    /// Reveals a file in Explorer (selected), opens the folder when given a directory, or falls back to
    /// the parent directory when the file is gone.
    /// </summary>
    public static void RevealInExplorer(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path))
            {
                // /select, highlights the file inside its folder. The path is canonicalized with
                // backslashes upstream, so the quoted form is correct on Windows.
                Process.Start("explorer.exe", $"/select,\"{path}\"");
                return;
            }

            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return;
            }

            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                Process.Start(new ProcessStartInfo(parent) { UseShellExecute = true });
        }
        catch
        {
            // Explorer missing / path vanished — swallow.
        }
    }
}
