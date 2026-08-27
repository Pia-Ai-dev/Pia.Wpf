namespace Pia.Services;

/// <summary>
/// The convention that keeps a run's own working notes out of the user's folder — and out of the run's later
/// searches. A model compensating for lost cross-step context writes scratch files; promoted, they litter the
/// working folder, and left visible they contaminate the run (BG3's config-todo.txt reported a TODO it had
/// written into its own env-pairs.md).
/// </summary>
internal static class RunScratchFolder
{
    internal const string Name = ".scratch";

    private const string Prefix = Name + "/";

    /// <summary>What the per-step instruction tells the model. Model-facing, so deliberately unlocalized.</summary>
    internal const string StepHint =
        "Put working notes that are not a deliverable under .scratch/ in the working folder: nothing there is "
        + "published, and list_files and search_files skip it — read_file and write_file on an explicit "
        + ".scratch/ path still work.";

    /// <summary>
    /// True for the folder itself and anything under it, given a path RELATIVE TO THE WORKING ROOT with
    /// either separator. Root-level only: a <c>docs/.scratch</c> the user made is theirs, not a run's.
    /// </summary>
    internal static bool Contains(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
            return false;

        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        return normalized.Equals(Name, StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
    }
}
