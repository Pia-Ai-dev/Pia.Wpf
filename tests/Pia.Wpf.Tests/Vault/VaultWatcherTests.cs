using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure.Vault;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Vault;

public class VaultWatcherTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string _vaultRoot;
    private readonly VaultPathProvider _paths;

    public VaultWatcherTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"pia-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tmpDir);
        _vaultRoot = Path.Combine(_tmpDir, "vault");
        Directory.CreateDirectory(_vaultRoot);
        _paths = new VaultPathProvider(_vaultRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tmpDir))
            {
                Directory.Delete(_tmpDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup of the temp dir.
        }
    }

    // Polls a predicate until it holds or the timeout elapses; the FileSystemWatcher + 300ms
    // debounce are inherently timing-based, so we wait rather than assume a fixed delay.
    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> predicate, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (await predicate())
                {
                    return true;
                }
            }
            catch
            {
                // Predicate not yet satisfied (e.g. NSubstitute Received() throws when the call has
                // not been observed) — keep polling until the deadline.
            }

            await Task.Delay(25);
        }

        try
        {
            return await predicate();
        }
        catch
        {
            return false;
        }
    }

    [Fact]
    public async Task Writing_md_file_invokes_IndexFileAsync_for_its_relative_path()
    {
        var indexer = Substitute.For<IVaultIndexer>();
        using var watcher = new VaultWatcher(indexer, _paths, NullLogger<VaultWatcher>.Instance);
        watcher.Start(_vaultRoot);

        var fullPath = Path.Combine(_vaultRoot, "profile.md");
        await File.WriteAllTextAsync(fullPath, "## Preferences\n- likes coffee\n", TestContext.Current.CancellationToken);

        var indexed = await WaitUntilAsync(async () =>
        {
            await indexer.Received().IndexFileAsync(Arg.Is<string>(p => p == "profile.md"));
            return true;
        });

        // The assertion above throws if Received() is not satisfied; the wait loop swallows it
        // until the deadline. Do a final hard assert so a never-fired event fails the test.
        await indexer.Received().IndexFileAsync(Arg.Is<string>(p => p == "profile.md"));
        Assert.True(indexed);
    }

    [Fact]
    public async Task Deleting_md_file_invokes_RemoveFileAsync_for_its_relative_path()
    {
        var indexer = Substitute.For<IVaultIndexer>();

        // Seed the file BEFORE starting the watcher so only the delete is observed.
        var fullPath = Path.Combine(_vaultRoot, "contacts.md");
        await File.WriteAllTextAsync(fullPath, "## John Smith\n- email: john@example.com\n", TestContext.Current.CancellationToken);

        using var watcher = new VaultWatcher(indexer, _paths, NullLogger<VaultWatcher>.Instance);
        watcher.Start(_vaultRoot);

        File.Delete(fullPath);

        var removed = await WaitUntilAsync(async () =>
        {
            await indexer.Received().RemoveFileAsync(Arg.Is<string>(p => p == "contacts.md"));
            return true;
        });

        await indexer.Received().RemoveFileAsync(Arg.Is<string>(p => p == "contacts.md"));
        Assert.True(removed);
    }

    [Fact]
    public async Task Restart_rebinds_watcher_to_new_root()
    {
        var indexer = Substitute.For<IVaultIndexer>();
        var rootB = Path.Combine(_tmpDir, "vaultB");
        Directory.CreateDirectory(rootB);

        using var watcher = new VaultWatcher(indexer, _paths, NullLogger<VaultWatcher>.Instance);
        watcher.Start(_vaultRoot);
        watcher.Restart(rootB);

        // A file created under the NEW root is indexed, relative to that new root.
        var fullPath = Path.Combine(rootB, "moved.md");
        await File.WriteAllTextAsync(fullPath, "## X\n- y\n", TestContext.Current.CancellationToken);

        var indexed = await WaitUntilAsync(async () =>
        {
            await indexer.Received().IndexFileAsync(Arg.Is<string>(p => p == "moved.md"));
            return true;
        });

        await indexer.Received().IndexFileAsync(Arg.Is<string>(p => p == "moved.md"));
        Assert.True(indexed);
    }
}
