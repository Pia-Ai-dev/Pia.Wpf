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
using Pia.Shared.Sync;
using Xunit;

namespace Pia.Tests.Services;

// A tombstone that arrived from the server must not be re-enqueued as a local deletion: the push would
// re-stamp the server row and the same tombstone would come back on every cycle, forever.
public class SyncPullDeleteEchoTests : IDisposable
{
    private readonly IAuthService _authService = Substitute.For<IAuthService>();
    private readonly ISettingsService _settingsService = Substitute.For<ISettingsService>();
    private readonly ITemplateService _templateService = Substitute.For<ITemplateService>();
    private readonly IProviderService _providerService = Substitute.For<IProviderService>();
    private readonly IHistoryService _historyService = Substitute.For<IHistoryService>();
    private readonly IMemoryService _memoryService = Substitute.For<IMemoryService>();
    private readonly IHttpClientFactory _httpClientFactory = Substitute.For<IHttpClientFactory>();
    private readonly ITodoService _todoService = Substitute.For<ITodoService>();
    private readonly IScheduledJobService _scheduledJobService = Substitute.For<IScheduledJobService>();
    private readonly IPersonaService _personaService = Substitute.For<IPersonaService>();

    private readonly string _trackerDir = Path.Combine(
        Path.GetTempPath(), "pia-pull-delete-echo-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(_trackerDir, recursive: true); } catch (IOException) { }
    }

    private SyncDeleteTrackerService CreateTracker()
    {
        Directory.CreateDirectory(_trackerDir);
        return new SyncDeleteTrackerService(_trackerDir, NullLogger<SyncDeleteTrackerService>.Instance);
    }

    private SyncClientService CreateSut(SyncDeleteTrackerService tracker)
    {
        var dpapiHelper = Substitute.For<DpapiHelper>(NullLogger<DpapiHelper>.Instance);

        _templateService.GetTemplatesAsync().Returns(Array.Empty<OptimizationTemplate>());
        _providerService.GetProvidersAsync().Returns(Array.Empty<AiProvider>());
        _memoryService.GetAllObjectsAsync().Returns(Array.Empty<MemoryObject>());
        _personaService.GetPersonasAsync().Returns(Array.Empty<Persona>());

        return new SyncClientService(
            _authService, _settingsService, _templateService,
            _providerService, _historyService, _memoryService,
            new SyncMapper(dpapiHelper), _httpClientFactory,
            NullLogger<SyncClientService>.Instance,
            tracker,
            todoService: _todoService,
            scheduledJobService: _scheduledJobService,
            personaService: _personaService);
    }

    private static async Task InvokePullChangesAsync(SyncClientService sut, HttpClient client, AppSettings settings)
    {
        var method = typeof(SyncClientService)
            .GetMethod("PullChangesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(sut, [client, "http://test", settings])!;
    }

    [Fact]
    public async Task PullChanges_AppliesServerTombstones_WithoutEnqueueingThemForPush()
    {
        var personaId = Guid.NewGuid();
        var todoId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        var response = new SyncPullResponse { ServerTimestamp = DateTime.UtcNow };
        response.Personas.Deleted.Add(personaId);
        response.Todos.Deleted.Add(todoId);
        response.ScheduledJobs.Deleted.Add(jobId);

        var tracker = CreateTracker();
        var sut = CreateSut(tracker);
        _authService.GetAccessTokenAsync().Returns("token");
        var client = new HttpClient(new SingleResponseHandler(JsonSerializer.Serialize(response)));

        await InvokePullChangesAsync(sut, client, new AppSettings { SyncEnabled = true, ServerUrl = "http://test" });

        await _personaService.Received(1).DeletePersonaAsync(personaId, trackForSync: false);
        await _todoService.Received(1).DeleteAsync(todoId, trackForSync: false);
        await _scheduledJobService.Received(1).DeleteAsync(jobId, trackForSync: false);

        var pending = tracker.GetPendingDeletes();
        Assert.Empty(pending.GetValueOrDefault("personas", []));
        Assert.Empty(pending.GetValueOrDefault("todos", []));
        Assert.Empty(pending.GetValueOrDefault("scheduledJobs", []));
    }

    [Fact]
    public void LocalDelete_StillEnqueuesForPush()
    {
        var tracker = CreateTracker();
        var id = Guid.NewGuid();

        tracker.TrackDeletion("personas", id);

        Assert.Equal([id], tracker.GetPendingDeletes()["personas"]);
    }

    private sealed class SingleResponseHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
