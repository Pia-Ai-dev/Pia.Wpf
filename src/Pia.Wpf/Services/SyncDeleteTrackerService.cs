using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Pia.Services;

public class SyncDeleteTrackerService
{
    private readonly string _filePath;
    private readonly ILogger<SyncDeleteTrackerService> _logger;
    private Dictionary<string, HashSet<Guid>> _pendingDeletes = new();
    private readonly object _lock = new();

    public SyncDeleteTrackerService(string dataDirectory, ILogger<SyncDeleteTrackerService> logger)
    {
        _logger = logger;
        _filePath = Path.Combine(dataDirectory, "pending-sync-deletes.json");
        Load();
    }

    public void TrackDeletion(string entityType, Guid id)
    {
        lock (_lock)
        {
            if (!_pendingDeletes.ContainsKey(entityType))
                _pendingDeletes[entityType] = [];
            _pendingDeletes[entityType].Add(id);
            var totalPending = _pendingDeletes.Values.Sum(s => s.Count);
            _logger.LogInformation("Delete tracked: {EntityType} {Id} (pending: {TotalCount})", entityType, id, totalPending);
            Save();
        }
    }

    public Dictionary<string, List<Guid>> GetPendingDeletes()
    {
        lock (_lock)
        {
            var result = _pendingDeletes.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.ToList());
            var summary = string.Join(", ", result.Select(kvp => $"{kvp.Key}={kvp.Value.Count}"));
            _logger.LogDebug("Pending deletes retrieved: {Counts}", summary);
            return result;
        }
    }

    public void ClearAfterSuccessfulPush()
    {
        lock (_lock)
        {
            var count = _pendingDeletes.Values.Sum(s => s.Count);
            _pendingDeletes.Clear();
            _logger.LogInformation("Pending deletes cleared after successful push ({TotalCount} total)", count);
            Save();
        }
    }

    private void Load()
    {
        if (!File.Exists(_filePath)) return;
        try
        {
            var json = File.ReadAllText(_filePath);
            var data = JsonSerializer.Deserialize<Dictionary<string, List<Guid>>>(json);
            if (data is not null)
            {
                _pendingDeletes = data.ToDictionary(
                    kvp => kvp.Key,
                    kvp => new HashSet<Guid>(kvp.Value));
                var count = _pendingDeletes.Values.Sum(s => s.Count);
                _logger.LogDebug("Loaded {Count} pending deletes from disk", count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load pending deletes from {Path}", _filePath);
            _pendingDeletes = new();
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            var data = _pendingDeletes.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.ToList());
            File.WriteAllText(_filePath, JsonSerializer.Serialize(data));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save pending deletes to {Path}", _filePath);
        }
    }
}
