using System;
using System.Linq;

namespace Pia.Services;

/// <summary>
/// Shortens absolute paths by replacing well-known Windows folder roots with their
/// environment-variable equivalents (e.g. <c>C:\Users\me\AppData\Roaming\Pia\x</c> →
/// <c>%APPDATA%\Pia\x</c>) and expands them back. Longest matching root wins so that
/// <c>%APPDATA%</c> is preferred over <c>%USERPROFILE%</c>.
/// </summary>
public static class PathShortener
{
    private static readonly (string Var, string Path)[] KnownRoots =
    [
        ("APPDATA",      Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)),
        ("LOCALAPPDATA", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
        ("USERPROFILE",  Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
    ];

    public static string Shorten(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;

        var matches = KnownRoots
            .Where(r => !string.IsNullOrEmpty(r.Path)
                        && path.StartsWith(r.Path, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Path.Length)
            .ToList();

        if (matches.Count == 0) return path;

        var best = matches[0];
        var remainder = path.Substring(best.Path.Length);
        return $"%{best.Var}%{remainder}";
    }

    public static string Expand(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        return Environment.ExpandEnvironmentVariables(path);
    }
}
