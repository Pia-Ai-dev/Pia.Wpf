using System.IO;
using Pia.Helpers;
using Pia.Infrastructure;
using Xunit;

namespace Pia.Tests.Helpers;

/// <summary>
/// Plan D8 / Batch 06 B14, both phases: a chip opened DURING the run must open the workspace copy, and the
/// same chip opened AFTER promotion must open the promoted copy.
/// <para>
/// Rooted at the REAL shape (<c>AssistantWorkspace.RunsRoot\&lt;guid&gt;</c>), not under
/// <c>Path.GetTempPath()</c> — <see cref="RunWorkspaceRedirects.Record"/>'s containment gate refuses anything
/// else, so a temp-rooted fixture would assert on a registry that installed nothing (plan R1's failure mode
/// exactly). Guid-named because <c>RunStartupSweepAsync</c> skips any directory name that is not a parseable
/// Guid, so a leaked fixture is swept as <c>run is null</c> on the next app start instead of living in the
/// developer's real runs folder forever.
/// </para>
/// <para>
/// Shares the <c>RunWorkspaceRedirectsStatic</c> collection with the tests that drive a real promotion,
/// because the registry is process-global: this class's cap fact deliberately overflows it, and evicting
/// another class's entry mid-fact would be a fixture-only failure.
/// </para>
/// </summary>
[Collection("RunWorkspaceRedirectsStatic")]
public sealed class RunWorkspaceRedirectsTests : IDisposable
{
    private readonly List<string> _dirs = [];

    /// <summary>A run-shaped workspace directory: inside the real runs root, so Record accepts it.</summary>
    private string NewWorkspace()
    {
        var dir = Path.Combine(AssistantWorkspace.RunsRoot, Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    private string NewDestination()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pia-redirect-dest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    private static string WriteFile(string root, string rel, string content = "x")
    {
        var full = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        foreach (var dir in _dirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// REGRESSION (phase 1 of plan D8). The recorded path wins while the file is still there, even though a
    /// redirect for that workspace is already installed — which is a real state: a byte-identical file counts
    /// as promoted before the workspace is torn down. Neutralize by resolving unconditionally and this reds.
    /// </summary>
    [Fact]
    public void Resolve_ReturnsTheRecordedPath_WhileItStillExists()
    {
        var ws = NewWorkspace();
        var dest = NewDestination();
        var inWorkspace = WriteFile(ws, Path.Combine("sub", "a.md"), "workspace");
        WriteFile(dest, Path.Combine("sub", "a.md"), "promoted");

        RunWorkspaceRedirects.Record(ws, dest);

        Assert.Equal(inWorkspace, RunWorkspaceRedirects.Resolve(inWorkspace));
    }

    /// <summary>
    /// REGRESSION (phase 2 of plan D8): the workspace is gone — promotion moved the file and the orchestrator
    /// tore the directory down — so the chip's recorded path must resolve to the same relative path under the
    /// destination. Neutralize by dropping the redirect lookup and this reds.
    /// </summary>
    [Fact]
    public void Resolve_RedirectsToThePromotedCopy_OnceTheWorkspaceIsGone()
    {
        var ws = NewWorkspace();
        var dest = NewDestination();
        var recorded = WriteFile(ws, Path.Combine("sub", "a.md"), "workspace");

        // Record BEFORE the teardown, which is the real order (RunWorkspaceService records inside its
        // promotion walk, the caller tears down afterwards) and the only order in which the workspace root
        // can still be real-path resolved.
        RunWorkspaceRedirects.Record(ws, dest);
        Directory.Delete(ws, recursive: true);

        var promoted = WriteFile(dest, Path.Combine("sub", "a.md"), "promoted");

        Assert.Equal(promoted, RunWorkspaceRedirects.Resolve(recorded));
    }

    /// <summary>
    /// GUARD (it cannot red on a revert of the redirect behaviour): a since-deleted file with no redirect
    /// comes back unchanged, so <c>ShellLauncher</c> no-ops on it exactly as it does today.
    /// </summary>
    [Fact]
    public void Resolve_ReturnsTheInput_WhenNeitherPathExists()
    {
        var missing = Path.Combine(AssistantWorkspace.RunsRoot, Guid.NewGuid().ToString(), "gone.md");

        Assert.Equal(missing, RunWorkspaceRedirects.Resolve(missing));
        Assert.Null(RunWorkspaceRedirects.Resolve(null));
        Assert.Equal(string.Empty, RunWorkspaceRedirects.Resolve(string.Empty));
    }

    /// <summary>
    /// REGRESSION: the containment gate on the registry. Both roots are app-derived today, and this is what
    /// keeps it that way — no path outside the runs tree can install a redirect that would silently send an
    /// "open file" somewhere else. Neutralize by dropping the gate and this reds.
    /// </summary>
    [Fact]
    public void Record_RefusesAWorkspaceRootOutsideTheRunsRoot()
    {
        var outside = Path.Combine(Path.GetTempPath(), "pia-redirect-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        _dirs.Add(outside);
        var dest = NewDestination();
        var recorded = WriteFile(outside, "a.md", "workspace");
        WriteFile(dest, "a.md", "promoted");

        RunWorkspaceRedirects.Record(outside, dest);
        File.Delete(recorded);

        // Nothing was installed, so the input comes back even though the destination copy exists.
        Assert.Equal(recorded, RunWorkspaceRedirects.Resolve(recorded));
    }

    /// <summary>
    /// GUARD: bounds process-local state that nothing else ever clears. The cap holds and the NEWEST entry is
    /// the one that survives — evicting the newest would be worse than not evicting at all.
    /// </summary>
    [Fact]
    public void Record_EvictsPastTheEntryCap()
    {
        string? newestRecorded = null;
        string? newestPromoted = null;

        for (var i = 0; i < RunWorkspaceRedirects.MaxEntries + 4; i++)
        {
            var ws = NewWorkspace();
            var dest = NewDestination();
            var recorded = WriteFile(ws, "a.md", "workspace");
            RunWorkspaceRedirects.Record(ws, dest);
            Directory.Delete(ws, recursive: true);
            newestRecorded = recorded;
            newestPromoted = WriteFile(dest, "a.md", "promoted");
        }

        Assert.True(
            RunWorkspaceRedirects.Count <= RunWorkspaceRedirects.MaxEntries,
            $"the registry grew to {RunWorkspaceRedirects.Count} entries, past its cap of {RunWorkspaceRedirects.MaxEntries}");
        Assert.Equal(newestPromoted, RunWorkspaceRedirects.Resolve(newestRecorded));
    }
}
