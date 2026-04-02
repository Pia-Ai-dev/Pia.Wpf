using System.IO;
using System.Text.Json;

namespace Pia.Services;

public class SyncDeleteTracker
{
    private readonly string _filePath;
    private Dictionary<string, HashSet<Guid>> _pendingDeletes = new();
    private readonly object _lock = new();

    public SyncDeleteTracker(string dataDirectory)
    {
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
            Save();
        }
    }

    public Dictionary<string, List<Guid>> GetPendingDeletes()
    {
        lock (_lock)
        {
            return _pendingDeletes.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.ToList());
        }
    }

    public void ClearAfterSuccessfulPush()
    {
        lock (_lock)
        {
            _pendingDeletes.Clear();
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
            }
        }
        catch
        {
            _pendingDeletes = new();
        }
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        var data = _pendingDeletes.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.ToList());
        File.WriteAllText(_filePath, JsonSerializer.Serialize(data));
    }
}
