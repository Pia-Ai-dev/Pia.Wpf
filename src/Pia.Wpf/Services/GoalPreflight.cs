using System.Linq;

namespace Pia.Services;

/// <summary>Cheap local check to refuse a blatant-junk goal before any run is created.</summary>
public static class GoalPreflight
{
    /// <summary>
    /// True when <paramref name="goal"/> is blatant junk. Deliberately narrow — no whitespace AND 8 characters
    /// or fewer — so any real multi-word goal passes unconditionally regardless of length.
    /// </summary>
    public static bool IsRefused(string? goal)
    {
        var trimmed = goal?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) return false; // empty/whitespace-only is a different, already-handled case

        return trimmed.Length <= 8 && !trimmed.Any(char.IsWhiteSpace);
    }
}
