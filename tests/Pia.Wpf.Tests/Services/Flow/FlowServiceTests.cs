using Microsoft.Extensions.Logging.Abstractions;
using Pia.Models.Flow;
using Pia.Services.Flow;
using Xunit;

namespace Pia.Tests.Services.Flow;

public class FlowServiceTests
{
    private static FlowService Create(out FakeFlowPersistenceStore persistence)
    {
        persistence = new FakeFlowPersistenceStore();
        return new FlowService(persistence, NullLogger<FlowService>.Instance);
    }

    private static FlowItemDraft Snackbar(FlowSeverity severity = FlowSeverity.Info, string title = "t") => new()
    {
        Severity = severity,
        Source = FlowSource.Snackbar,
        Title = title,
        Lifetime = severity is FlowSeverity.Warning or FlowSeverity.Error or FlowSeverity.ActionRequired
            ? FlowLifetime.Persistent
            : FlowLifetime.Transient(TimeSpan.FromSeconds(4)),
        DedupKey = null,
        RequestDurable = false,
    };

    private static FlowItemDraft EntityBacked(string dedupKey, FlowSeverity severity = FlowSeverity.Warning, string title = "t", FlowAction? action = null) => new()
    {
        Severity = severity,
        Source = FlowSource.TodoDeadline,
        Title = title,
        Lifetime = FlowLifetime.Persistent,
        DedupKey = dedupKey,
        Action = action,
        RequestDurable = true,
    };

    [Fact]
    public void Publish_AddsItemToSnapshot()
    {
        var service = Create(out _);
        var item = service.Publish(Snackbar());

        Assert.Single(service.Snapshot);
        Assert.Equal(item.Id, service.Snapshot[0].Id);
        Assert.NotEqual(Guid.Empty, item.Id);
    }

    [Fact]
    public void Publish_EntityBackedPersistentReDerivable_IsDurableAndPersisted()
    {
        var service = Create(out var persistence);
        var item = service.Publish(EntityBacked("todo-1", action: new OpenTodoAction(Guid.NewGuid(), "Open")));

        Assert.True(item.Durable);
        Assert.Equal(1, persistence.UpsertCount);
        Assert.Contains(item.Id, persistence.Store.Keys);
    }

    [Fact]
    public void Publish_InvokeAction_ForcedSessionOnly()
    {
        var service = Create(out var persistence);
        var draft = new FlowItemDraft
        {
            Severity = FlowSeverity.ActionRequired,
            Source = FlowSource.Snackbar,
            Title = "t",
            Lifetime = FlowLifetime.Persistent,
            DedupKey = "k", // even with a key
            Action = new InvokeAction(() => { }, "Undo"),
            RequestDurable = true, // even when requested
        };

        var item = service.Publish(draft);

        Assert.False(item.Durable);
        Assert.Equal(0, persistence.UpsertCount);
    }

    [Fact]
    public void Publish_NullDedupKey_NotDurableEvenWhenRequested()
    {
        var service = Create(out var persistence);
        var draft = new FlowItemDraft
        {
            Severity = FlowSeverity.Error,
            Source = FlowSource.InAppToast,
            Title = "t",
            Lifetime = FlowLifetime.Persistent,
            DedupKey = null,
            RequestDurable = true,
        };

        Assert.False(service.Publish(draft).Durable);
        Assert.Equal(0, persistence.UpsertCount);
    }

    [Fact]
    public void Publish_TransientRequestDurable_ForcedFalse()
    {
        var service = Create(out _);
        var draft = new FlowItemDraft
        {
            Severity = FlowSeverity.Info,
            Source = FlowSource.TodoDeadline,
            Title = "t",
            Lifetime = FlowLifetime.Transient(TimeSpan.FromSeconds(3)),
            DedupKey = "k",
            RequestDurable = true,
        };

        Assert.False(service.Publish(draft).Durable);
    }

    [Fact]
    public void Dedup_SameKey_UpdatesInPlace()
    {
        var service = Create(out _);
        service.Publish(EntityBacked("chat-1", FlowSeverity.Success, "first"));
        service.Publish(EntityBacked("chat-1", FlowSeverity.Error, "second"));

        Assert.Single(service.Snapshot);
        var item = service.Snapshot[0];
        Assert.Equal("second", item.Title);
        Assert.Equal(FlowSeverity.Error, item.Severity);
    }

    [Fact]
    public void Dedup_NullKey_DoesNotCollapse()
    {
        var service = Create(out _);
        service.Publish(Snackbar(title: "a"));
        service.Publish(Snackbar(title: "b"));

        Assert.Equal(2, service.Snapshot.Count);
    }

    [Fact]
    public void Dismiss_RemovesById_AndDeletesDurable()
    {
        var service = Create(out var persistence);
        var item = service.Publish(EntityBacked("todo-1"));

        service.Dismiss(item.Id);

        Assert.Empty(service.Snapshot);
        Assert.Equal(1, persistence.DeleteCount);
        Assert.DoesNotContain(item.Id, persistence.Store.Keys);
    }

    [Fact]
    public void Retract_RemovesByKey_AndDeletesDurable()
    {
        var service = Create(out var persistence);
        service.Publish(EntityBacked("todo-7"));

        service.Retract("todo-7");

        Assert.Empty(service.Snapshot);
        Assert.Equal(1, persistence.DeleteCount);
    }

    [Fact]
    public void MarkRead_SetsReadAndPersists()
    {
        var service = Create(out var persistence);
        var item = service.Publish(EntityBacked("todo-1"));

        service.MarkRead(item.Id);

        Assert.True(service.Snapshot[0].IsRead);
        Assert.Equal(2, persistence.UpsertCount); // publish + mark-read
    }

    [Fact]
    public void Clear_RemovesAll_AndDeletesAll()
    {
        var service = Create(out var persistence);
        service.Publish(EntityBacked("a"));
        service.Publish(EntityBacked("b"));

        service.Clear();

        Assert.Empty(service.Snapshot);
        Assert.Equal(1, persistence.DeleteAllCount);
    }

    [Fact]
    public void Sweep_RemovesExpiredTransient_KeepsPersistent()
    {
        var service = Create(out _);
        var transient = service.Publish(Snackbar(FlowSeverity.Info));
        service.Publish(Snackbar(FlowSeverity.Error)); // persistent

        var afterExpiry = transient.CreatedAt + TimeSpan.FromSeconds(4) + TimeSpan.FromMilliseconds(1);
        var changed = service.Sweep(afterExpiry);

        Assert.True(changed);
        Assert.Single(service.Snapshot);
        Assert.Equal(FlowSeverity.Error, service.Snapshot[0].Severity);
    }

    [Fact]
    public void Sweep_BeforeExpiry_KeepsTransient()
    {
        var service = Create(out _);
        var transient = service.Publish(Snackbar(FlowSeverity.Info));

        var beforeExpiry = transient.CreatedAt + TimeSpan.FromSeconds(4) - TimeSpan.FromMilliseconds(1);

        Assert.False(service.Sweep(beforeExpiry));
        Assert.Single(service.Snapshot);
    }

    [Fact]
    public void Capacity_EvictsOldestTransientFirst()
    {
        var service = Create(out _);
        var first = service.Publish(Snackbar(FlowSeverity.Info, "oldest"));
        for (var i = 0; i < FlowService.Capacity; i++) // total now Capacity + 1
            service.Publish(Snackbar(FlowSeverity.Info, $"item-{i}"));

        Assert.Equal(FlowService.Capacity, service.Snapshot.Count);
        Assert.DoesNotContain(service.Snapshot, x => x.Id == first.Id);
    }

    [Fact]
    public void Capacity_NeverEvictsErrorOrActionRequired()
    {
        var service = Create(out _);
        for (var i = 0; i < FlowService.Capacity + 1; i++)
            service.Publish(Snackbar(FlowSeverity.Error, $"err-{i}")); // all protected, unread, persistent

        // Nothing is evictable, so the store is allowed to exceed capacity rather than drop an Error.
        Assert.Equal(FlowService.Capacity + 1, service.Snapshot.Count);
    }

    [Fact]
    public void Capacity_EvictsReadNonProtected_AfterTransientExhausted()
    {
        var service = Create(out _);
        var readWarning = service.Publish(Snackbar(FlowSeverity.Warning, "old-read-warning")); // persistent, non-protected
        service.MarkRead(readWarning.Id);
        for (var i = 0; i < FlowService.Capacity; i++) // fill with unread Errors (protected)
            service.Publish(Snackbar(FlowSeverity.Error, $"err-{i}"));

        Assert.Equal(FlowService.Capacity, service.Snapshot.Count);
        Assert.DoesNotContain(service.Snapshot, x => x.Id == readWarning.Id);
    }

    [Fact]
    public void ItemArrived_RaisedOnPublish()
    {
        var service = Create(out _);
        FlowItem? arrived = null;
        service.ItemArrived += (_, item) => arrived = item;

        var published = service.Publish(Snackbar());

        Assert.NotNull(arrived);
        Assert.Equal(published.Id, arrived!.Id);
    }

    [Fact]
    public void Capacity_DoesNotEvictTheJustPublishedItem()
    {
        var service = Create(out _);
        for (var i = 0; i < FlowService.Capacity; i++) // fill with protected (unevictable) items
            service.Publish(Snackbar(FlowSeverity.Error, $"err-{i}"));

        var fresh = service.Publish(Snackbar(FlowSeverity.Info, "fresh-transient"));

        // The new transient must not evict itself; the store exceeds capacity instead.
        Assert.Equal(FlowService.Capacity + 1, service.Snapshot.Count);
        Assert.Contains(service.Snapshot, x => x.Id == fresh.Id);
    }

    [Fact]
    public void Dedup_DurabilityDowngrade_DeletesPersistedRow()
    {
        var service = Create(out var persistence);
        var item = service.Publish(EntityBacked("k1", action: new OpenTodoAction(Guid.NewGuid(), "Open")));
        Assert.True(item.Durable);
        Assert.Contains(item.Id, persistence.Store.Keys);

        // Re-publish the same key without durability → the stale persisted row must be deleted.
        service.Publish(new FlowItemDraft
        {
            Severity = FlowSeverity.Warning,
            Source = FlowSource.TodoDeadline,
            Title = "downgraded",
            Lifetime = FlowLifetime.Persistent,
            DedupKey = "k1",
            Action = null,
            RequestDurable = false,
        });

        Assert.False(service.Snapshot[0].Durable);
        Assert.DoesNotContain(item.Id, persistence.Store.Keys);
    }
}
