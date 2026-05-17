using System.IO;

namespace Pia.Infrastructure;

/// <summary>
/// Sandboxes file access to a configured root directory. Every user-supplied
/// relative path must be resolved through <see cref="TryResolveInside"/> before
/// being passed to the filesystem.
/// </summary>
public static class SafeFolderPath
{
    /// <summary>
    /// Resolves <paramref name="userPath"/> against <paramref name="sandboxRoot"/>
    /// and verifies the result stays inside the root.
    /// Rejects: rooted paths (C:\..., \\server\...), invalid path characters,
    /// and any combination that escapes the root via "..\" traversal.
    /// </summary>
    public static bool TryResolveInside(string? sandboxRoot, string? userPath, out string resolved)
    {
        resolved = string.Empty;
        if (string.IsNullOrWhiteSpace(sandboxRoot)) return false;
        if (string.IsNullOrWhiteSpace(userPath)) return false;

        var trimmed = userPath.Trim();

        // Block absolute paths, UNC paths, drive-relative paths.
        if (Path.IsPathRooted(trimmed)) return false;
        if (trimmed.Contains('\0')) return false;
        if (trimmed.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;

        string fullRoot;
        try { fullRoot = Path.GetFullPath(sandboxRoot); }
        catch { return false; }

        if (!fullRoot.EndsWith(Path.DirectorySeparatorChar))
            fullRoot += Path.DirectorySeparatorChar;

        string full;
        try { full = Path.GetFullPath(Path.Combine(fullRoot, trimmed)); }
        catch { return false; }

        // Comparison must include the trailing separator so "/rootEvil" is not
        // accepted as being inside "/root".
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) return false;
        if (full.Length == fullRoot.Length - 1) return false; // resolved to root itself

        resolved = full;
        return true;
    }

    /// <summary>
    /// Returns true if <paramref name="sandboxRoot"/> is a usable directory.
    /// </summary>
    public static bool IsConfiguredAndExists(string? sandboxRoot)
    {
        if (string.IsNullOrWhiteSpace(sandboxRoot)) return false;
        try { return Directory.Exists(Path.GetFullPath(sandboxRoot)); }
        catch { return false; }
    }
}
