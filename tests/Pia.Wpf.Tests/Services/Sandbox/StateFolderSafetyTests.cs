using System.Diagnostics;
using System.IO;
using Pia.Services.Plugins.Sandbox.Runtimes;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Services.Sandbox;

public sealed class StateFolderSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pia-statesafety-" + Guid.NewGuid().ToString("N"));
    private readonly string _state;
    private readonly string _outside;
    private readonly string _outsideFile;

    public StateFolderSafetyTests()
    {
        _state = Directory.CreateDirectory(Path.Combine(_root, "srv.runtime")).FullName;
        _outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        _outsideFile = Path.Combine(_outside, "keep.txt");
        File.WriteAllText(_outsideFile, "outside");
    }

    public void Dispose()
    {
        StateFolderSafety.DeleteTree(_state);
        TempPath.Remove(_root);
    }

    internal static void Junction(string link, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        var info = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { "/c", "mklink", "/J", link, target })
            info.ArgumentList.Add(arg);

        using var process = Process.Start(info)!;
        process.WaitForExit();
        Assert.SkipUnless(Directory.Exists(link), "a junction could not be created here");
    }

    private static bool IsLink(string path) =>
        Directory.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    [Fact]
    public void EnsureNoReparsePoints_ReplacesAJunctionOnThePath_AndLeavesItsTargetAlone()
    {
        Directory.CreateDirectory(Path.Combine(_state, "home"));
        var link = Path.Combine(_state, "home", "AppData");
        Junction(link, _outside);

        StateFolderSafety.EnsureNoReparsePoints(_state, Path.Combine(link, "Roaming", "NuGet", "NuGet.Config"));

        Assert.True(Directory.Exists(link));
        Assert.False(IsLink(link));
        Assert.Empty(Directory.EnumerateFileSystemEntries(link));
        Assert.Equal("outside", File.ReadAllText(_outsideFile));
    }

    [Fact]
    public void EnsureNoReparsePoints_RemovesAJunctionAtTheLeaf_WithoutRecreatingIt()
    {
        var link = Path.Combine(_state, "pia-uv-spec.json");
        Junction(link, _outside);

        StateFolderSafety.EnsureNoReparsePoints(_state, link);

        Assert.False(Directory.Exists(link));
        Assert.True(File.Exists(_outsideFile));
    }

    [Fact]
    public void EnsureNoReparsePoints_LeavesRealFoldersAndMissingOnesAlone()
    {
        var real = Directory.CreateDirectory(Path.Combine(_state, "home", "AppData")).FullName;
        File.WriteAllText(Path.Combine(real, "a.txt"), "a");

        StateFolderSafety.EnsureNoReparsePoints(_state, Path.Combine(real, "Roaming", "missing", "x.txt"));

        Assert.True(File.Exists(Path.Combine(real, "a.txt")));
        Assert.False(Directory.Exists(Path.Combine(real, "Roaming")));
    }

    [Fact]
    public void EnsureNoReparsePoints_RefusesAPathOutsideTheRoot() =>
        Assert.Throws<ArgumentException>(() => StateFolderSafety.EnsureNoReparsePoints(_state, _outsideFile));

    [Fact]
    public void EnumerateNoLinks_NeverYieldsWhatSitsBehindAJunction()
    {
        var real = Directory.CreateDirectory(Path.Combine(_state, "tools", "pkg")).FullName;
        File.WriteAllText(Path.Combine(real, "inside.txt"), "in");
        File.WriteAllText(Path.Combine(_state, ".hidden"), "dot");
        File.SetAttributes(Path.Combine(_state, ".hidden"), FileAttributes.Hidden);
        Junction(Path.Combine(_state, "tools", "link"), _outside);

        var entries = StateFolderSafety.EnumerateNoLinks(_state).ToList();

        Assert.Contains(Path.Combine(real, "inside.txt"), entries);
        Assert.Contains(Path.Combine(_state, ".hidden"), entries);
        Assert.DoesNotContain(entries, e => e.EndsWith("keep.txt", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(Path.Combine(_state, "tools", "link"), entries);
    }

    [Fact]
    public void EnumerateNoLinks_OfAMissingFolder_IsEmpty() =>
        Assert.Empty(StateFolderSafety.EnumerateNoLinks(Path.Combine(_root, "missing")));

    [Fact]
    public void DeleteTree_DeletesAJunctionAndReadOnlyFiles_AndLeavesTheOutsideFile()
    {
        var deep = Directory.CreateDirectory(Path.Combine(_state, "a", "b", "c", "d")).FullName;
        var readOnly = Path.Combine(deep, "ro.txt");
        File.WriteAllText(readOnly, "ro");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        File.SetAttributes(_outsideFile, FileAttributes.ReadOnly);
        Junction(Path.Combine(_state, "a", "link"), _outside);

        StateFolderSafety.DeleteTree(_state);

        Assert.False(Directory.Exists(_state));
        Assert.Equal("outside", File.ReadAllText(_outsideFile));
        Assert.True(File.GetAttributes(_outsideFile).HasFlag(FileAttributes.ReadOnly));
        File.SetAttributes(_outsideFile, FileAttributes.Normal);
    }

    [Fact]
    public void DeleteTree_OfAJunctionItself_RemovesOnlyTheLink()
    {
        var link = Path.Combine(_state, "tools");
        Junction(link, _outside);

        StateFolderSafety.DeleteTree(link);

        Assert.False(Directory.Exists(link));
        Assert.True(File.Exists(_outsideFile));
    }

    [Fact]
    public void DeleteTree_OfAMissingFolder_DoesNothing() =>
        StateFolderSafety.DeleteTree(Path.Combine(_root, "missing"));
}
