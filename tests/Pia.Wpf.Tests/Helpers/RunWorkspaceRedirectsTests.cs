using System.IO;
using Pia.Helpers;
using Pia.Infrastructure;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Helpers;

/// <summary>
/// Rooted at the runs root because <see cref="RunWorkspaceRedirects.Record"/>'s containment gate refuses
/// anything else — a REDIRECTED one, so the developer's own profile is not touched — and collection-shared
/// because the cap fact deliberately overflows a process-global registry.
/// </summary>
[Collection("PiaPathsStatic")]
public sealed class RunWorkspaceRedirectsTests : IClassFixture<RedirectedProfileFixture>, IDisposable
{
    private readonly List<string> _dirs = [];

    public RunWorkspaceRedirectsTests(RedirectedProfileFixture profile) => _ = profile;

    /// <summary>A run-shaped workspace directory: inside the runs root of the redirected profile, so Record's
    /// containment gate accepts it without the developer's real one being touched.</summary>
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
            TempPath.Remove(dir);
        }
    }

    /// <summary>A byte-identical file counts as promoted before teardown, so a redirect can be installed while the file is still there.</summary>
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

    [Fact]
    public void Resolve_RedirectsToThePromotedCopy_OnceTheWorkspaceIsGone()
    {
        var ws = NewWorkspace();
        var dest = NewDestination();
        var recorded = WriteFile(ws, Path.Combine("sub", "a.md"), "workspace");

        // Record BEFORE the teardown: the workspace root can only be real-path resolved while it still exists.
        RunWorkspaceRedirects.Record(ws, dest);
        Directory.Delete(ws, recursive: true);

        var promoted = WriteFile(dest, Path.Combine("sub", "a.md"), "promoted");

        Assert.Equal(promoted, RunWorkspaceRedirects.Resolve(recorded));
    }

    [Fact]
    public void Resolve_ReturnsTheInput_WhenNeitherPathExists()
    {
        var missing = Path.Combine(AssistantWorkspace.RunsRoot, Guid.NewGuid().ToString(), "gone.md");

        Assert.Equal(missing, RunWorkspaceRedirects.Resolve(missing));
        Assert.Null(RunWorkspaceRedirects.Resolve(null));
        Assert.Equal(string.Empty, RunWorkspaceRedirects.Resolve(string.Empty));
    }

    /// <summary>No path outside the runs tree may install a redirect that silently sends an "open file" elsewhere.</summary>
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

    /// <summary>Bounds process-local state nothing else clears, and the newest entry is the one that survives.</summary>
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
