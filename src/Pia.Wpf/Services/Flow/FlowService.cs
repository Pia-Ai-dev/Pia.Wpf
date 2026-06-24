using System.Threading;
using Microsoft.Extensions.Logging;
using Pia.Models.Flow;

namespace Pia.Services.Flow;

/// <summary>
/// The canonical thread-safe Flow store (design §6–§7). Owns dedup, bounded capacity, transient
/// expiry, the durability invariant, and write-through persistence. All persistence I/O and event
/// raising happen outside the lock; presenters marshal the events to the UI thread.
/// Item content (Title/Body) is sensitive — only ids/enums/counts are logged at default level.
/// </summary>
public sealed class FlowService : IFlowService, IDisposable
{
    /// <summary>Maximum number of live items (design §6 "bounded capacity").</summary>
    internal const int Capacity = 50;

    private readonly object _gate = new();
    private readonly List<FlowItem> _items = new();
    private readonly IFlowPersistenceStore _persistence;
    private readonly ILogger<FlowService> _logger;
    private readonly Timer _sweepTimer;
    private bool _disposed;

    public FlowService(IFlowPersistenceStore persistence, ILogger<FlowService> logger)
    {
        _persistence = persistence;
        _logger = logger;
        // Thin wall-clock driver over the deterministic Sweep(now) seam (which tests call directly).
        _sweepTimer = new Timer(OnSweepTick, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public event EventHandler? Changed;
    public event EventHandler<FlowItem>? ItemArrived;

    public IReadOnlyList<FlowItem> Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _items.OrderByDescending(i => i.CreatedAt).ToList();
            }
        }
    }

    public FlowItem Publish(FlowItemDraft draft)
    {
        var durable = ComputeDurable(draft);
        FlowItem result;
        FlowItem? persistCopy;     // snapshot taken under the lock so persistence I/O can't read a torn item
        bool deleteDowngraded;     // a dedup re-publish that flipped Durable true→false must delete the old row
        List<FlowItem> evicted;

        lock (_gate)
        {
            FlowItem? existing = draft.DedupKey is null
                ? null
                : _items.FirstOrDefault(i => i.DedupKey == draft.DedupKey);

            if (existing is not null)
            {
                // Smart dedup: newer state wins, updated in place (design §6).
                var wasDurable = existing.Durable;
                existing.Severity = draft.Severity;
                existing.Title = draft.Title;
                existing.Body = draft.Body;
                existing.Lifetime = draft.Lifetime;
                existing.Action = draft.Action;
                existing.Durable = durable;
                existing.IsRead = false;
                existing.CreatedAt = DateTimeOffset.Now;
                result = existing;
                deleteDowngraded = wasDurable && !durable;
            }
            else
            {
                result = new FlowItem
                {
                    Id = Guid.NewGuid(),
                    CreatedAt = DateTimeOffset.Now,
                    Severity = draft.Severity,
                    Source = draft.Source,
                    Title = draft.Title,
                    Body = draft.Body,
                    DedupKey = draft.DedupKey,
                    Lifetime = draft.Lifetime,
                    IsRead = false,
                    Action = draft.Action,
                    Durable = durable,
                };
                _items.Add(result);
                deleteDowngraded = false;
            }

            // An item never evicts itself (a brand-new arrival is not "oldest"); the store may exceed
            // capacity instead, consistent with the protected-only over-capacity rule (design §6).
            evicted = EvictIfNeeded(result);
            persistCopy = result.Durable ? Clone(result) : null;
        }

        // Persistence + events outside the lock; persistCopy is an isolated snapshot (no torn read).
        if (deleteDowngraded)
            SafePersist(() => _persistence.Delete(result.Id));
        if (persistCopy is not null)
            SafePersist(() => _persistence.Upsert(persistCopy));
        foreach (var item in evicted)
            DeleteThroughIfDurable(item);

        if (draft.RequestDurable && !durable)
            _logger.LogDebug("Flow item from {Source} requested durable but failed the invariant; kept session-only", draft.Source);

        RaiseChanged();
        ItemArrived?.Invoke(this, result);
        return result;
    }

    public void MarkRead(Guid id)
    {
        FlowItem? persistCopy = null;
        lock (_gate)
        {
            var item = _items.FirstOrDefault(i => i.Id == id);
            if (item is null || item.IsRead)
                return;
            item.IsRead = true;
            if (item.Durable)
                persistCopy = Clone(item);
        }

        if (persistCopy is not null)
            SafePersist(() => _persistence.Upsert(persistCopy));
        RaiseChanged();
    }

    public void Dismiss(Guid id)
    {
        FlowItem? removed;
        lock (_gate)
        {
            removed = _items.FirstOrDefault(i => i.Id == id);
            if (removed is null)
                return;
            _items.Remove(removed);
        }

        DeleteThroughIfDurable(removed);
        RaiseChanged();
    }

    public void Retract(string dedupKey)
    {
        if (string.IsNullOrEmpty(dedupKey))
            return;

        List<FlowItem> removed;
        lock (_gate)
        {
            removed = _items.Where(i => i.DedupKey == dedupKey).ToList();
            if (removed.Count == 0)
                return;
            foreach (var item in removed)
                _items.Remove(item);
        }

        foreach (var item in removed)
            DeleteThroughIfDurable(item);
        RaiseChanged();
    }

    public void Clear()
    {
        bool any;
        lock (_gate)
        {
            any = _items.Count > 0;
            _items.Clear();
        }

        if (!any)
            return;
        SafePersist(_persistence.DeleteAll);
        RaiseChanged();
    }

    public Task LoadAsync()
    {
        IReadOnlyList<FlowItem> loaded;
        try
        {
            loaded = _persistence.ReadAll();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Flow failed to load durable items");
            return Task.CompletedTask;
        }

        lock (_gate)
        {
            foreach (var item in loaded)
            {
                if (item.DedupKey is not null && _items.Any(i => i.DedupKey == item.DedupKey))
                    continue;
                _items.Add(item);
            }
        }

        if (loaded.Count > 0)
            RaiseChanged();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Removes transient items whose lifetime has elapsed as of <paramref name="now"/>. Public and
    /// pure so expiry is deterministically testable without a wall clock. Returns true if anything changed.
    /// </summary>
    public bool Sweep(DateTimeOffset now)
    {
        List<FlowItem> expired;
        lock (_gate)
        {
            expired = _items
                .Where(i => !i.Lifetime.IsPersistent && i.Lifetime.Duration is { } d && i.CreatedAt + d <= now)
                .ToList();
            if (expired.Count == 0)
                return false;
            foreach (var item in expired)
                _items.Remove(item);
        }

        // Transient items are never durable, but delete-through anyway to stay safe.
        foreach (var item in expired)
            DeleteThroughIfDurable(item);
        RaiseChanged();
        return true;
    }

    /// <summary>Durable ⇒ persistent AND entity-backed (non-null DedupKey) AND re-derivable/no action (design §6).</summary>
    private static bool ComputeDurable(FlowItemDraft draft) =>
        draft.RequestDurable
        && draft.Lifetime.IsPersistent
        && !string.IsNullOrEmpty(draft.DedupKey)
        && (draft.Action is null || draft.Action.IsReDerivable);

    /// <summary>Caller holds <see cref="_gate"/>. Returns items removed for capacity (to be delete-through'd).</summary>
    private List<FlowItem> EvictIfNeeded(FlowItem justPublished)
    {
        var evicted = new List<FlowItem>();
        while (_items.Count > Capacity)
        {
            // Oldest by CreatedAt within each tier (a dedup re-publish bumps CreatedAt, so list position
            // is not a reliable age proxy). Prefer transient, then read non-(ActionRequired/Error). The
            // just-published item is never a candidate — a brand-new arrival must not evict itself.
            var victim = _items.Where(i => !ReferenceEquals(i, justPublished) && !i.Lifetime.IsPersistent)
                               .OrderBy(i => i.CreatedAt).FirstOrDefault()
                         ?? _items.Where(i => !ReferenceEquals(i, justPublished) && i.IsRead && !IsProtected(i))
                               .OrderBy(i => i.CreatedAt).FirstOrDefault();
            if (victim is null)
                break; // only protected/unread-persistent items left — leave over capacity rather than drop them
            _items.Remove(victim);
            evicted.Add(victim);
        }

        if (evicted.Count > 0)
            _logger.LogDebug("Flow evicted {Count} item(s) for capacity", evicted.Count);
        return evicted;
    }

    /// <summary>An isolated copy for persistence I/O outside the lock, so a concurrent mutation of the live item can't tear the persisted row.</summary>
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

    private static bool IsProtected(FlowItem item) =>
        item.Severity is FlowSeverity.ActionRequired or FlowSeverity.Error;

    private void OnSweepTick(object? state)
    {
        if (_disposed)
            return;
        try
        {
            Sweep(DateTimeOffset.Now);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Flow sweep failed");
        }
    }

    /// <summary>Delete-through: removes a durable item's persisted row on every removal path (the load-bearing invariant).</summary>
    private void DeleteThroughIfDurable(FlowItem item)
    {
        if (item.Durable)
            SafePersist(() => _persistence.Delete(item.Id));
    }

    private void SafePersist(Action op)
    {
        try
        {
            op();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Flow persistence operation failed");
        }
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        // Join any in-flight sweep so no Changed/persist fires after Dispose returns.
        using var done = new ManualResetEvent(false);
        if (_sweepTimer.Dispose(done))
            done.WaitOne(TimeSpan.FromSeconds(2));
    }
}
