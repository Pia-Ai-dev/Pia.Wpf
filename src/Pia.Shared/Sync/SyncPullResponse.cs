namespace Pia.Shared.Sync;

using Pia.Shared.Models;

/// <summary>
/// Response from GET /api/sync/pull?since={timestamp}.
/// Contains all entities changed since the given timestamp.
/// </summary>
public class SyncPullResponse
{
    public DateTime ServerTimestamp { get; set; }

    /// <summary>Settings if modified since last sync, null otherwise.</summary>
    public SyncSettings? Settings { get; set; }

    public SyncEntityChanges<SyncTemplate> Templates { get; set; } = new();
    public SyncEntityChanges<SyncProvider> Providers { get; set; } = new();
    public SyncSessionChanges Sessions { get; set; } = new();
    public SyncEntityChanges<SyncMemory> Memories { get; set; } = new();
    public SyncEntityChanges<SyncTodo> Todos { get; set; } = new();
    public SyncEntityChanges<SyncKanbanColumn> KanbanColumns { get; set; } = new();
}

/// <summary>
/// Changes for entities that support upsert and delete (templates, providers, memories).
/// </summary>
public class SyncEntityChanges<T>
{
    public List<T> Upserted { get; set; } = [];
    public List<Guid> Deleted { get; set; } = [];
}

/// <summary>
/// Changes for sessions (append-only, no updates).
/// </summary>
public class SyncSessionChanges
{
    public List<SyncSession> Added { get; set; } = [];
    public List<Guid> Deleted { get; set; } = [];
}
