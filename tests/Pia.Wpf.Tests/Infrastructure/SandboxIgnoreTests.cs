using System.IO;
using Pia.Infrastructure;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Infrastructure;

/// <summary>
/// Guards <see cref="SandboxIgnore"/>: that the shipped defaults actually load from the embedded
/// resource (a build/packaging slip that drops it would silently regress <c>@Files</c> back to
/// listing <c>.git</c>), and that a folder's <c>.gitignore</c>/<c>.piaignore</c> layer on top.
/// </summary>
public sealed class SandboxIgnoreTests : IDisposable
{
    private readonly string _root;

    public SandboxIgnoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pia-ignore-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        TempPath.Remove(_root);
    }

    [Fact]
    public void Defaults_LoadFromEmbeddedResource()
    {
        // Non-empty, and carries an entry that lives ONLY in the embedded file (not the code-side
        // FallbackDefaults) — so this fails if the embedded resource stops loading.
        Assert.NotEmpty(SandboxIgnore.DefaultPatterns);
        Assert.Contains("Thumbs.db", SandboxIgnore.DefaultPatterns);
    }

    [Fact]
    public void Defaults_IgnoreCoreBuildAndVcsDirectories()
    {
        var m = SandboxIgnore.ForRoot(_root); // no .gitignore/.piaignore present → defaults only

        Assert.True(m.IsIgnored(".git", isDirectory: true));
        Assert.True(m.IsIgnored("bin", isDirectory: true));
        Assert.True(m.IsIgnored("obj", isDirectory: true));
        Assert.True(m.IsIgnored("node_modules", isDirectory: true));
        Assert.True(m.IsIgnored("src/bin", isDirectory: true)); // depth-agnostic

        Assert.False(m.IsIgnored("notes.md", isDirectory: false));
        Assert.False(m.IsIgnored("cabinet", isDirectory: true)); // substring trap
    }

    [Fact]
    public void ForRoot_LayersPiaIgnore()
    {
        File.WriteAllText(Path.Combine(_root, SandboxIgnore.PiaIgnoreFileName), "secret.txt\n*.tmp\n");
        var m = SandboxIgnore.ForRoot(_root);

        Assert.True(m.IsIgnored("secret.txt", isDirectory: false));
        Assert.True(m.IsIgnored("cache/x.tmp", isDirectory: false));
        Assert.False(m.IsIgnored("notes.md", isDirectory: false));
    }

    [Fact]
    public void ForRoot_LayersGitIgnore()
    {
        File.WriteAllText(Path.Combine(_root, SandboxIgnore.GitIgnoreFileName), "*.bak\n");
        var m = SandboxIgnore.ForRoot(_root);

        Assert.True(m.IsIgnored("old.bak", isDirectory: false));
    }

    [Fact]
    public void ForRoot_PiaIgnoreNegation_ReincludesFile()
    {
        File.WriteAllText(Path.Combine(_root, SandboxIgnore.PiaIgnoreFileName), "*.log\n!keep.log\n");
        var m = SandboxIgnore.ForRoot(_root);

        Assert.False(m.IsIgnored("keep.log", isDirectory: false));
        Assert.True(m.IsIgnored("debug.log", isDirectory: false));
    }

    [Fact]
    public void ForRoot_BoundsIgnoreFileLineCount()
    {
        // A pathological ignore file must not produce unbounded rules. The read caps at 2000 lines,
        // so a real pattern past the cap is dropped (early rules apply; late ones are ignored).
        var early = "early-secret.txt";
        var pad = string.Concat(Enumerable.Repeat("# pad\n", 2100));
        File.WriteAllText(Path.Combine(_root, SandboxIgnore.PiaIgnoreFileName), early + "\n" + pad + "late-secret.txt\n");

        var m = SandboxIgnore.ForRoot(_root);

        Assert.True(m.IsIgnored("early-secret.txt", isDirectory: false)); // before the cap
        Assert.False(m.IsIgnored("late-secret.txt", isDirectory: false)); // dropped past the line cap
    }
}
