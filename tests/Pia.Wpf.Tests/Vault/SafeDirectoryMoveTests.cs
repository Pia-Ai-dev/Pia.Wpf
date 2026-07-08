using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Pia.Infrastructure.Vault;
using Pia.Models;
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
    public async Task Migration_shaped_move_verifies_vault_root_files()
    {
        // Mirrors the in-place migration: the SOURCE is the vault root itself, so relative paths are
        // "memory/..." / "index.md" with no "Vault/" prefix. Confirms hash-verify covers them.
        var src = Path.Combine(_base, "legacyVault");
        var dst = Path.Combine(_base, "nestedVault");
        Directory.CreateDirectory(Path.Combine(src, "memory"));
        await File.WriteAllTextAsync(Path.Combine(src, "memory", "m.md"), "---\nid: 1\n---\nremember", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(src, "index.md"), "# index", TestContext.Current.CancellationToken);

        var result = await SafeDirectoryMove.MoveAsync(src, dst, null, CancellationToken.None);

        Assert.Equal(DirectoryMoveOutcome.Success, result.Outcome);
        Assert.False(Directory.Exists(src));
        Assert.Equal("---\nid: 1\n---\nremember",
            await File.ReadAllTextAsync(Path.Combine(dst, "memory", "m.md"), TestContext.Current.CancellationToken));
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
        // Synchronous IProgress so Report runs inline during MoveAsync — no async-drain race.
        var progress = new SynchronousProgress(p => phases.Add(p.Phase));

        var result = await SafeDirectoryMove.MoveAsync(src, dst, progress, CancellationToken.None);

        Assert.Equal(DirectoryMoveOutcome.Success, result.Outcome);
        Assert.Contains(FolderMovePhase.Copying, phases);
    }

    private sealed class SynchronousProgress : IProgress<FolderMoveProgress>
    {
        private readonly Action<FolderMoveProgress> _handler;
        public SynchronousProgress(Action<FolderMoveProgress> handler) => _handler = handler;
        public void Report(FolderMoveProgress value) => _handler(value);
    }
}
