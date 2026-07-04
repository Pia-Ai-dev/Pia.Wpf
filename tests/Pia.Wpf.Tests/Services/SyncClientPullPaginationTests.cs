using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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

/// <summary>
/// Tests for the cli-3 client transfer-optimization work (Sec 6.4 / 6.3 / 3.5):
/// chunked first-sync push, opt-in pull pagination (<c>?limit=</c> + <c>HasMore</c> drain), and
/// the plugin-catalog skip round-trip (<c>?catalogVersion=</c> ↔ <c>SyncPullResponse.CatalogVersion</c>).
/// </summary>
public class SyncClientPullPaginationTests
{
    private readonly IAuthService _authService = Substitute.For<IAuthService>();
    private readonly ISettingsService _settingsService = Substitute.For<ISettingsService>();
    private readonly ITemplateService _templateService = Substitute.For<ITemplateService>();
    private readonly IProviderService _providerService = Substitute.For<IProviderService>();
    private readonly IHistoryService _historyService = Substitute.For<IHistoryService>();
    private readonly IMemoryService _memoryService = Substitute.For<IMemoryService>();
    private readonly IHttpClientFactory _httpClientFactory = Substitute.For<IHttpClientFactory>();
    private readonly IScheduledJobService _scheduledJobService = Substitute.For<IScheduledJobService>();

    private SyncClientService CreateSut()
    {
        var dpapiHelper = Substitute.For<DpapiHelper>(NullLogger<DpapiHelper>.Instance);
        var mapper = new SyncMapper(dpapiHelper);
        var deleteTracker = new SyncDeleteTrackerService(Path.GetTempPath(), NullLogger<SyncDeleteTrackerService>.Instance);

        _templateService.GetTemplatesAsync().Returns(Array.Empty<OptimizationTemplate>());
        _providerService.GetProvidersAsync().Returns(Array.Empty<AiProvider>());
        _memoryService.GetAllObjectsAsync().Returns(Array.Empty<MemoryObject>());
        _historyService.SearchSessionsAsync(
            Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<DateTime?>(),
            Arg.Any<DateTime?>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(Array.Empty<OptimizationSession>());
        _historyService.GetSessionsAsync(Arg.Any<int>(), Arg.Any<int>())
            .Returns(Array.Empty<OptimizationSession>());
        _historyService.GetSessionAsync(Arg.Any<Guid>()).Returns((OptimizationSession?)null);
        _scheduledJobService.GetModifiedSinceAsync(Arg.Any<DateTime>()).Returns([]);

        return new SyncClientService(
            _authService, _settingsService, _templateService,
            _providerService, _historyService, _memoryService,
            mapper, _httpClientFactory,
            NullLogger<SyncClientService>.Instance,
            deleteTracker,
            scheduledJobService: _scheduledJobService);
    }

    private static OptimizationSession Session(Guid id) =>
        new() { Id = id, OriginalText = "o", OptimizedText = "p" };

    private static async Task InvokePullChangesAsync(SyncClientService sut, HttpClient client, AppSettings settings)
    {
        var method = typeof(SyncClientService)
            .GetMethod("PullChangesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(sut, [client, "http://test", settings])!;
    }

    private static async Task<(int Pulled, int DecryptionErrors, bool PullSucceeded, DateTime? ServerTimestamp)> InvokePullChangesWithResultAsync(
        SyncClientService sut, HttpClient client, AppSettings settings)
    {
        var method = typeof(SyncClientService)
            .GetMethod("PullChangesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task)method.Invoke(sut, [client, "http://test", settings])!;
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        return ((int, int, bool, DateTime?))result;
    }

    // --- Sec 6.4: chunked first-sync push ---

    [Fact]
    public async Task PerformFirstSyncMigration_SplitsSessionsIntoBatchesAtBoundary()
    {
        var sut = CreateSut();
        // 250 sessions with a batch size of 200 => exactly two push bodies (200 + 50).
        var ids = Enumerable.Range(0, 250).Select(_ => Guid.NewGuid()).ToList();
        var sessions = ids.Select(Session).ToList();
        _historyService.GetSessionsAsync(0, 1000).Returns(sessions);

        var handler = new RecordingHandler();
        _httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));

        _authService.IsLoggedIn.Returns(true);
        _authService.GetAccessTokenAsync().Returns("token");
        _settingsService.GetSettingsAsync().Returns(new AppSettings { SyncEnabled = true, ServerUrl = "http://test" });

        await sut.PerformFirstSyncMigrationAsync();

        // Two chunks => two POSTs to /api/sync/push.
        Assert.Equal(2, handler.PushBodies.Count);

        // Every session id must appear across the combined push bodies (nothing dropped at the split).
        var combined = string.Concat(handler.PushBodies);
        foreach (var id in ids)
            Assert.Contains(id.ToString(), combined);

        // The split is by the session count, not lost or duplicated: 250 total session ids across bodies.
        var firstBatchCount = CountSessionIds(handler.PushBodies[0], ids);
        var secondBatchCount = CountSessionIds(handler.PushBodies[1], ids);
        Assert.Equal(200, firstBatchCount);
        Assert.Equal(50, secondBatchCount);
    }

    [Fact]
    public async Task PerformFirstSyncMigration_NoSessions_StillSendsOneBatch()
    {
        var sut = CreateSut();
        var handler = new RecordingHandler();
        _httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));

        _authService.IsLoggedIn.Returns(true);
        _authService.GetAccessTokenAsync().Returns("token");
        _settingsService.GetSettingsAsync().Returns(new AppSettings { SyncEnabled = true, ServerUrl = "http://test" });

        await sut.PerformFirstSyncMigrationAsync();

        // Even with zero sessions the non-session entities + Settings must still be pushed once.
        Assert.Equal(1, handler.PushBodies.Count);
    }

    private static int CountSessionIds(string body, IReadOnlyList<Guid> ids) =>
        ids.Count(id => body.Contains(id.ToString()));

    // --- Sec 6.3: opt-in pull pagination (HasMore drain) ---

    [Fact]
    public async Task PullChanges_DrainsWhileHasMore_AdvancingSinceCursor()
    {
        var sut = CreateSut();

        var ts1 = new DateTime(2026, 7, 4, 10, 0, 0, DateTimeKind.Utc);
        var ts2 = new DateTime(2026, 7, 4, 11, 0, 0, DateTimeKind.Utc);

        var page1 = Serialize(new SyncPullResponse
        {
            ServerTimestamp = ts1,
            Sessions = new SyncSessionChanges { Added = { new SyncSession { Id = Guid.NewGuid(), OriginalText = "a", OptimizedText = "b" } } },
            HasMore = true,
        });
        var page2 = Serialize(new SyncPullResponse
        {
            ServerTimestamp = ts2,
            Sessions = new SyncSessionChanges { Added = { new SyncSession { Id = Guid.NewGuid(), OriginalText = "c", OptimizedText = "d" } } },
            HasMore = false,
        });

        var handler = new PullHandler(
            (HttpStatusCode.OK, page1, null),
            (HttpStatusCode.OK, page2, null));
        using var client = new HttpClient(handler);

        var settings = new AppSettings();
        await InvokePullChangesAsync(sut, client, settings);

        // HasMore=true on page 1 drives a second pull; HasMore=false on page 2 stops the drain.
        Assert.Equal(2, handler.RequestUris.Count);
        // Every pull carries the ?limit= page cap.
        Assert.All(handler.RequestUris, u => Assert.Contains("limit=", u));
        // The second request's since cursor advanced to page 1's ServerTimestamp (the continuation
        // token). The URL is built as a raw string, so the ISO timestamp is not percent-encoded.
        Assert.Contains($"since={ts1:O}", handler.RequestUris[1]);
    }

    [Fact]
    public async Task PullChanges_LaterPageDrainFailure_KeepsEarlierPagesAndAdvancesCursor()
    {
        var sut = CreateSut();
        var ts1 = new DateTime(2026, 7, 4, 10, 0, 0, DateTimeKind.Utc);

        var page1 = Serialize(new SyncPullResponse
        {
            ServerTimestamp = ts1,
            Sessions = new SyncSessionChanges { Added = { new SyncSession { Id = Guid.NewGuid(), OriginalText = "a", OptimizedText = "b" } } },
            HasMore = true,
        });

        var handler = new PullHandler(
            (HttpStatusCode.OK, page1, null),
            (HttpStatusCode.InternalServerError, "", null));
        using var client = new HttpClient(handler);

        var settings = new AppSettings();
        var result = await InvokePullChangesWithResultAsync(sut, client, settings);

        // Page 1 applied (1 session), page 2 failed: PullSucceeded stays true and the cursor
        // advances to page 1's ServerTimestamp so the next cycle resumes after it instead of
        // re-pulling everything from scratch.
        Assert.Equal(2, handler.RequestUris.Count);
        Assert.True(result.PullSucceeded);
        Assert.Equal(1, result.Pulled);
        Assert.Equal(ts1, result.ServerTimestamp);
    }

    [Fact]
    public async Task PullChanges_HasMoreWithNonAdvancingCursor_StopsAfterFirstPage()
    {
        var sut = CreateSut();
        var body = Serialize(new SyncPullResponse
        {
            ServerTimestamp = DateTime.MinValue,
            HasMore = true,
        });
        var handler = new PullHandler((HttpStatusCode.OK, body, null));
        using var client = new HttpClient(handler);

        await InvokePullChangesAsync(sut, client, new AppSettings());

        // ServerTimestamp does not advance past `since` (DateTime.MinValue) despite HasMore=true,
        // so the strict '>' guard stops the drain instead of looping forever.
        Assert.Equal(1, handler.RequestUris.Count);
    }

    [Fact]
    public async Task PullChanges_SingleResponseNoHasMore_DoesNotDrain()
    {
        var sut = CreateSut();
        var body = Serialize(new SyncPullResponse
        {
            ServerTimestamp = new DateTime(2026, 7, 4, 10, 0, 0, DateTimeKind.Utc),
            // HasMore null (pre-upgrade server) => the loop runs exactly once.
        });
        var handler = new PullHandler((HttpStatusCode.OK, body, null));
        using var client = new HttpClient(handler);

        await InvokePullChangesAsync(sut, client, new AppSettings());

        Assert.Equal(1, handler.RequestUris.Count);
    }

    // --- Sec 3.5: catalog-version round-trip ---

    [Fact]
    public async Task PullChanges_FirstRun_OmitsCatalogVersionThenStoresAndReplaysIt()
    {
        var sut = CreateSut();
        var body = Serialize(new SyncPullResponse
        {
            ServerTimestamp = new DateTime(2026, 7, 4, 10, 0, 0, DateTimeKind.Utc),
            CatalogVersion = 42,
        });
        var handler = new PullHandler(
            (HttpStatusCode.OK, body, null),
            (HttpStatusCode.OK, body, null));
        using var client = new HttpClient(handler);

        var settings = new AppSettings(); // LastCatalogVersion null => first pull omits the param.
        await InvokePullChangesAsync(sut, client, settings);

        Assert.DoesNotContain("catalogVersion=", handler.RequestUris[0]);
        // The echoed catalog version is persisted for the next pull.
        Assert.Equal(42, settings.LastCatalogVersion);

        // A subsequent pull replays it as ?catalogVersion=42 so the server can skip the catalog.
        await InvokePullChangesAsync(sut, client, settings);
        Assert.Contains("catalogVersion=42", handler.RequestUris[1]);
    }

    [Fact]
    public async Task PullChanges_CatalogVersionStoredMidDrain_CarriesIntoNextPageUrl()
    {
        var sut = CreateSut();
        var ts1 = new DateTime(2026, 7, 4, 10, 0, 0, DateTimeKind.Utc);
        var page1 = Serialize(new SyncPullResponse
        {
            ServerTimestamp = ts1,
            CatalogVersion = 42,
            HasMore = true,
        });
        var page2 = Serialize(new SyncPullResponse
        {
            ServerTimestamp = ts1,
            HasMore = false,
        });
        var handler = new PullHandler(
            (HttpStatusCode.OK, page1, null),
            (HttpStatusCode.OK, page2, null));
        using var client = new HttpClient(handler);

        var settings = new AppSettings(); // LastCatalogVersion null on entry.
        await InvokePullChangesAsync(sut, client, settings);

        // Page 1 stores CatalogVersion=42 before its own PullPageAsync call returns (Sec 3.5), so
        // the drain's own second request already carries it — the "persist only after the page is
        // fully applied" fix defers persistence within the same call, not past it.
        Assert.DoesNotContain("catalogVersion=", handler.RequestUris[0]);
        Assert.Contains("catalogVersion=42", handler.RequestUris[1]);
    }

    private static string Serialize(SyncPullResponse response) =>
        JsonSerializer.Serialize(response);

    /// <summary>Records decompressed push bodies for /api/sync/push and answers pulls with an empty 200.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> PushBodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("/api/sync/push"))
            {
                if (request.Content is not null)
                    PushBodies.Add(await ReadBodyAsync(request.Content, cancellationToken));
                var pushBody = JsonSerializer.Serialize(new SyncPushResponse { ServerTimestamp = DateTime.UtcNow });
                return Json(pushBody);
            }

            // Pull: minimal empty response so PerformFirstSyncMigrationAsync's trailing pull succeeds.
            var pullBody = JsonSerializer.Serialize(new SyncPullResponse { ServerTimestamp = DateTime.UtcNow });
            return Json(pullBody);
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private static async Task<string> ReadBodyAsync(HttpContent content, CancellationToken ct)
        {
            var bytes = await content.ReadAsByteArrayAsync(ct);
            if (!content.Headers.ContentEncoding.Contains("gzip"))
                return Encoding.UTF8.GetString(bytes);

            using var input = new MemoryStream(bytes);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            await gzip.CopyToAsync(output, ct);
            return Encoding.UTF8.GetString(output.ToArray());
        }
    }

    /// <summary>Serves a queued sequence of pull responses and records each request URI.</summary>
    private sealed class PullHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body, string? ETag)> _responses;
        public List<string> RequestUris { get; } = new();

        public PullHandler(params (HttpStatusCode Status, string Body, string? ETag)[] responses) =>
            _responses = new Queue<(HttpStatusCode, string, string?)>(responses);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!.ToString());
            var (status, body, etag) = _responses.Count > 0
                ? _responses.Dequeue()
                : (HttpStatusCode.OK, JsonSerializer.Serialize(new SyncPullResponse { ServerTimestamp = DateTime.UtcNow }), null);
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (etag is not null)
                response.Headers.ETag = new EntityTagHeaderValue(etag);
            return Task.FromResult(response);
        }
    }
}
