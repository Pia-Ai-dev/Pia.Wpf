using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Pia.Infrastructure.Vault;
using Xunit;

namespace Pia.Tests.Vault;

public class SafeDirectoryMoveTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "sdm-" + Guid.NewGuid().ToString("N"));

    public SafeDirectoryMoveTests() => Directory.CreateDirectory(_base);

    public void Dispose() { try { Directory.Delete(_base, true); } catch { } }

    [Fact]
    public async Task Move_copies_tree_then_deletes_source()
    {
        var src = Path.Combine(_base, "src");
        var dst = Path.Combine(_base, "dst");
        Directory.CreateDirectory(Path.Combine(src, "Vault", "memory"));
        await File.WriteAllTextAsync(Path.Combine(src, "Vault", "memory", "a.md"), "x", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(src, "doc.txt"), "hello", TestContext.Current.CancellationToken);

        var result = await SafeDirectoryMove.MoveAsync(src, dst, progress: null, CancellationToken.None);

        Assert.Equal(DirectoryMoveOutcome.Success, result.Outcome);
        Assert.False(Directory.Exists(src));
        Assert.Equal("x", await File.ReadAllTextAsync(Path.Combine(dst, "Vault", "memory", "a.md"), TestContext.Current.CancellationToken));
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(dst, "doc.txt"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Verify_failure_keeps_source_and_removes_partial_dst()
    {
        var src = Path.Combine(_base, "src2");
        var dst = Path.Combine(_base, "dst2");
        Directory.CreateDirectory(src);
        await File.WriteAllTextAsync(Path.Combine(src, "a.txt"), "data", TestContext.Current.CancellationToken);

        var result = await SafeDirectoryMove.MoveAsync(src, dst, null, CancellationToken.None,
            verifyOverride: () => false);

        Assert.Equal(DirectoryMoveOutcome.VerifyFailed, result.Outcome);
        Assert.True(Directory.Exists(src));            // source intact
        Assert.True(File.Exists(Path.Combine(src, "a.txt")));
        Assert.False(Directory.Exists(dst));           // partial copy removed (we created it)
    }

    [Fact]
    public async Task Missing_source_is_a_noop_success()
    {
        var src = Path.Combine(_base, "does-not-exist");
        var dst = Path.Combine(_base, "dst3");
        var result = await SafeDirectoryMove.MoveAsync(src, dst, null, CancellationToken.None);
        Assert.Equal(DirectoryMoveOutcome.Success, result.Outcome);
    }

    [Fact]
    public async Task Reports_phases_through_progress()
    {
        var src = Path.Combine(_base, "src4");
        var dst = Path.Combine(_base, "dst4");
        Directory.CreateDirectory(src);
        await File.WriteAllTextAsync(Path.Combine(src, "f.txt"), "z", TestContext.Current.CancellationToken);

        var phases = new System.Collections.Concurrent.ConcurrentBag<FolderMovePhase>();
        var progress = new Progress<FolderMoveProgress>(p => phases.Add(p.Phase));

        var result = await SafeDirectoryMove.MoveAsync(src, dst, progress, CancellationToken.None);

        Assert.Equal(DirectoryMoveOutcome.Success, result.Outcome);
        // Progress callbacks are async; give them a moment to drain on the captured context.
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Contains(FolderMovePhase.Copying, phases);
    }
}
