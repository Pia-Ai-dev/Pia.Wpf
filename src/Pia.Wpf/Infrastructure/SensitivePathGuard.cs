using System.IO;
using Pia.Paths;

namespace Pia.Infrastructure;

/// <summary>
/// Rejects writes to paths that must never be touched even when they fall inside a (possibly
/// permissively-configured) sandbox: Pia's own data/config/DB under <c>%LOCALAPPDATA%\Pia</c>
/// and true system / credential directories. Load-bearing now that the path resolver accepts
/// in-base absolute paths. Kept deliberately tight to avoid false positives — this is a denylist
/// of well-known dangerous roots, not a general allowlist.
/// <para>
/// TWO islands are carved back out of the otherwise-blocked <c>%LOCALAPPDATA%\Pia</c>: the agent's
/// legacy default scratch workdir (<see cref="AssistantWorkspace.LegacyWorkdir"/>), kept for
/// migrate-in-place users whose folder stays there and IS the sandbox, so blocking it would dead-end
/// every file tool out of the box; and the per-run agent workspace root
/// (<see cref="AssistantWorkspace.RunsRoot"/>, Batch 06 B1) — every unattended run's isolated workspace
/// lives there, and it sits inside the same blocked root. Each carve-out is its exact subtree only —
/// Pia's DB/config/logs siblings stay blocked, and widening the sandbox to <c>%LOCALAPPDATA%\Pia</c>
/// itself still can't reach them.
/// </para>
/// </summary>
public static class SensitivePathGuard
{
    private static readonly object RootsGate = new();
    private static string[]? _blockedRoots;
    private static string[]? _allowedExceptions;
    private static string _rootsKey = string.Empty;

    /// <summary>
    /// Both arrays, rebuilt whenever the routed data directories move. They used to be <c>static readonly</c>,
    /// which froze them at type load — the trap <c>PiaPaths</c> exists to avoid, and the reason a test wanting
    /// a redirected profile had to use the REAL runs root. Keyed on the two routed roots because nothing else
    /// feeding these arrays can change in-process: <c>LOCALAPPDATA</c> and the credential directories do not
    /// move. Production therefore still builds once.
    /// </summary>
    private static (string[] Blocked, string[] Allowed) Roots()
    {
        var key = PiaPaths.LocalDataDirectory + " " + PiaPaths.RoamingDataDirectory;
        lock (RootsGate)
        {
            if (_blockedRoots is null || _allowedExceptions is null || _rootsKey != key)
            {
                _blockedRoots = BuildBlockedRoots();
                _allowedExceptions = BuildAllowedExceptions();
                _rootsKey = key;
            }

            return (_blockedRoots, _allowedExceptions);
        }
    }

    /// <summary>
    /// True when <paramref name="resolvedPath"/> (already §0.3-resolved + canonicalized) is inside a
    /// blocked root. <paramref name="reason"/> carries a short human-readable explanation when blocked.
    /// </summary>
    public static bool IsBlocked(string resolvedPath, out string reason)
    {
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(resolvedPath)) return false;

        string full;
        try { full = Path.GetFullPath(resolvedPath); }
        catch { return false; }

        var (blockedRoots, allowedExceptions) = Roots();

        // Carve-outs win over the denylist: an allowed island (the workdir) sits inside a blocked
        // root, so it must be checked first or the StartsWith below would re-block it.
        foreach (var allowed in allowedExceptions)
        {
            if (string.IsNullOrEmpty(allowed)) continue;
            var allowedWithSep = SafeFolderPath.WithTrailingSeparator(allowed);
            if (full.StartsWith(allowedWithSep, StringComparison.OrdinalIgnoreCase) ||
                full.Equals(allowed, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        foreach (var root in blockedRoots)
        {
            if (string.IsNullOrEmpty(root)) continue;
            var rootWithSep = SafeFolderPath.WithTrailingSeparator(root);
            if (full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) ||
                full.Equals(root, StringComparison.OrdinalIgnoreCase))
            {
                reason = "the path is inside a protected system or application data directory";
                return true;
            }
        }
        return false;
    }

    /// <summary>Internal so a test can assert what an override produces directly, without going through the
    /// cache in <see cref="Roots"/>.</summary>
    internal static string[] BuildBlockedRoots()
    {
        var roots = new List<string?>();

        void AddEnv(string var, string? sub = null)
        {
            var v = Environment.GetEnvironmentVariable(var);
            if (string.IsNullOrEmpty(v)) return;
            roots.Add(sub is null ? v : SafeCombine(v, sub));
        }

        // Pia's own data/config/DB.
        AddEnv("LOCALAPPDATA", "Pia");
        AddEnv("APPDATA", "Pia");

        // True system / credential directories.
        AddEnv("WINDIR");                                   // C:\Windows
        AddEnv("ProgramData", "Microsoft\\Crypto");         // machine keys
        AddEnv("APPDATA", "Microsoft\\Crypto");             // user keys
        AddEnv("APPDATA", "Microsoft\\Protect");            // DPAPI master keys
        AddEnv("USERPROFILE", ".ssh");                      // SSH private keys
        AddEnv("USERPROFILE", ".aws");                      // cloud credentials
        AddEnv("USERPROFILE", ".gnupg");

        var canonical = roots
            .Where(r => !string.IsNullOrEmpty(r))
            .Select(r => SafeCanonical(r!))
            .Where(r => r is not null)
            .Select(r => r!)
            .ToList();

        // The two entries above cover the real profile; these cover a redirected data directory, which is the
        // same category of secret. Canonicalized as an island rather than through SafeCanonical because a
        // throwaway data directory routinely does not exist yet when this array is built, and SafeCanonical
        // drops what it cannot resolve.
        foreach (var dataRoot in new[] { PiaPaths.LocalDataDirectory, PiaPaths.RoamingDataDirectory })
        {
            var island = CanonicalizeAllowedIsland(dataRoot);
            if (island is not null && !canonical.Contains(island, StringComparer.OrdinalIgnoreCase))
                canonical.Add(island);
        }

        return canonical.ToArray();
    }

    /// <summary>
    /// Islands carved out of an otherwise-blocked root. Two of them:
    /// <list type="bullet">
    /// <item>The pre-relocation default workdir under <c>%LOCALAPPDATA%\Pia</c> — kept as a back-compat
    /// carve-out for migrate-in-place users whose folder stays there. New installs use
    /// <see cref="AssistantWorkspace.DefaultRoot"/> (under Documents), which is outside every blocked
    /// root and needs no exception. Canonicalized through the SAME <see cref="SafeCanonical"/> path as
    /// <see cref="BuildBlockedRoots"/> so the prefix match in <see cref="IsBlocked"/> lines up.</item>
    /// <item><see cref="AssistantWorkspace.RunsRoot"/> — every unattended run's isolated workspace
    /// (Batch 06 B1). Canonicalized through <see cref="CanonicalizeAllowedIsland"/> instead (B2): unlike
    /// the workdir, this directory routinely does not exist yet when this array is built.</item>
    /// </list>
    /// The vault gets no entry here — full file-tool access by design, and that is still exactly true of an
    /// interactive turn. It is NO LONGER the whole story for an unattended one: Batch 06 B6 deliberately
    /// leaves the vault OUT of the tree copied into a run's isolated workspace (<c>MemoryService</c>, the
    /// vault watcher and the ingest indexer own that tree and write to it through their own paths), so inside
    /// an isolated run <c>list_files</c> simply will not show <c>Vault\</c> and the run reaches memory through
    /// the memory tools, which do not read <c>WorkspaceRoot</c> at all. That narrowing is a PROVISIONING
    /// decision in <c>RunWorkspaceService</c>, not an entry here — this guard's denylist is unchanged.
    /// </summary>
    private static string[] BuildAllowedExceptions()
    {
        var exceptions = new List<string>();

        var workdir = SafeCanonical(AssistantWorkspace.LegacyWorkdir);
        if (workdir is not null) exceptions.Add(workdir);

        var runsRoot = CanonicalizeAllowedIsland(AssistantWorkspace.RunsRoot);
        if (runsRoot is not null) exceptions.Add(runsRoot);

        return exceptions.ToArray();
    }

    /// <summary>
    /// Canonicalizes an allowed island even when it does not exist yet: walks up to the deepest EXISTING
    /// ancestor, canonicalizes THAT, and re-appends the missing tail. Deliberately NOT shared with
    /// <see cref="SafeCanonical"/>, because the two have opposite failure directions — a lexical BLOCKED root
    /// fails open (nothing resolves through a missing directory, so there is nothing to block), while a lexical
    /// ALLOWED island fails CLOSED: the prefix match against the resolver's canonical candidate misses and the
    /// island stays blocked, dead-ending every file tool in a run whose workspace has not been created yet
    /// (Batch 06 B2). <c>%LOCALAPPDATA%\Pia\runs</c> does not exist on a fresh install and this array is built
    /// once per process, so the missing-tail case is the NORMAL case, not an edge one. Returns null only when
    /// nothing on the path can be resolved (not even its root).
    /// </summary>
    internal static string? CanonicalizeAllowedIsland(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch { return null; }

        var existing = full;
        var tail = string.Empty;
        while (!Directory.Exists(existing))
        {
            var parent = Path.GetDirectoryName(existing);
            if (string.IsNullOrEmpty(parent) || parent == existing)
                return null; // nothing on the path can be resolved
            var name = Path.GetFileName(existing);
            tail = tail.Length == 0 ? name : Path.Combine(name, tail);
            existing = parent;
        }

        var canonicalExisting = SafeFolderPath.Canonicalize(existing);
        return tail.Length == 0 ? canonicalExisting : Path.Combine(canonicalExisting, tail);
    }

    private static string SafeCombine(string a, string b)
    {
        try { return Path.Combine(a, b); }
        catch { return a; }
    }

    private static string? SafeCanonical(string p)
    {
        try
        {
            var full = Path.GetFullPath(p);
            // Canonicalize the SAME way IsBlocked's incoming path is canonicalized (the resolver
            // junction/symlink-resolves it). Otherwise a reparse point on the way to a blocked root
            // would diverge the resolved candidate from the lexical blocked-root prefix and miss the
            // StartsWith. Only existing roots can be canonicalized; a non-existent root (e.g. no
            // ~/.ssh) stays lexical, which is still correct (nothing resolves through a missing dir).
            return Directory.Exists(full) ? SafeFolderPath.Canonicalize(full) : full;
        }
        catch { return null; }
    }
}
