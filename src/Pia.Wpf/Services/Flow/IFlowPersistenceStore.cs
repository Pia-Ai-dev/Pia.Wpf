using Pia.Models.Flow;

namespace Pia.Services.Flow;

/// <summary>
/// Reads and writes the durable subset of Flow items to local storage (design §6, §10).
/// Only entity-backed persistent items with a re-derivable (or null) action are ever stored.
/// </summary>
public interface IFlowPersistenceStore
{
    /// <summary>Loads all persisted durable items. Each is reconstructed as Persistent + Durable.</summary>
    IReadOnlyList<FlowItem> ReadAll();

    /// <summary>Inserts or replaces a durable item.</summary>
    void Upsert(FlowItem item);

    /// <summary>Removes a single item by id (no-op if absent).</summary>
    void Delete(Guid id);

    /// <summary>Removes every persisted item.</summary>
    void DeleteAll();
}
