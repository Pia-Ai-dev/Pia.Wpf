using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
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

/// <summary>
/// Regression tests for the 2026-07-04 E2EE-window stranding fixes (H3): plugin
/// preferences must survive a failed/403 push (peek-then-clear-on-success) and a
/// prefs-only cycle must not be short-circuited away; the onboarding full re-push
/// (PerformFirstSyncMigrationAsync) must include scheduled jobs so they are not stranded
/// behind the advancing pull cursor.
/// </summary>
public class SyncClientServiceE2EEWindowTests
{
    private readonly IAuthService _authService = Substitute.For<IAuthService>();
    private readonly ISettingsService _settingsService = Substitute.For<ISettingsService>();
    private readonly ITemplateService _templateService = Substitute.For<ITemplateService>();
    private readonly IProviderService _providerService = Substitute.For<IProviderService>();
    private readonly IHistoryService _historyService = Substitute.For<IHistoryService>();
    private readonly IMemoryService _memoryService = Substitute.For<IMemoryService>();
    private readonly IHttpClientFactory _httpClientFactory = Substitute.For<IHttpClientFactory>();
    private readonly IPluginService _pluginService = Substitute.For<IPluginService>();
    private readonly IScheduledJobService _scheduledJobService = Substitute.For<IScheduledJobService>();

    private SyncClientService CreateSut()
    {
        var dpapiHelper = Substitute.For<DpapiHelper>(NullLogger<DpapiHelper>.Instance);
        var mapper = new SyncMapper(dpapiHelper);
        var deleteTracker = new SyncDeleteTrackerService(Path.GetTempPath(), NullLogger<SyncDeleteTrackerService>.Instance);

        // Empty local stores so the push/migration request builds cleanly.
        _templateService.GetTemplatesAsync().Returns(Array.Empty<OptimizationTemplate>());
        _providerService.GetProvidersAsync().Returns(Array.Empty<AiProvider>());
        _memoryService.GetAllObjectsAsync().Returns(Array.Empty<MemoryObject>());
        _historyService.SearchSessionsAsync(
            Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<DateTime?>(),
            Arg.Any<DateTime?>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(Array.Empty<OptimizationSession>());
        _historyService.GetSessionsAsync(Arg.Any<int>(), Arg.Any<int>())
            .Returns(Array.Empty<OptimizationSession>());

        return new SyncClientService(
            _authService, _settingsService, _templateService,
            _providerService, _historyService, _memoryService,
            mapper, _httpClientFactory,
            NullLogger<SyncClientService>.Instance,
            deleteTracker,
            scheduledJobService: _scheduledJobService,
            pluginService: _pluginService);
    }

    private static async Task<(int PushedCount, bool PushSucceeded, bool SentChanges)> InvokePushChangesAsync(SyncClientService sut, HttpClient client, AppSettings settings)
    {
        var method = typeof(SyncClientService)
            .GetMethod("PushChangesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return await (Task<(int PushedCount, bool PushSucceeded, bool SentChanges)>)method.Invoke(sut, [client, "http://test", settings])!;
    }

    [Fact]
    public async Task PushChanges_On403E2EERequired_DoesNotClearPluginPrefs()
    {
        var sut = CreateSut();
        _pluginService.GetPendingPreferenceChanges().Returns(
            [new SyncPluginPreference { PluginId = Guid.NewGuid(), IsEnabled = true }]);

        var handler = new CapturingHandler(HttpStatusCode.Forbidden, """{"error":"e2ee_required"}""");
        using var client = new HttpClient(handler);

        var result = await InvokePushChangesAsync(sut, client, new AppSettings());

        Assert.Equal(0, result.PushedCount);
        Assert.False(result.PushSucceeded); // a failed push must be distinguishable from a no-op push
        Assert.False(result.SentChanges);
        Assert.Equal(1, handler.PushCount); // it actually attempted the POST (not short-circuited)
        // The pending prefs must NOT be dropped — the push failed, so they persist for retry.
        _pluginService.DidNotReceive().ClearPreferenceChangesAfterSuccessfulPush();
    }

    [Fact]
    public async Task PushChanges_PrefsOnly_OnSuccess_PushesAndClearsPrefs()
    {
        var sut = CreateSut();
        // Only a plugin pref is dirty — nothing else. Must still push (not short-circuit).
        _pluginService.GetPendingPreferenceChanges().Returns(
            [new SyncPluginPreference { PluginId = Guid.NewGuid(), IsEnabled = false }]);

        var okBody = System.Text.Json.JsonSerializer.Serialize(new SyncPushResponse { ServerTimestamp = DateTime.UtcNow });
        var handler = new CapturingHandler(HttpStatusCode.OK, okBody);
        using var client = new HttpClient(handler);

        var result = await InvokePushChangesAsync(sut, client, new AppSettings());

        Assert.Equal(1, handler.PushCount); // prefs-only cycle was NOT short-circuited
        Assert.True(result.PushSucceeded);
        // PushedCount only counts upserts, but a prefs-only push must still report SentChanges
        // so a prefs-only push resets backoff instead of being misclassified as idle.
        Assert.True(result.SentChanges);
        _pluginService.Received(1).ClearPreferenceChangesAfterSuccessfulPush();
    }

    [Fact]
    public async Task PerformFirstSyncMigration_IncludesScheduledJobs()
    {
        var sut = CreateSut();
        var jobId = Guid.NewGuid();
        _scheduledJobService.GetModifiedSinceAsync(Arg.Any<DateTime>())
            .Returns([new ScheduledJob { Id = jobId, Name = "nightly", Query = "summarize" }]);

        var okBody = System.Text.Json.JsonSerializer.Serialize(new SyncPushResponse { ServerTimestamp = DateTime.UtcNow });
        var handler = new CapturingHandler(HttpStatusCode.OK, okBody);
        _httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));

        _authService.IsLoggedIn.Returns(true);
        _authService.GetAccessTokenAsync().Returns("token");
        _settingsService.GetSettingsAsync().Returns(new AppSettings
        {
            SyncEnabled = true,
            ServerUrl = "http://test"
        });

        await sut.PerformFirstSyncMigrationAsync();

        Assert.Equal(1, handler.PushCount);
        Assert.NotNull(handler.LastPushBody);
        // The migration request is now gzipped; CapturingHandler decompresses it back to JSON,
        // which must carry the scheduled job.
        Assert.Contains(jobId.ToString(), handler.LastPushBody!);
    }

    private sealed class CapturingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int PushCount { get; private set; }
        public string? LastPushBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("/api/sync/push"))
            {
                PushCount++;
                if (request.Content is not null)
                    LastPushBody = await ReadBodyAsync(request.Content, cancellationToken);
            }
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            };
        }

        // Both push sites gzip the body (Content-Encoding: gzip); decompress so assertions can
        // inspect the JSON. Falls back to a plain read for any uncompressed content.
        private static async Task<string> ReadBodyAsync(HttpContent content, CancellationToken ct)
        {
            var bytes = await content.ReadAsByteArrayAsync(ct);
            if (!content.Headers.ContentEncoding.Contains("gzip"))
                return System.Text.Encoding.UTF8.GetString(bytes);

            using var input = new MemoryStream(bytes);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            await gzip.CopyToAsync(output, ct);
            return System.Text.Encoding.UTF8.GetString(output.ToArray());
        }
    }
}
