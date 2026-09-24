using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Pia.Shared.Sync;
using Xunit;

namespace Pia.Tests.Services;

// Per-entity merge rules the pull applies: append-only sessions, last-write-wins columns, the column
// a todo without one falls back to, and which tombstones are honoured.
public class SyncPullApplyTests : IDisposable
{
    private readonly IAuthService _authService = Substitute.For<IAuthService>();
    private readonly ISettingsService _settingsService = Substitute.For<ISettingsService>();
    private readonly ITemplateService _templateService = Substitute.For<ITemplateService>();
    private readonly IProviderService _providerService = Substitute.For<IProviderService>();
    private readonly IHistoryService _historyService = Substitute.For<IHistoryService>();
    private readonly IMemoryService _memoryService = Substitute.For<IMemoryService>();
    private readonly IHttpClientFactory _httpClientFactory = Substitute.For<IHttpClientFactory>();
    private readonly ITodoService _todoService = Substitute.For<ITodoService>();
    private readonly IKanbanColumnService _columnService = Substitute.For<IKanbanColumnService>();

    private readonly string _trackerDir = Path.Combine(
        Path.GetTempPath(), "pia-pull-apply-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(_trackerDir, recursive: true); } catch (IOException) { }
    }

    private SyncClientService CreateSut()
    {
        Directory.CreateDirectory(_trackerDir);
        var dpapiHelper = Substitute.For<DpapiHelper>(NullLogger<DpapiHelper>.Instance);

        _authService.GetAccessTokenAsync().Returns("token");
        _templateService.GetTemplatesAsync().Returns(Array.Empty<OptimizationTemplate>());
        _providerService.GetProvidersAsync().Returns(Array.Empty<AiProvider>());
        _memoryService.GetAllObjectsAsync().Returns(Array.Empty<MemoryObject>());
        _memoryService.GetObjectAsync(Arg.Any<Guid>()).Returns((MemoryObject?)null);
        _historyService.GetSessionAsync(Arg.Any<Guid>()).Returns((OptimizationSession?)null);
        _todoService.GetAsync(Arg.Any<Guid>()).Returns((TodoItem?)null);
        _columnService.GetAsync(Arg.Any<Guid>()).Returns((KanbanColumn?)null);

        return new SyncClientService(
            _authService, _settingsService, _templateService,
            _providerService, _historyService, _memoryService,
            new SyncMapper(dpapiHelper), _httpClientFactory,
            NullLogger<SyncClientService>.Instance,
            new SyncDeleteTrackerService(_trackerDir, NullLogger<SyncDeleteTrackerService>.Instance),
            todoService: _todoService,
            columnService: _columnService);
    }

    private static async Task<(int Pulled, int DecryptionErrors, bool PullSucceeded, DateTime? ServerTimestamp)> PullAsync(
        SyncClientService sut, SyncPullResponse response, HttpStatusCode status = HttpStatusCode.OK)
    {
        using var client = new HttpClient(new SingleResponseHandler(status, JsonSerializer.Serialize(response)));
        var method = typeof(SyncClientService)
            .GetMethod("PullChangesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return await (Task<(int, int, bool, DateTime?)>)method.Invoke(
            sut, [client, "http://test", new AppSettings { SyncEnabled = true, ServerUrl = "http://test" }])!;
    }

    private static SyncPullResponse EmptyResponse() => new() { ServerTimestamp = DateTime.UtcNow };

    private static KanbanColumn Column(Guid id, string name, DateTime updatedAt) =>
        new() { Id = id, Name = name, UpdatedAt = updatedAt };

    private static SyncSession RemoteSession(Guid id) => new()
    {
        Id = id,
        OriginalText = "before",
        OptimizedText = "after",
        CreatedAt = DateTime.UtcNow
    };

    // --- Sessions: append-only, never updated in place ---

    [Fact]
    public async Task PullSession_NewRemote_IsImported()
    {
        var id = Guid.NewGuid();
        var response = EmptyResponse();
        response.Sessions.Added.Add(RemoteSession(id));

        await PullAsync(CreateSut(), response);

        await _historyService.Received(1).AddSessionAsync(Arg.Is<OptimizationSession>(s => s.Id == id));
    }

    [Fact]
    public async Task PullSession_AlreadyPresent_IsSkipped()
    {
        var id = Guid.NewGuid();
        var sut = CreateSut();
        _historyService.GetSessionAsync(id).Returns(new OptimizationSession
        {
            Id = id,
            OriginalText = "before",
            OptimizedText = "after"
        });

        var response = EmptyResponse();
        response.Sessions.Added.Add(RemoteSession(id));

        await PullAsync(sut, response);

        await _historyService.DidNotReceive().AddSessionAsync(Arg.Any<OptimizationSession>());
    }

    [Fact]
    public async Task PullSession_Deleted_IsDeleted()
    {
        var id = Guid.NewGuid();
        var response = EmptyResponse();
        response.Sessions.Deleted.Add(id);

        await PullAsync(CreateSut(), response);

        await _historyService.Received(1).DeleteSessionAsync(id);
    }

    // --- Tombstones beyond the personas/todos/jobs the echo test seeds ---

    [Fact]
    public async Task PullTemplate_Deleted_IsDeletedWithoutTrackingForPush()
    {
        var id = Guid.NewGuid();
        var response = EmptyResponse();
        response.Templates.Deleted.Add(id);

        await PullAsync(CreateSut(), response);

        await _templateService.Received(1).DeleteTemplateAsync(id, trackForSync: false);
    }

    [Fact]
    public async Task PullProvider_Deleted_IsDeletedWithoutTrackingForPush()
    {
        var id = Guid.NewGuid();
        var response = EmptyResponse();
        response.Providers.Deleted.Add(id);

        await PullAsync(CreateSut(), response);

        await _providerService.Received(1).DeleteProviderAsync(id, trackForSync: false);
    }

    [Fact]
    public async Task PullMemory_Deleted_IsDeletedWithoutTrackingForPush()
    {
        var id = Guid.NewGuid();
        var response = EmptyResponse();
        response.Memories.Deleted.Add(id);

        await PullAsync(CreateSut(), response);

        await _memoryService.Received(1).DeleteObjectAsync(id, trackForSync: false);
    }

    // --- Kanban columns: last-write-wins on UpdatedAt, deletions deliberately not applied ---

    [Fact]
    public async Task PullKanbanColumn_NewRemote_IsImported()
    {
        var id = Guid.NewGuid();
        var response = EmptyResponse();
        response.KanbanColumns.Upserted.Add(new SyncKanbanColumn
        {
            Id = id,
            Name = "Doing",
            UpdatedAt = DateTime.UtcNow
        });

        await PullAsync(CreateSut(), response);

        await _columnService.Received(1).ImportAsync(Arg.Is<KanbanColumn>(c => c.Id == id));
    }

    [Fact]
    public async Task PullKanbanColumn_RemoteNewer_IsUpdated()
    {
        var id = Guid.NewGuid();
        var sut = CreateSut();
        _columnService.GetAsync(id).Returns(Column(id, "Doing", DateTime.UtcNow.AddMinutes(-10)));

        var response = EmptyResponse();
        response.KanbanColumns.Upserted.Add(new SyncKanbanColumn
        {
            Id = id,
            Name = "In progress",
            UpdatedAt = DateTime.UtcNow
        });

        await PullAsync(sut, response);

        await _columnService.Received(1).ImportAsync(Arg.Is<KanbanColumn>(c => c.Name == "In progress"));
    }

    [Fact]
    public async Task PullKanbanColumn_RemoteOlder_IsSkipped()
    {
        var id = Guid.NewGuid();
        var sut = CreateSut();
        _columnService.GetAsync(id).Returns(Column(id, "Doing", DateTime.UtcNow));

        var response = EmptyResponse();
        response.KanbanColumns.Upserted.Add(new SyncKanbanColumn
        {
            Id = id,
            Name = "stale",
            UpdatedAt = DateTime.UtcNow.AddMinutes(-10)
        });

        await PullAsync(sut, response);

        await _columnService.DidNotReceive().ImportAsync(Arg.Any<KanbanColumn>());
    }

    [Fact]
    public async Task PullKanbanColumn_Deleted_IsIgnored()
    {
        var response = EmptyResponse();
        response.KanbanColumns.Deleted.Add(Guid.NewGuid());

        await PullAsync(CreateSut(), response);

        await _columnService.DidNotReceive().DeleteAsync(Arg.Any<Guid>());
    }

    // --- A todo that arrives without a column lands in one picked from its status ---

    [Fact]
    public async Task PullTodo_WithoutColumnId_Completed_LandsInTheClosedColumn()
    {
        var closedId = Guid.NewGuid();
        var sut = CreateSut();
        _columnService.GetClosedColumnAsync().Returns(Column(closedId, "Done", DateTime.UtcNow));

        var response = EmptyResponse();
        response.Todos.Upserted.Add(new SyncTodo
        {
            Id = Guid.NewGuid(),
            Title = "ship it",
            Status = (int)TodoStatus.Completed,
            ColumnId = null,
            UpdatedAt = DateTime.UtcNow
        });

        await PullAsync(sut, response);

        await _todoService.Received(1).ImportAsync(Arg.Is<TodoItem>(t => t.ColumnId == closedId));
    }

    [Fact]
    public async Task PullTodo_WithoutColumnId_Pending_LandsInTheDefaultColumn()
    {
        var defaultId = Guid.NewGuid();
        var sut = CreateSut();
        _columnService.GetDefaultViewColumnAsync().Returns(Column(defaultId, "Inbox", DateTime.UtcNow));

        var response = EmptyResponse();
        response.Todos.Upserted.Add(new SyncTodo
        {
            Id = Guid.NewGuid(),
            Title = "ship it",
            Status = (int)TodoStatus.Pending,
            ColumnId = null,
            UpdatedAt = DateTime.UtcNow
        });

        await PullAsync(sut, response);

        await _todoService.Received(1).ImportAsync(Arg.Is<TodoItem>(t => t.ColumnId == defaultId));
    }

    // --- Settings ---

    [Fact]
    public async Task PullSettings_Present_AppliesOntoTheLocalSettingsAndSavesThem()
    {
        var local = new AppSettings { AutoTypeDelayMs = 5 };
        var sut = CreateSut();
        _settingsService.GetSettingsAsync().Returns(local);

        var response = EmptyResponse();
        response.Settings = new SyncSettings { AutoTypeDelayMs = 42 };

        await PullAsync(sut, response);

        Assert.Equal(42, local.AutoTypeDelayMs);
        await _settingsService.Received(1).SaveSettingsAsync(local);
    }

    [Fact]
    public async Task PullSettings_Absent_NeverReadsTheLocalSettings()
    {
        // The catalog-version latch saves the pull's own AppSettings instance at the end of the page,
        // so the read is the only unambiguous signal that the settings-apply branch ran.
        await PullAsync(CreateSut(), EmptyResponse());

        await _settingsService.DidNotReceive().GetSettingsAsync();
    }

    // --- Page outcome ---

    [Fact]
    public async Task PullChanges_Pulled_IsTheSumAcrossEveryUpsertedCollection()
    {
        var response = EmptyResponse();
        for (var i = 0; i < 2; i++)
            response.Templates.Upserted.Add(new SyncTemplate { Id = Guid.NewGuid(), Name = "t" + i, Prompt = "p" });
        for (var i = 0; i < 3; i++)
            response.Sessions.Added.Add(RemoteSession(Guid.NewGuid()));
        response.Memories.Upserted.Add(new SyncMemory
        {
            Id = Guid.NewGuid(),
            Type = "note",
            Label = "m",
            Data = "{}",
            UpdatedAt = DateTime.UtcNow
        });

        var result = await PullAsync(CreateSut(), response);

        Assert.Equal(6, result.Pulled);
        Assert.True(result.PullSucceeded);
    }

    [Fact]
    public async Task PullChanges_FirstPageHttpFailure_ReportsPullFailed()
    {
        var result = await PullAsync(CreateSut(), EmptyResponse(), HttpStatusCode.InternalServerError);

        Assert.False(result.PullSucceeded);
        Assert.Equal(0, result.Pulled);
        Assert.Null(result.ServerTimestamp);
    }

    private sealed class SingleResponseHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
