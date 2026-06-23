using System.Collections.Concurrent;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>
/// Thread-safe in-memory store of last-read modification times, keyed by
/// <c>(taskId, canonicalizedResolvedPath)</c>. See <see cref="IFileStalenessStore"/>.
/// </summary>
public class FileStalenessStore : IFileStalenessStore
{
    // Resolved paths arrive canonicalized from the resolver; key comparison still uses the
    // case-insensitive ordinal comparer so it stays symmetric with SafeFolderPath's
    // OrdinalIgnoreCase containment check on the Windows filesystem.
    private readonly ConcurrentDictionary<StalenessKey, DateTime> _reads = new();

    public void RecordRead(Guid taskId, string resolvedPath, DateTime mtimeUtc)
    {
        if (string.IsNullOrEmpty(resolvedPath)) return;
        _reads[new StalenessKey(taskId, resolvedPath)] = mtimeUtc;
    }

    public bool CheckStaleness(Guid taskId, string resolvedPath, DateTime currentMtimeUtc)
    {
        if (string.IsNullOrEmpty(resolvedPath)) return false;
        if (!_reads.TryGetValue(new StalenessKey(taskId, resolvedPath), out var recorded))
            return false; // unknown (taskId, path) -> treat as not-stale (see interface doc)

        return recorded != currentMtimeUtc;
    }

    private readonly record struct StalenessKey
    {
        private readonly Guid _taskId;
        private readonly string _path;

        public StalenessKey(Guid taskId, string path)
        {
            _taskId = taskId;
            _path = path;
        }

        public bool Equals(StalenessKey other) =>
            _taskId == other._taskId &&
            string.Equals(_path, other._path, StringComparison.OrdinalIgnoreCase);

        public override int GetHashCode() =>
            HashCode.Combine(_taskId, StringComparer.OrdinalIgnoreCase.GetHashCode(_path));
    }
}
