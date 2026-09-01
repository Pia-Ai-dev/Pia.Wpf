using System.IO;
using Pia.Helpers;
using Pia.Paths;
using Xunit;

namespace Pia.Tests.Helpers;

/// <summary>
/// Holds the lifetime of the files a virtual-file drop has to write. They are read once, during the drop, and
/// are dead by the time staging returns — the chip carries the extracted text, not the path — so the startup
/// clear is the guarantee and the per-drop sweep is the tidying.
/// </summary>
public sealed class ShellDropCacheTests
{
    [Fact]
    public void CreateDropDirectory_LandsUnderTheRoutedLocalRoot()
    {
        using var scope = PiaPathsTestOverride.Apply(out var local);

        var directory = ShellDropCache.CreateDropDirectory();

        Assert.True(Directory.Exists(directory));
        Assert.StartsWith(Path.Combine(local, "DropCache"), directory, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Two drags of the same mail must not have one overwrite the other's file.</summary>
    [Fact]
    public void CreateDropDirectory_IsFreshEachTime()
    {
        using var scope = PiaPathsTestOverride.Apply(out _);

        Assert.NotEqual(ShellDropCache.CreateDropDirectory(), ShellDropCache.CreateDropDirectory());
    }

    [Fact]
    public void Clear_RemovesEverythingTheCacheHolds()
    {
        using var scope = PiaPathsTestOverride.Apply(out _);
        var directory = ShellDropCache.CreateDropDirectory();
        File.WriteAllText(Path.Combine(directory, "mail.msg"), "x");

        ShellDropCache.Clear();

        Assert.False(Directory.Exists(PiaPaths.DropCacheDirectory));
    }

    [Fact]
    public void CreateDropDirectory_SweepsAnEarlierDropButLeavesARecentOne()
    {
        using var scope = PiaPathsTestOverride.Apply(out _);
        var stale = ShellDropCache.CreateDropDirectory();
        var recent = ShellDropCache.CreateDropDirectory();
        Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-1));

        ShellDropCache.CreateDropDirectory();

        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(recent));
    }

    [Fact]
    public void Clear_IsQuietWhenThereIsNothingToClear()
    {
        using var scope = PiaPathsTestOverride.Apply(out _);

        ShellDropCache.Clear();
        ShellDropCache.Clear();
    }

    /// <summary>Applies a throwaway local root and removes it again, so a run never touches the real profile.</summary>
    private sealed class PiaPathsTestOverride : IDisposable
    {
        private readonly IDisposable _override;
        private readonly string _root;

        private PiaPathsTestOverride(string root)
        {
            _root = root;
            _override = PiaPaths.OverrideForTests(null, root);
        }

        internal static PiaPathsTestOverride Apply(out string localRoot)
        {
            localRoot = Path.Combine(Path.GetTempPath(), "pia-dropcache-" + Guid.NewGuid().ToString("N")[..8]);
            return new PiaPathsTestOverride(localRoot);
        }

        public void Dispose()
        {
            _override.Dispose();
            try
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
            }
            catch (IOException) { }
        }
    }
}
