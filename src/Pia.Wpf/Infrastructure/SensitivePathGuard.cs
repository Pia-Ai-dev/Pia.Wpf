using System.IO;

namespace Pia.Infrastructure;

/// <summary>
/// Rejects writes to paths that must never be touched even when they fall inside a (possibly
/// permissively-configured) sandbox: Pia's own data/config/DB under <c>%LOCALAPPDATA%\Pia</c>
/// and true system / credential directories. Load-bearing now that the path resolver accepts
/// in-base absolute paths. Kept deliberately tight to avoid false positives — this is a denylist
/// of well-known dangerous roots, not a general allowlist.
/// <para>
/// One island is carved back out of the otherwise-blocked <c>%LOCALAPPDATA%\Pia</c>: the agent's
/// default scratch workdir (<see cref="AssistantWorkspace.DefaultWorkdir"/>) lives there and IS
/// the sandbox, so blocking it would dead-end every file tool out of the box. The carve-out is
/// the exact workdir subtree only — Pia's DB/config/logs siblings stay blocked, and widening the
/// sandbox to <c>%LOCALAPPDATA%\Pia</c> itself still can't reach them.
/// </para>
/// </summary>
public static class SensitivePathGuard
{
    private static readonly string[] BlockedRoots = BuildBlockedRoots();
    private static readonly string[] AllowedExceptions = BuildAllowedExceptions();

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

        // Carve-outs win over the denylist: an allowed island (the workdir) sits inside a blocked
        // root, so it must be checked first or the StartsWith below would re-block it.
        foreach (var allowed in AllowedExceptions)
        {
            if (string.IsNullOrEmpty(allowed)) continue;
            var allowedWithSep = SafeFolderPath.WithTrailingSeparator(allowed);
            if (full.StartsWith(allowedWithSep, StringComparison.OrdinalIgnoreCase) ||
                full.Equals(allowed, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        foreach (var root in BlockedRoots)
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

    private static string[] BuildBlockedRoots()
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

        return roots
            .Where(r => !string.IsNullOrEmpty(r))
            .Select(r => SafeCanonical(r!))
            .Where(r => r is not null)
            .Select(r => r!)
            .ToArray();
    }

    /// <summary>
    /// Islands carved out of an otherwise-blocked root. Canonicalized through the SAME
    /// <see cref="SafeCanonical"/> path as <see cref="BuildBlockedRoots"/> so the prefix match in
    /// <see cref="IsBlocked"/> (which compares against the resolver's already-canonicalized path)
    /// lines up. Currently just the agent's default scratch workdir under <c>%LOCALAPPDATA%\Pia</c>.
    /// </summary>
    private static string[] BuildAllowedExceptions()
    {
        var canonical = SafeCanonical(AssistantWorkspace.DefaultWorkdir);
        return canonical is null ? [] : [canonical];
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
