using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure.Vault;
using Pia.Logging;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>
/// Bridges the on-disk memory vault to the index: a recursive <see cref="FileSystemWatcher"/> over
/// the vault root watches <c>*.md</c> files and, after a per-path debounce, drives
/// <see cref="IVaultIndexer.IndexFileAsync"/> (Created/Changed/Renamed) or
/// <see cref="IVaultIndexer.RemoveFileAsync"/> (Deleted). Pia's own writes flow through the same
/// watcher with no special-casing — that is safe because <c>IndexFileAsync</c> is content-hash
/// idempotent (unchanged sections are skipped). Editors that save by rename emit Created/Changed for
/// the new name and Deleted for the old, which the relative-path debounce handles naturally.
/// </summary>
public sealed class VaultWatcher : IDisposable
{
    /// <summary>Coalescing window for filesystem events on a single path before we (re)index it.</summary>
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(300);

    private readonly IVaultIndexer _indexer;
    private readonly VaultPathProvider _paths;
    private readonly ILogger<VaultWatcher> _logger;

    // One pending timer per vault-relative path; firing replaces (debounces) the previous one.
    private readonly ConcurrentDictionary<string, Timer> _pending = new(StringComparer.Ordinal);

    private FileSystemWatcher? _watcher;
    private string? _root;
    private bool _disposed;

    public VaultWatcher(IVaultIndexer indexer, VaultPathProvider paths, ILogger<VaultWatcher> logger)
    {
        _indexer = indexer;
        _paths = paths;
        _logger = logger;
    }

    /// <summary>Start watching the vault's default root (creating it if absent).</summary>
    public void Start() => Start(_paths.VaultRoot);

    /// <summary>
    /// Start watching <paramref name="root"/> recursively for <c>*.md</c> changes. The directory is
    /// created if it does not exist so the watcher never throws on a fresh install. Idempotent: a
    /// second call after one already started is ignored.
    /// </summary>
    public void Start(string root)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_watcher is not null)
        {
            return;
        }

        Directory.CreateDirectory(root);
        _root = root;

        var watcher = new FileSystemWatcher(root, "*.md")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        watcher.Created += OnChangedOrCreated;
        watcher.Changed += OnChangedOrCreated;
        watcher.Renamed += OnRenamed;
        watcher.Deleted += OnDeleted;
        watcher.Error += OnError;
        watcher.EnableRaisingEvents = true;
        _watcher = watcher;

        _logger.SensitiveDebug("Started vault watcher on {Root}", root);
        _logger.LogInformation("Vault watcher started (root hash {RootHash})", HashPath(root));
    }

    private void OnChangedOrCreated(object sender, FileSystemEventArgs e) =>
        ScheduleIndex(e.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        // The old name may have moved out of the *.md set or to a new path: drop it, index the new.
        if (e.OldFullPath is { } oldPath && oldPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            ScheduleRemove(oldPath);
        }

        ScheduleIndex(e.FullPath);
    }

    private void OnDeleted(object sender, FileSystemEventArgs e) =>
        ScheduleRemove(e.FullPath);

    private void OnError(object sender, ErrorEventArgs e) =>
        _logger.LogWarning(e.GetException(), "Vault watcher error");

    private void ScheduleIndex(string fullPath) =>
        Debounce(fullPath, relativePath => _indexer.IndexFileAsync(relativePath));

    private void ScheduleRemove(string fullPath) =>
        Debounce(fullPath, relativePath => _indexer.RemoveFileAsync(relativePath));

    private void Debounce(string fullPath, Func<string, Task> action)
    {
        if (_disposed || _root is null)
        {
            return;
        }

        var relativePath = ToRelative(_root, fullPath);

        // Replace any in-flight timer for this path so rapid bursts collapse into one index pass.
        var timer = new Timer(_ => Fire(relativePath, action), null, DebounceWindow, Timeout.InfiniteTimeSpan);
        if (_pending.TryRemove(relativePath, out var previous))
        {
            previous.Dispose();
        }

        _pending[relativePath] = timer;
    }

    private async void Fire(string relativePath, Func<string, Task> action)
    {
        if (_pending.TryRemove(relativePath, out var timer))
        {
            timer.Dispose();
        }

        try
        {
            await action(relativePath);
        }
        catch (Exception ex)
        {
            // A watcher callback must never crash the process; surface and move on.
            _logger.LogWarning(ex, "Vault watcher failed to process a change");
            _logger.SensitiveDebug("Vault watcher failed on {Path}", relativePath);
        }
    }

    private static string ToRelative(string root, string fullPath)
    {
        // Normalize to forward slashes so the relative path matches the vault store's own keys.
        var relative = Path.GetRelativePath(root, fullPath);
        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string HashPath(string path)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(path));
        return Convert.ToHexString(hash, 0, 4);
    }

    /// <summary>
    /// Stop watching and release the directory handle (so the watched root can be moved/deleted on
    /// Windows), leaving the instance reusable via <see cref="Start"/> / <see cref="Restart"/>.
    /// </summary>
    public void Stop()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnChangedOrCreated;
            _watcher.Changed -= OnChangedOrCreated;
            _watcher.Renamed -= OnRenamed;
            _watcher.Deleted -= OnDeleted;
            _watcher.Error -= OnError;
            _watcher.Dispose();
            _watcher = null;
        }

        foreach (var timer in _pending.Values)
        {
            timer.Dispose();
        }

        _pending.Clear();
    }

    /// <summary>Stop and re-start on a new root (used by folder relocation).</summary>
    public void Restart(string root)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Stop();
        Start(root);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}
