using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Pia.Helpers;

/// <summary>
/// Detects an installed Visual Studio Code and opens files in it. Companion to <see cref="ShellLauncher"/>:
/// best-effort, failures are swallowed (a missing install / since-deleted file must never crash the UI),
/// and nothing is logged (file paths are sensitive per CLAUDE.md). Lives in Helpers (not Infrastructure)
/// so Views/ViewModels may call it without breaking the layer rule.
///
/// Detection and the extracted icon are resolved lazily and cached process-wide — VS Code's install
/// location does not change while the app runs. Launching <c>Code.exe &lt;path&gt;</c> explicitly (rather
/// than shell-opening the file) means a script/config the assistant authored opens as text, never runs —
/// so this deliberately supports the <c>.ps1/.bat/.cmd</c> types <see cref="ShellLauncher.OpenFile"/> refuses.
/// </summary>
public static partial class VsCodeLauncher
{
    /// <summary>
    /// Common code / script / config / markup / text extensions VS Code is a sensible editor for. Binary
    /// and office-document types (images, .docx, …) are intentionally excluded — the chip's default-open
    /// and reveal buttons already cover those.
    /// </summary>
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csx", ".fs", ".vb", ".js", ".jsx", ".mjs", ".cjs", ".ts", ".tsx", ".py",
        ".java", ".kt", ".kts", ".go", ".rs", ".rb", ".php", ".swift", ".scala",
        ".c", ".cpp", ".cc", ".cxx", ".h", ".hpp", ".m", ".mm", ".lua", ".r", ".pl",
        ".sh", ".bash", ".zsh", ".ps1", ".psm1", ".bat", ".cmd", ".sql",
        ".html", ".htm", ".css", ".scss", ".sass", ".less", ".vue", ".svelte",
        ".xml", ".xaml", ".json", ".jsonc", ".yaml", ".yml", ".toml", ".ini", ".env", ".config",
        ".md", ".markdown", ".txt", ".csv", ".log", ".gitignore", ".dockerfile",
    };

    // Guards the lazy detection/icon caches. Since the startup prewarm resolves these on a background
    // thread, a UI-thread reader must not observe the "_resolved = true" flag before "_executable" is
    // published (which would return a false "VS Code absent" that sticks). Mirrors GitLocator's _gate.
    private static readonly object _gate = new();
    private static bool _resolved;
    private static string? _executable;

    private static bool _iconResolved;
    private static ImageSource? _icon;

    /// <summary>True when an installed VS Code (<c>Code.exe</c>) could be located.</summary>
    public static bool IsAvailable => ResolveExecutable() is not null;

    /// <summary>
    /// True when <paramref name="path"/> has an extension worth opening in VS Code (see
    /// <see cref="SupportedExtensions"/>). Pure — does not touch the filesystem or the install.
    /// </summary>
    public static bool IsSupportedFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var ext = Path.GetExtension(path);
        return !string.IsNullOrEmpty(ext) && SupportedExtensions.Contains(ext);
    }

    /// <summary>Opens <paramref name="path"/> in VS Code. No-op when VS Code is absent or the path is empty.</summary>
    public static void Open(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var exe = ResolveExecutable();
        if (exe is null) return;

        try
        {
            Process.Start(new ProcessStartInfo(exe, $"\"{path}\"") { UseShellExecute = false });
        }
        catch
        {
            // VS Code vanished mid-session / launch failed — swallow; the chip stays so the user can retry.
        }
    }

    /// <summary>
    /// The VS Code application icon extracted from the installed <c>Code.exe</c>, as a frozen
    /// <see cref="ImageSource"/> (cached). Null when VS Code is absent or extraction fails — callers should
    /// fall back to a generic glyph rather than hiding their button.
    /// </summary>
    public static ImageSource? TryGetIcon()
    {
        lock (_gate)
        {
            if (_iconResolved) return _icon;
            _iconResolved = true;

            var exe = ResolveExecutable(); // reentrant on _gate
            if (exe is null) return _icon = null;

            var large = new IntPtr[1];
            try
            {
                var count = ExtractIconEx(exe, 0, large, null, 1);
                if (count == 0 || large[0] == IntPtr.Zero) return _icon = null;

                var source = Imaging.CreateBitmapSourceFromHIcon(
                    large[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return _icon = source;
            }
            catch
            {
                return _icon = null;
            }
            finally
            {
                if (large[0] != IntPtr.Zero) DestroyIcon(large[0]);
            }
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
        // 1. Known install locations (load-bearing: user install first, then system).
        foreach (var candidate in CandidatePaths())
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate)) return candidate;
            }
            catch
            {
                // Malformed path — try the next candidate.
            }
        }

        // 2. Registry App Paths (bonus — not all installs register here).
        var fromRegistry = ResolveAppPath("Code.exe");
        if (!string.IsNullOrWhiteSpace(fromRegistry) && SafeExists(fromRegistry)) return fromRegistry;

        // 3. PATH fallback: the installer adds "<install>\bin" (containing code.cmd) to PATH; Code.exe
        //    sits one directory above bin.
        var fromPath = ResolveFromPath();
        if (!string.IsNullOrWhiteSpace(fromPath) && SafeExists(fromPath)) return fromPath;

        return null;
    }

    private static IEnumerable<string> CandidatePaths()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        if (!string.IsNullOrEmpty(local))
            yield return Path.Combine(local, "Programs", "Microsoft VS Code", "Code.exe");
        if (!string.IsNullOrEmpty(programFiles))
            yield return Path.Combine(programFiles, "Microsoft VS Code", "Code.exe");
        if (!string.IsNullOrEmpty(programFilesX86))
            yield return Path.Combine(programFilesX86, "Microsoft VS Code", "Code.exe");
    }

    /// <summary>
    /// Reads <c>SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\Code.exe</c> (HKLM first, then HKCU),
    /// or null if absent/unreadable. Mirrors the shape used by the MeetingAttendee browser resolver.
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

    private static string? ResolveFromPath()
    {
        try
        {
            var psi = new ProcessStartInfo("where.exe", "code.cmd")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(2000);

            var cmd = output
                .Split('\n')
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.Length > 0);
            if (string.IsNullOrEmpty(cmd)) return null;

            // cmd = "<install>\bin\code.cmd" -> Code.exe is the parent of "bin".
            var installRoot = Path.GetDirectoryName(Path.GetDirectoryName(cmd));
            return installRoot is null ? null : Path.Combine(installRoot, "Code.exe");
        }
        catch
        {
            return null;
        }
    }

    private static bool SafeExists(string path)
    {
        try { return File.Exists(path); }
        catch { return false; }
    }

    [LibraryImport("shell32.dll", EntryPoint = "ExtractIconExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint ExtractIconEx(
        string lpszFile, int nIconIndex, [Out] IntPtr[]? phiconLarge, [Out] IntPtr[]? phiconSmall, uint nIcons);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr hIcon);
}
