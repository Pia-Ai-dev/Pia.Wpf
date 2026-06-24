using Pia.Models.Flow;
using Pia.Services.Flow;

namespace Pia.Tests.Services.Flow;

/// <summary>
/// In-memory <see cref="IFlowPersistenceStore"/> double that records calls and survives across two
/// <see cref="FlowService"/> instances (modelling a restart). <see cref="ReadAll"/> returns fresh clones,
/// like the real SQLite store, so reloaded items don't alias the originals.
/// </summary>
internal sealed class FakeFlowPersistenceStore : IFlowPersistenceStore
{
    public Dictionary<Guid, FlowItem> Store { get; } = new();
    public int UpsertCount { get; private set; }
    public int DeleteCount { get; private set; }
    public int DeleteAllCount { get; private set; }

    public IReadOnlyList<FlowItem> ReadAll() =>
        Store.Values.OrderBy(i => i.CreatedAt).Select(Clone).ToList();

    public void Upsert(FlowItem item)
    {
        UpsertCount++;
        Store[item.Id] = Clone(item);
    }

    public void Delete(Guid id)
    {
        DeleteCount++;
        Store.Remove(id);
    }

    public void DeleteAll()
    {
        DeleteAllCount++;
        Store.Clear();
    }

    private static FlowItem Clone(FlowItem i) => new()
    {
        Id = i.Id,
        CreatedAt = i.CreatedAt,
        Severity = i.Severity,
        Source = i.Source,
        Title = i.Title,
        Body = i.Body,
        DedupKey = i.DedupKey,
        Lifetime = i.Lifetime,
        IsRead = i.IsRead,
        Action = i.Action,
        Durable = i.Durable,
    };
}
