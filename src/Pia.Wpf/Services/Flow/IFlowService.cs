using Pia.Models.Flow;

namespace Pia.Services.Flow;

/// <summary>
/// The canonical, thread-safe Flow store (design §7). Owns dedup, auto-retract, expiry, bounded
/// capacity, the durability invariant, and persistence. A singleton shared across all windows.
/// </summary>
public interface IFlowService
{
    /// <summary>An immutable snapshot of the current live items (newest first).</summary>
    IReadOnlyList<FlowItem> Snapshot { get; }

    /// <summary>Raised after any change to the live set (publish / update / dismiss / retract / expire / clear).</summary>
    event EventHandler? Changed;

    /// <summary>Raised when an item is published or updated in place, so a foreground presenter can peek.</summary>
    event EventHandler<FlowItem>? ItemArrived;

    /// <summary>Publishes a draft. Stamps id/timestamp, enforces the durability invariant, dedups by key, persists if durable.</summary>
    FlowItem Publish(FlowItemDraft draft);

    /// <summary>Marks an item read (and persists the change if durable).</summary>
    void MarkRead(Guid id);

    /// <summary>Removes an item by id (user dismissal). Deletes its durable row. Never mutates the source entity.</summary>
    void Dismiss(Guid id);

    /// <summary>Auto-retract: removes the live item for a dedup key (entity resolved). Deletes its durable row.</summary>
    void Retract(string dedupKey);

    /// <summary>Removes every live item ("Clear all") and deletes all durable rows.</summary>
    void Clear();

    /// <summary>Loads durable items from persistence into the live set on startup.</summary>
    Task LoadAsync();
}
