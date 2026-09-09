namespace Pia.Services.Screen;

/// <summary>What the matcher sees of a window.</summary>
public readonly record struct AllowlistWindow(string ProcessName, string Title);

public enum AllowlistVerdict
{
    /// <summary>Exactly one open window matches an entry, so the target is unambiguous.</summary>
    Allowed,

    NotListed,

    /// <summary>Every matching entry matches more than one open window, so no entry names a single target.</summary>
    Ambiguous,
}

public readonly record struct AllowlistResolution(
    AllowlistVerdict Verdict,
    ScreenCaptureAllowlistEntry? Entry,
    int MatchCount);

/// <summary>Pure matching for the allowlist: a program name, a case-insensitive title substring, and the rule
/// that an entry authorises a capture only while it resolves to exactly one open window.</summary>
public static class ScreenCaptureAllowlistMatcher
{
    /// <summary>Trims and drops a trailing ".exe" in any case — <c>Process.ProcessName</c> carries no extension
    /// but users type one.</summary>
    public static string NormalizeProcessName(string? processName)
    {
        var trimmed = (processName ?? string.Empty).Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^4].Trim()
            : trimmed;
    }

    public static bool Matches(ScreenCaptureAllowlistEntry entry, string? processName, string? title)
    {
        var wanted = NormalizeProcessName(entry.ProcessName);
        // A blank name would otherwise match an elevated window whose process enumeration returned nothing.
        if (wanted.Length == 0)
            return false;

        if (!wanted.Equals(NormalizeProcessName(processName), StringComparison.OrdinalIgnoreCase))
            return false;

        var pattern = entry.TitleContains.Trim();
        return pattern.Length == 0
            || (title ?? string.Empty).Contains(pattern, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolve a candidate against the list. <paramref name="visibleWindows"/> must contain the candidate; the
    /// count it produces is what makes "a window named in advance" mean one window rather than a family of them.
    /// </summary>
    public static AllowlistResolution Resolve(
        IReadOnlyList<ScreenCaptureAllowlistEntry> entries,
        AllowlistWindow candidate,
        IReadOnlyList<AllowlistWindow> visibleWindows)
    {
        var matching = entries
            .Where(e => Matches(e, candidate.ProcessName, candidate.Title))
            .ToList();

        if (matching.Count == 0)
            return new AllowlistResolution(AllowlistVerdict.NotListed, null, 0);

        if (!visibleWindows.Any(w => w == candidate))
            return new AllowlistResolution(AllowlistVerdict.NotListed, null, 0);

        var narrowest = int.MaxValue;
        foreach (var entry in matching)
        {
            var count = visibleWindows.Count(w => Matches(entry, w.ProcessName, w.Title));
            if (count == 1)
                return new AllowlistResolution(AllowlistVerdict.Allowed, entry, 1);
            if (count < narrowest)
                narrowest = count;
        }

        return new AllowlistResolution(AllowlistVerdict.Ambiguous, null, narrowest);
    }
}
