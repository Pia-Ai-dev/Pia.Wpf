using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using Microsoft.Win32;

namespace Pia.Helpers;

/// <summary>
/// Detects an installed Obsidian and opens Pia's memory vault — or one note in it — there. Sibling of
/// <see cref="VsCodeLauncher"/>: best-effort, failures are swallowed (a missing install must never crash
/// the UI), and nothing is logged (vault paths are sensitive per CLAUDE.md). Lives in Helpers (not
/// Infrastructure) so Views/ViewModels may call it without breaking the layer rule.
/// </summary>
public static class ObsidianLauncher
{
    // Guards the lazy detection/icon caches; the startup prewarm resolves both on a background thread, so a
    // UI-thread reader must not see _resolved before _executable is published. Mirrors VsCodeLauncher's _gate.
    private static readonly object _gate = new();
    private static bool _resolved;
    private static string? _executable;

    private static bool _iconResolved;
    private static ImageSource? _icon;

    /// <summary>True when an installed Obsidian (<c>Obsidian.exe</c>) could be located.</summary>
    public static bool IsAvailable => ResolveExecutable() is not null;

    /// <summary>True when <paramref name="path"/> is a markdown note — the only thing Obsidian opens. Pure.</summary>
    public static bool IsMarkdownNote(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var ext = Path.GetExtension(path);
        return ext.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".markdown", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Opens <paramref name="vaultRoot"/> as a vault. No-op when Obsidian is absent or the path is empty.</summary>
    public static void OpenVault(string? vaultRoot) => Launch(BuildUri(vaultRoot, null));

    /// <summary>
    /// Opens one note, addressed vault-relative (<c>memory/topics/foo.md</c>). Obsidian resolves the note
    /// itself, so a section anchor is deliberately not passed — the file is the addressable unit.
    /// </summary>
    public static void OpenNote(string? vaultRoot, string? pathUnderRoot)
        => Launch(BuildUri(vaultRoot, pathUnderRoot));

    /// <summary>The Obsidian application icon (cached), or null when absent / extraction failed.</summary>
    public static ImageSource? TryGetIcon()
    {
        lock (_gate)
        {
            if (_iconResolved) return _icon;
            _iconResolved = true;
            return _icon = AppIcon.TryLoad(ResolveExecutable()); // ResolveExecutable is reentrant on _gate
        }
    }

    private static void Launch(string? uri)
    {
        if (uri is null) return;
        var exe = ResolveExecutable();
        if (exe is null) return;

        try
        {
            // The exe with the URI as its argument, not a ShellExecute of the URI itself: a portable install
            // registers no obsidian:// handler, and this is exactly what the handler would have run anyway.
            Process.Start(new ProcessStartInfo(exe, $"\"{uri}\"") { UseShellExecute = false });
        }
        catch
        {
            // Obsidian vanished mid-session / launch failed — swallow; the button stays so the user can retry.
        }
    }

    // Resolved per call, not cached: the user can add the vault to Obsidian while Pia is running, and that
    // flips which of the two URI forms below actually works.
    private static string? BuildUri(string? vaultRoot, string? pathUnderRoot)
        => string.IsNullOrWhiteSpace(vaultRoot)
            ? null
            : ComposeUri(vaultRoot, pathUnderRoot, ResolveVaultId(vaultRoot));

    /// <summary>
    /// The <c>obsidian://open</c> URI for a vault or one note in it. With a <paramref name="vaultId"/> (the
    /// vault is registered) it addresses the vault by id, which survives a folder rename; without one it
    /// falls back to an absolute path, which still works when the folder sits inside a vault the user added.
    /// </summary>
    internal static string ComposeUri(string vaultRoot, string? pathUnderRoot, string? vaultId)
    {
        var file = NormalizeRelative(pathUnderRoot);

        if (!string.IsNullOrEmpty(vaultId))
        {
            var uri = $"obsidian://open?vault={Uri.EscapeDataString(vaultId)}";
            return file is null ? uri : $"{uri}&file={Uri.EscapeDataString(file)}";
        }

        var absolute = file is null
            ? vaultRoot
            : Path.Combine(vaultRoot, file.Replace('/', Path.DirectorySeparatorChar));
        return $"obsidian://open?path={Uri.EscapeDataString(absolute)}";
    }

    private static string? NormalizeRelative(string? pathUnderRoot)
    {
        if (string.IsNullOrWhiteSpace(pathUnderRoot)) return null;
        var normalized = pathUnderRoot.Trim().Replace('\\', '/').TrimStart('/');
        return normalized.Length == 0 ? null : normalized;
    }

    private static string? ResolveVaultId(string vaultRoot)
    {
        try
        {
            var registry = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "obsidian", "obsidian.json");
            return File.Exists(registry) ? FindVaultId(File.ReadAllText(registry), vaultRoot) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The key Obsidian filed <paramref name="vaultRoot"/> under in its <c>obsidian.json</c> vault list, or
    /// null when the vault is not registered (or the file is unreadable/malformed).
    /// </summary>
    internal static string? FindVaultId(string json, string vaultRoot)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("vaults", out var vaults)
                || vaults.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var vault in vaults.EnumerateObject())
            {
                if (vault.Value.ValueKind != JsonValueKind.Object) continue;
                if (!vault.Value.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                if (SamePath(path.GetString(), vaultRoot)) return vault.Name;
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string? ResolveExecutable()
    {
        lock (_gate)
        {
            if (_resolved) return _executable;
            _resolved = true;
            return _executable = ProbeExecutable();
        }
    }

    private static string? ProbeExecutable()
    {
        // 1. Known install locations (user install first, then machine-wide).
        foreach (var candidate in CandidatePaths())
        {
            if (SafeExists(candidate)) return candidate;
        }

        // 2. Registry App Paths (bonus — not all installs register here).
        var fromRegistry = ResolveAppPath("Obsidian.exe");
        if (SafeExists(fromRegistry)) return fromRegistry;

        // 3. The obsidian:// handler — the one probe a relocated or portable install still answers. Obsidian
        //    adds no directory to PATH, so there is no where.exe step like VsCodeLauncher's.
        var fromProtocol = ExtractExecutable(ResolveProtocolCommand());
        return SafeExists(fromProtocol) ? fromProtocol : null;
    }

    private static IEnumerable<string> CandidatePaths()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        if (!string.IsNullOrEmpty(local))
            yield return Path.Combine(local, "Programs", "obsidian", "Obsidian.exe");
        if (!string.IsNullOrEmpty(programFiles))
            yield return Path.Combine(programFiles, "Obsidian", "Obsidian.exe");
        if (!string.IsNullOrEmpty(programFilesX86))
            yield return Path.Combine(programFilesX86, "Obsidian", "Obsidian.exe");
    }

    /// <summary>
    /// Reads <c>SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\&lt;exe&gt;</c> (HKLM first, then HKCU),
    /// or null if absent/unreadable. Mirrors the shape used by <see cref="VsCodeLauncher"/> and GitLocator.
    /// </summary>
    private static string? ResolveAppPath(string exe)
    {
        const string subKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\";
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var key = root.OpenSubKey(subKey + exe);
                if (key?.GetValue(null) is string path && !string.IsNullOrWhiteSpace(path))
                    return path.Trim('"');
            }
            catch
            {
                // Registry access denied / malformed — fall through to the next root, then null.
            }
        }
        return null;
    }

    private static string? ResolveProtocolCommand()
    {
        try
        {
            // HKCR is the merged HKLM+HKCU class view, so this single read covers a per-user install too.
            using var key = Registry.ClassesRoot.OpenSubKey(@"obsidian\shell\open\command");
            return key?.GetValue(null) as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The executable out of a registered shell command (<c>"C:\…\Obsidian.exe" "%1"</c>).</summary>
    internal static string? ExtractExecutable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var trimmed = command.Trim();

        if (trimmed[0] == '"')
        {
            var closing = trimmed.IndexOf('"', 1);
            return closing > 1 ? trimmed[1..closing] : null;
        }

        var exe = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe < 0 ? null : trimmed[..(exe + 4)];
    }

    private static bool SafeExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return File.Exists(path); }
        catch { return false; }
    }
}
