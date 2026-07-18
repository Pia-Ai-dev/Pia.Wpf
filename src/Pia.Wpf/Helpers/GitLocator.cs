using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace Pia.Helpers;

/// <summary>
/// Detects an installed Git and pins the resolved <c>git.exe</c> path, the git analogue of
/// <see cref="VsCodeLauncher"/>. Detection is resolved lazily and cached process-wide (git's install
/// location does not change while the app runs), and is prewarmed at startup off the UI thread.
///
/// <para>
/// Security: only a real <c>git.exe</c> is accepted — a <c>.cmd</c>/<c>.bat</c> shim (scoop/choco
/// wrappers) is refused, because <see cref="ProcessStartInfo.ArgumentList"/>'s argv-escaping safety
/// contract only holds for a real <c>.exe</c>; for a batch shim .NET switches to <c>cmd.exe</c>
/// escaping and can reopen a quoting-injection surface. Nothing here is logged (paths are sensitive
/// per CLAUDE.md).
/// </para>
/// </summary>
public static class GitLocator
{
    private static readonly object _gate = new();
    private static bool _resolved;
    private static string? _executable;

    /// <summary>
    /// Test seam: when set, replaces the real PATH/registry probe. The returned candidate still passes
    /// through <see cref="AcceptExecutable"/> (so a <c>.cmd</c> override is still refused). Set/reset
    /// only from tests, which serialize via a non-parallel collection.
    /// </summary>
    internal static Func<string?>? ProbeOverride;

    /// <summary>True when an installed <c>git.exe</c> could be located and pinned.</summary>
    public static bool IsAvailable => ResolveExecutable() is not null;

    /// <summary>The pinned real <c>git.exe</c> path, or null when git is not installed.</summary>
    public static string? Executable => ResolveExecutable();

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
        if (ProbeOverride is not null)
            return AcceptExecutable(ProbeOverride());

        // 1. PATH via `where.exe git.exe` — the git installer adds its cmd/ dir to PATH, and probing
        //    the explicit "git.exe" name (never bare "git") avoids ever resolving a git.cmd/git.bat shim.
        var fromPath = ResolveFromPath();
        if (AcceptExecutable(fromPath) is { } path) return path;

        // 2. Registry App Paths (bonus — not all installs register here).
        var fromRegistry = ResolveAppPath("git.exe");
        if (AcceptExecutable(fromRegistry) is { } reg) return reg;

        // 3. Known install locations.
        foreach (var candidate in CandidatePaths())
            if (AcceptExecutable(candidate) is { } known) return known;

        return null;
    }

    /// <summary>
    /// Returns <paramref name="candidate"/> only if it exists on disk and is a real <c>.exe</c>
    /// (never a <c>.cmd</c>/<c>.bat</c>/<c>.ps1</c> shim). Otherwise null. Pure enough to unit-test
    /// without git on the box.
    /// </summary>
    internal static string? AcceptExecutable(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return null;
        try
        {
            var ext = Path.GetExtension(candidate);
            if (!string.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase)) return null;
            return File.Exists(candidate) ? candidate : null;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> CandidatePaths()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        foreach (var root in new[] { programFiles, programFilesX86 })
        {
            if (string.IsNullOrEmpty(root)) continue;
            yield return Path.Combine(root, "Git", "cmd", "git.exe");
            yield return Path.Combine(root, "Git", "bin", "git.exe");
        }
        if (!string.IsNullOrEmpty(local))
        {
            yield return Path.Combine(local, "Programs", "Git", "cmd", "git.exe");
            yield return Path.Combine(local, "Programs", "Git", "bin", "git.exe");
        }
    }

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
            var psi = new ProcessStartInfo("where.exe", "git.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                // where.exe searches the working directory before PATH; anchor it to System32 so a
                // git.exe planted in the app's launch directory can't be pinned ahead of the real install.
                WorkingDirectory = Environment.SystemDirectory,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(2000);

            return output
                .Split('\n')
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.Length > 0);
        }
        catch
        {
            return null;
        }
    }

    // Test seam: force the cached result, or clear it so the next access re-probes. Tests that touch
    // these must run in the non-parallel "GitLocatorStatic" collection and reset in a finally.
    internal static void SetExecutableForTests(string? path)
    {
        lock (_gate)
        {
            _resolved = true;
            _executable = path;
        }
    }

    internal static void ResetForTests()
    {
        lock (_gate)
        {
            _resolved = false;
            _executable = null;
            ProbeOverride = null;
        }
    }
}
