using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media;
using Microsoft.Win32;

namespace Pia.Helpers;

/// <summary>What Pia can do about a vault folder before opening it in Obsidian.</summary>
public enum VaultRegistrationState
{
    /// <summary>Obsidian already resolves it, exactly or as a folder inside a registered vault.</summary>
    Registered,

    /// <summary>Its registry is readable and does not list the folder, so Pia can merge an entry in.</summary>
    Registrable,

    /// <summary>No registry Pia can read — it cannot tell, and must not guess by writing one.</summary>
    Undetermined,
}

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

    private static string? _registryPathOverride;

    /// <summary>True when an installed Obsidian (<c>Obsidian.exe</c>) could be located.</summary>
    public static bool IsAvailable => ResolveExecutable() is not null;

    /// <summary>
    /// Whether Obsidian already resolves <paramref name="vaultRoot"/>, could be told to, or cannot be asked.
    /// The three answers line up one-to-one with what <see cref="BuildUri"/> can produce: a vault-id URI,
    /// nothing, and the path= form respectively.
    ///
    /// <para><see cref="VaultRegistrationState.Undetermined"/> is deliberately NOT folded into
    /// <see cref="VaultRegistrationState.Registrable"/>: Obsidian may keep its vault list where this build
    /// cannot see it (a portable install), and an id written into a file it never reads would make every
    /// later open fire a URI it rejects, with no way back to the path= form.</para>
    /// </summary>
    public static VaultRegistrationState GetRegistrationState(string? vaultRoot)
    {
        if (string.IsNullOrWhiteSpace(vaultRoot)) return VaultRegistrationState.Undetermined;

        var registry = ReadVaultRegistry();
        if (registry is null) return VaultRegistrationState.Undetermined;

        return FindVaultId(registry, vaultRoot) is not null || IsPathInsideAnyVault(registry, vaultRoot)
            ? VaultRegistrationState.Registered
            : VaultRegistrationState.Registrable;
    }

    /// <summary>
    /// True when an Obsidian process is currently running. <see cref="TryRegisterVault"/> must not be called
    /// while this is true: Obsidian holds its own in-memory copy of the same registry file and can overwrite
    /// a racing external write on its next save, corrupting every vault it lists, not just this one.
    /// </summary>
    public static bool IsObsidianRunning()
    {
        try
        {
            var processes = Process.GetProcessesByName("Obsidian");
            try { return processes.Length > 0; }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        catch
        {
            return true; // Can't tell — assume it might be running so a caller never risks the write.
        }
    }

    /// <summary>True when <paramref name="path"/> is a markdown note — the only thing Obsidian opens. Pure.</summary>
    public static bool IsMarkdownNote(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var ext = Path.GetExtension(path);
        return ext.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".markdown", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Opens <paramref name="vaultRoot"/> as a vault. No-op when Obsidian is absent or the path is empty.</summary>
    public static void OpenVault(string? vaultRoot) => Open(vaultRoot, null);

    /// <summary>
    /// Opens one note, addressed vault-relative (<c>memory/topics/foo.md</c>). Obsidian resolves the note
    /// itself, so a section anchor is deliberately not passed — the file is the addressable unit.
    /// </summary>
    public static void OpenNote(string? vaultRoot, string? pathUnderRoot) => Open(vaultRoot, pathUnderRoot);

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

    /// <summary>
    /// Obsidian has no URI or CLI action that registers a folder it has never seen as a vault — the user
    /// has to click "Open folder as vault" themselves at least once. So when neither URI form below could
    /// possibly resolve, this skips straight to a bare launch, landing on Obsidian's vault switcher, rather
    /// than firing a URI Obsidian is guaranteed to reject with its own "Vault not found" dialog. Putting the
    /// path where the user can paste it is the caller's job — it has to happen before the dialog that says so.
    /// </summary>
    private static void Open(string? vaultRoot, string? pathUnderRoot)
    {
        if (string.IsNullOrWhiteSpace(vaultRoot)) return;
        var exe = ResolveExecutable();
        if (exe is null) return;

        Launch(exe, BuildUri(vaultRoot, pathUnderRoot));
    }

    private static void Launch(string exe, string? uri)
    {
        try
        {
            // The exe with the URI as its argument, not a ShellExecute of the URI itself: a portable install
            // registers no obsidian:// handler, and this is exactly what the handler would have run anyway.
            var info = uri is null
                ? new ProcessStartInfo(exe) { UseShellExecute = false }
                : new ProcessStartInfo(exe, $"\"{uri}\"") { UseShellExecute = false };
            Process.Start(info);
        }
        catch
        {
            // Obsidian vanished mid-session / launch failed — swallow; the button stays so the user can retry.
        }
    }

    // Resolved per call, not cached: the user can add the vault to Obsidian while Pia is running, and that
    // flips which of the two URI forms below actually works — or whether either can, at all. One form per
    // GetRegistrationState answer, Undetermined included: with no evidence either way the path= form still
    // gets its chance instead of degrading every open to a bare launch.
    private static string? BuildUri(string vaultRoot, string? pathUnderRoot)
    {
        var registry = ReadVaultRegistry();
        if (registry is null) return ComposeUri(vaultRoot, pathUnderRoot, null);

        var vaultId = FindVaultId(registry, vaultRoot);
        if (vaultId is null && !IsPathInsideAnyVault(registry, vaultRoot)) return null;

        return ComposeUri(vaultRoot, pathUnderRoot, vaultId);
    }

    private static string? ReadVaultRegistry()
    {
        try
        {
            return File.Exists(VaultRegistryPath()) ? File.ReadAllText(VaultRegistryPath()) : null;
        }
        catch
        {
            return null;
        }
    }

    private static string VaultRegistryPath() => _registryPathOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "obsidian", "obsidian.json");

    /// <summary>Test seam: retargets Obsidian's registry file, restoring the real path on dispose.</summary>
    internal static IDisposable OverrideRegistryPathForTests(string path) => new RegistryPathOverride(path);

    private sealed class RegistryPathOverride : IDisposable
    {
        private readonly string? _previous;

        internal RegistryPathOverride(string path)
        {
            _previous = _registryPathOverride;
            _registryPathOverride = path;
        }

        public void Dispose() => _registryPathOverride = _previous;
    }

    /// <summary>
    /// Registers <paramref name="vaultRoot"/> in Obsidian's own vault list so <c>obsidian://open?vault=</c>
    /// resolves it immediately — there is no supported API for this, so it edits the registry file Obsidian
    /// itself reads on startup. The caller must have already confirmed <see cref="IsObsidianRunning"/> is
    /// false; this does not re-check, since consent and the running-check happen together at the call site.
    ///
    /// <para>False when the file is missing or unmergeable, both of which mean "don't touch it": creating one
    /// would be a guess at where Obsidian keeps its list, and rewriting one that will not parse would throw
    /// away vaults and settings Pia never put there.</para>
    /// </summary>
    public static bool TryRegisterVault(string vaultRoot)
    {
        try
        {
            var registry = VaultRegistryPath();
            if (!File.Exists(registry)) return false;

            var vaultId = RandomNumberGenerator.GetHexString(16, lowercase: true);
            var updated = AddVaultEntry(
                File.ReadAllText(registry), vaultId, vaultRoot, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (updated is null) return false;

            // Write-then-move rather than a direct write, so a crash mid-write leaves the old registry intact.
            var temp = registry + ".tmp";
            File.WriteAllText(temp, updated);
            File.Move(temp, registry, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Merges one vault entry into the registry JSON, leaving every other vault — and every other top-level
    /// key Obsidian keeps there — untouched. Null when the JSON is present but not a usable object, since
    /// replacing it wholesale is data loss, not a merge. Empty input is a fresh registry, not a failure:
    /// Obsidian leaves a zero-byte file behind if it is interrupted mid-write.
    /// </summary>
    internal static string? AddVaultEntry(string? existingJson, string vaultId, string vaultRoot, long timestampMs)
    {
        JsonObject root;
        if (string.IsNullOrWhiteSpace(existingJson))
        {
            root = new JsonObject();
        }
        else
        {
            try
            {
                if (JsonNode.Parse(existingJson) is not JsonObject parsed) return null;
                root = parsed;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        if (root["vaults"] is not JsonObject vaults)
        {
            vaults = new JsonObject();
            root["vaults"] = vaults;
        }

        vaults[vaultId] = new JsonObject { ["path"] = vaultRoot, ["ts"] = timestampMs };
        return root.ToJsonString();
    }

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

    /// <summary>
    /// True when <paramref name="path"/> is, or sits inside, any vault in the registry — what
    /// <c>obsidian://open?path=</c> actually needs, a looser bar than <see cref="FindVaultId"/>'s exact match.
    /// </summary>
    internal static bool IsPathInsideAnyVault(string json, string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("vaults", out var vaults)
                || vaults.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (var vault in vaults.EnumerateObject())
            {
                if (vault.Value.ValueKind != JsonValueKind.Object) continue;
                if (!vault.Value.TryGetProperty("path", out var vaultPath) || vaultPath.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                if (IsSameOrAncestor(vaultPath.GetString(), path)) return true;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private static bool IsSameOrAncestor(string? ancestor, string? path)
    {
        if (string.IsNullOrWhiteSpace(ancestor) || string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ancestor));
            var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return target.Equals(root, StringComparison.OrdinalIgnoreCase)
                || target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
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
