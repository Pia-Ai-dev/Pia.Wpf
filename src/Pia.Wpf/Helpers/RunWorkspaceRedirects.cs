using System.Collections.Concurrent;
using System.IO;
using Pia.Infrastructure;

namespace Pia.Helpers;

/// <summary>
/// Maps a file path recorded inside a run's isolated workspace to where that file ended up after promotion,
/// so an open-file chip still opens the right file once the workspace is gone (Batch 06 B14 / plan D8,
/// "resolve on open").
/// <para>
/// PROCESS-LOCAL and bounded on purpose. <c>FileRef</c> chips are NOT persisted — they live only on the
/// in-memory <c>AssistantMessage</c> and vanish on chat reload (Batch 06 §0.4) — so a redirect that outlived
/// the process would have no chip left to serve. Nothing here is user-authored: both roots are app-derived,
/// and <see cref="Record"/> REFUSES a workspace root that is not under
/// <see cref="AssistantWorkspace.RunsRoot"/>, so no model-supplied string can install a redirect.
/// <see cref="Resolve"/> does one <c>File.Exists</c> on the recorded path first, so DURING the run the chip
/// opens the workspace copy exactly as it does today.
/// </para>
/// <para>
/// No logging and no injected state, matching <see cref="ShellLauncher"/>'s contract in this same folder:
/// every string that passes through here is a path, i.e. user content, and there is no
/// <c>SensitiveError</c> helper to make a failure line safe.
/// </para>
/// </summary>
public static class RunWorkspaceRedirects
{
    /// <summary>
    /// How many promotions stay resolvable at once. A session shows chips from a handful of runs, and the
    /// registry is a convenience cache, not a record — evicting the oldest entry costs a chip an open, never
    /// a file.
    /// </summary>
    internal const int MaxEntries = 16;

    private readonly record struct Entry(string DestinationRoot, long Seq);

    /// <summary>Canonicalized workspace root → where that workspace promoted to. Case-insensitive because
    /// Windows paths are.</summary>
    private static readonly ConcurrentDictionary<string, Entry> _redirects = new(StringComparer.OrdinalIgnoreCase);

    private static long _sequence;

    /// <summary>Entry count, for the bound-is-really-enforced fact.</summary>
    internal static int Count => _redirects.Count;

    /// <summary>
    /// Records a promotion: <paramref name="workspaceRoot"/>'s files now live under
    /// <paramref name="destinationRoot"/> at the same relative paths. No-op unless
    /// <paramref name="workspaceRoot"/> resolves inside <see cref="AssistantWorkspace.RunsRoot"/>. Never
    /// throws.
    /// </summary>
    public static void Record(string workspaceRoot, string destinationRoot)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(workspaceRoot) || string.IsNullOrWhiteSpace(destinationRoot))
                return;

            // Canonicalized through the SAME SafeFolderPath.NormalizeWorkspaceRoot FilesToolHandler and
            // GitToolHandler resolve the ambient workspace root with (GetFullPath, then a real-path resolve
            // while the directory exists). The chip carries an absolute path built from THAT spelling, so a
            // key in any other spelling — e.g. the uncanonicalized RootFor(runId) the caller holds — would
            // miss the prefix match in Resolve and silently dead-end every post-promotion chip with nothing
            // failing. Recording happens inside the promotion walk, i.e. BEFORE the caller's teardown, which
            // is what makes the directory still exist here and the real-path resolve possible.
            var root = SafeFolderPath.NormalizeWorkspaceRoot(workspaceRoot);

            // The containment gate. Canonicalized through the same missing-tail helper the guard's allowed
            // islands use, because the runs root may not exist yet and a lexical comparison against a
            // real-path-resolved candidate fails closed.
            var runsRoot = SensitivePathGuard.CanonicalizeAllowedIsland(AssistantWorkspace.RunsRoot);
            if (runsRoot is null || !root.StartsWith(SafeFolderPath.WithTrailingSeparator(runsRoot), StringComparison.OrdinalIgnoreCase))
                return;

            _redirects[root] = new Entry(Path.GetFullPath(destinationRoot), Interlocked.Increment(ref _sequence));
            EvictPastCap();
        }
        catch
        {
            // Bookkeeping for a chip's convenience: a fault here costs one open, and must never surface on
            // the promotion path that called it.
        }
    }

    /// <summary>
    /// The path to open: the recorded one while it still exists, else the same relative path under the
    /// destination that workspace promoted to when THAT exists, else <paramref name="recordedPath"/>
    /// unchanged (so <see cref="ShellLauncher"/> no-ops on a since-deleted file exactly as it does today).
    /// Never throws.
    /// </summary>
    public static string? Resolve(string? recordedPath)
    {
        if (string.IsNullOrWhiteSpace(recordedPath))
            return recordedPath;

        try
        {
            // Phase 1 of plan D8: during the run the workspace copy is the file the chip means, even when a
            // redirect for this workspace is already recorded (a byte-identical file is "promoted" without
            // the workspace being gone yet).
            if (File.Exists(recordedPath))
                return recordedPath;

            if (_redirects.IsEmpty)
                return recordedPath;

            // Canonicalized against the RECORDED key's spelling, and it has to survive the leaf being gone —
            // which is the only case that gets here. CanonicalizeAllowedIsland walks up to the deepest
            // existing ancestor, real-path-resolves THAT and re-appends the missing tail, so a junction
            // anywhere on the way to %LOCALAPPDATA% cannot make the prefix match miss. In production the
            // chip's path is already canonical, so this is idempotent there.
            var full = SensitivePathGuard.CanonicalizeAllowedIsland(recordedPath) ?? Path.GetFullPath(recordedPath);
            foreach (var (root, entry) in _redirects)
            {
                var prefix = SafeFolderPath.WithTrailingSeparator(root);
                if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                var promoted = Path.Combine(entry.DestinationRoot, full[prefix.Length..]);
                if (File.Exists(promoted))
                    return promoted;
            }

            return recordedPath;
        }
        catch
        {
            return recordedPath;
        }
    }

    /// <summary>Drops the oldest entries until the cap holds. Bounds process-local state that nothing else
    /// ever clears — a long session with many runs must not accumulate redirects forever.</summary>
    private static void EvictPastCap()
    {
        while (_redirects.Count > MaxEntries)
        {
            string? oldestKey = null;
            var oldestSeq = long.MaxValue;
            foreach (var (key, entry) in _redirects)
            {
                if (entry.Seq >= oldestSeq)
                    continue;
                oldestSeq = entry.Seq;
                oldestKey = key;
            }

            if (oldestKey is null)
                return;

            _redirects.TryRemove(oldestKey, out _);
        }
    }
}
