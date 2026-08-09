using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure;
using Pia.Models.Flow;
using Pia.Services.Flow;
using Xunit;

namespace Pia.Tests.Services.Flow;

public sealed class FlowPersistenceStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteContext _context;
    private FlowPersistenceStore? _store;
    private readonly List<FlowService> _services = new();

    public FlowPersistenceStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"flow-test-{Guid.NewGuid():N}.db");
        _context = new SqliteContext(_dbPath);
    }

    private FlowPersistenceStore Store() => _store ??= new FlowPersistenceStore(_context, NullLogger<FlowPersistenceStore>.Instance);

    private FlowService NewService(IFlowPersistenceStore persistence)
    {
        var service = new FlowService(persistence, NullLogger<FlowService>.Instance);
        _services.Add(service);
        return service;
    }

    private static FlowItem Durable(string dedupKey, FlowAction? action = null) => new()
    {
        Id = Guid.NewGuid(),
        CreatedAt = DateTimeOffset.Now,
        Severity = FlowSeverity.Warning,
        Source = FlowSource.TodoDeadline,
        Title = "Pay the rent",
        Body = "due within 24h",
        DedupKey = dedupKey,
        Lifetime = FlowLifetime.Persistent,
        IsRead = false,
        Action = action,
        Durable = true,
    };

    [Fact]
    public void Upsert_Then_ReadAll_RoundTripsFieldsAndAction()
    {
        var store = Store();
        var chatId = Guid.NewGuid();
        var item = Durable("todo-1", new OpenChatAction(chatId, "Open chat"));

        store.Upsert(item);
        var reloaded = store.ReadAll();

        var only = Assert.Single(reloaded);
        Assert.Equal(item.Id, only.Id);
        Assert.Equal(FlowSeverity.Warning, only.Severity);
        Assert.Equal(FlowSource.TodoDeadline, only.Source);
        Assert.Equal("Pay the rent", only.Title);
        Assert.Equal("due within 24h", only.Body);
        Assert.Equal("todo-1", only.DedupKey);
        Assert.True(only.Durable);
        Assert.True(only.Lifetime.IsPersistent);
        var action = Assert.IsType<OpenChatAction>(only.Action);
        Assert.Equal(chatId, action.ChatId);
        Assert.Equal("Open chat", action.Label);
    }

    [Fact]
    public void OpenRunAction_RoundTrips_Unchanged()
    {
        var store = Store();
        var runId = Guid.NewGuid();
        var item = new FlowItem
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.Now,
            Severity = FlowSeverity.Success,
            Source = FlowSource.AgentRun,
            Title = "Agent run",
            Body = "Finished",
            DedupKey = "run-1",
            Lifetime = FlowLifetime.Persistent,
            IsRead = false,
            Action = new OpenRunAction(runId, "Open run"),
            Durable = true,
        };

        store.Upsert(item);
        var only = Assert.Single(store.ReadAll());

        Assert.Equal(FlowSource.AgentRun, only.Source);
        var action = Assert.IsType<OpenRunAction>(only.Action);
        Assert.Equal(FlowActionKind.OpenRun, action.Kind);
        Assert.Equal(runId, action.RunId);
        Assert.Equal("Open run", action.Label);
    }

    [Fact]
    public void ContinueRunAction_RoundTrips_Unchanged()
    {
        var store = Store();
        var runId = Guid.NewGuid();
        var item = new FlowItem
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.Now,
            Severity = FlowSeverity.ActionRequired,
            Source = FlowSource.AgentRun,
            Title = "Agent run",
            Body = "Stopped at its budget",
            DedupKey = "run-2",
            Lifetime = FlowLifetime.Persistent,
            IsRead = false,
            Action = new ContinueRunAction(runId, "Continue run"),
            Durable = true,
        };

        store.Upsert(item);
        var only = Assert.Single(store.ReadAll());

        Assert.Equal(FlowSource.AgentRun, only.Source);
        var action = Assert.IsType<ContinueRunAction>(only.Action);
        Assert.Equal(FlowActionKind.ContinueRun, action.Kind);
        Assert.Equal(runId, action.RunId);
        Assert.Equal("Continue run", action.Label);
    }

    [Fact]
    public void OpenParkedRunAction_RoundTrips_Unchanged()
    {
        var store = Store();
        var runId = Guid.NewGuid();
        var item = new FlowItem
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.Now,
            Severity = FlowSeverity.ActionRequired,
            Source = FlowSource.AgentRun,
            Title = "Agent run",
            Body = "Waiting for you to clarify the goal",
            DedupKey = "run-3",
            Lifetime = FlowLifetime.Persistent,
            IsRead = false,
            Action = new OpenParkedRunAction(runId, "Open run"),
            Durable = true,
        };

        store.Upsert(item);
        var only = Assert.Single(store.ReadAll());

        Assert.Equal(FlowSource.AgentRun, only.Source);
        var action = Assert.IsType<OpenParkedRunAction>(only.Action);
        Assert.Equal(FlowActionKind.OpenParkedRun, action.Kind);
        Assert.Equal(runId, action.RunId);
        Assert.Equal("Open run", action.Label);
    }

    [Fact]
    public void Upsert_SameId_Replaces()
    {
        var store = Store();
        var item = Durable("todo-1");
        store.Upsert(item);
        item.Title = "Updated";
        item.IsRead = true;
        store.Upsert(item);

        var only = Assert.Single(store.ReadAll());
        Assert.Equal("Updated", only.Title);
        Assert.True(only.IsRead);
    }

    [Fact]
    public void Delete_RemovesRow()
    {
        var store = Store();
        var item = Durable("todo-1");
        store.Upsert(item);

        store.Delete(item.Id);

        Assert.Empty(store.ReadAll());
    }

    [Fact]
    public async Task DurableItem_SurvivesReload()
    {
        var store = Store();
        var service1 = NewService(store);
        service1.Publish(new FlowItemDraft
        {
            Severity = FlowSeverity.Warning,
            Source = FlowSource.TodoDeadline,
            Title = "t",
            Lifetime = FlowLifetime.Persistent,
            DedupKey = "todo-99",
            RequestDurable = true,
        });

        var service2 = NewService(store);
        await service2.LoadAsync();

        Assert.Single(service2.Snapshot);
        Assert.Equal("todo-99", service2.Snapshot[0].DedupKey);
    }

    [Fact]
    public async Task RetractedDurableItem_DoesNotResurrectOnReload()
    {
        var store = Store();
        var service1 = NewService(store);
        service1.Publish(new FlowItemDraft
        {
            Severity = FlowSeverity.Warning,
            Source = FlowSource.TodoDeadline,
            Title = "t",
            Lifetime = FlowLifetime.Persistent,
            DedupKey = "todo-99",
            RequestDurable = true,
        });

        service1.Retract("todo-99");

        var service2 = NewService(store);
        await service2.LoadAsync();

        Assert.Empty(service2.Snapshot);
    }

    public void Dispose()
    {
        foreach (var service in _services)
            service.Dispose();
        _store?.Dispose();
        _context.Dispose();
        try
        {
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }
        catch (IOException)
        {
            // best-effort temp cleanup
        }
    }
}
