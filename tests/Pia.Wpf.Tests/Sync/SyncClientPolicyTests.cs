using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Sync;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Sync;

/// <summary>Pinned against raw JSON: "the key was absent" versus "the key was present and empty" is a
/// serialization property, so constructing objects would test nothing.</summary>
public class SyncClientPolicyTests : IDisposable
{
    private readonly List<string> _trackerDirs = [];

    public void Dispose()
    {
        foreach (var dir in _trackerDirs)
        {
            TempPath.Remove(dir);
        }
    }

    /// <summary><c>PullPageAsync</c> deserializes with no options, so anything but Web defaults would test a serializer the client never runs.</summary>
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Mirrors the server's app-wide config, where a null property is an absent key rather than <c>"key": null</c>.</summary>
    private static readonly JsonSerializerOptions ServerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private const string PolicyDocument =
        """{"defaults":{"uiLanguage":"DE"},"enforce":{"assistantFileToolsEnabled":false}}""";

    private const string DrainPageDocument =
        """{"enforce":{"assistantFileToolsEnabled":true}}""";

    private const string SentinelDocument =
        """{"defaults":{"assistantDefaultWorkingDirectory":"policy-sentinel-zz9"}}""";

    private const string Sentinel = "policy-sentinel-zz9";

    private const string FullPolicyJson = """
        {
          "serverTimestamp": "2026-08-20T09:14:02.1837744Z",
          "templates": { "upserted": [], "deleted": [] },
          "plugins": { "upserted": [], "deleted": [] },
          "managedPersonas": { "personas": [], "recentlyRemoved": [] },
          "clientPolicy": {
            "document": "{\"defaults\":{\"uiLanguage\":\"DE\"},\"enforce\":{\"assistantFileToolsEnabled\":false}}",
            "updatedAt": "2026-08-19T08:55:10.0000000Z"
          },
          "catalogVersion": 4194235871203344761
        }
        """;

    // The group has no policy. Present-and-empty is a real answer: CLEAR the cache. Note updatedAt is
    // absent, not null.
    private const string EmptyPolicyJson = """
        {
          "serverTimestamp": "2026-08-20T09:20:11.4410920Z",
          "clientPolicy": { "document": "{}" },
          "catalogVersion": 4194235871203344761
        }
        """;

    // Catalog fast-skip fired: no clientPolicy key at all. KEEP the cache.
    private const string CatalogSkippedJson = """
        {
          "serverTimestamp": "2026-08-20T09:21:44.9910110Z",
          "templates": { "upserted": [], "deleted": [] },
          "catalogVersion": 4194235871203344761
        }
        """;

    private const string FirstOfTwoPagesJson = """
        {
          "serverTimestamp": "2026-08-20T09:14:02.1837744Z",
          "templates": { "upserted": [], "deleted": [] },
          "managedPersonas": { "personas": [], "recentlyRemoved": [] },
          "clientPolicy": {
            "document": "{\"defaults\":{\"uiLanguage\":\"DE\"},\"enforce\":{\"assistantFileToolsEnabled\":false}}",
            "updatedAt": "2026-08-19T08:55:10.0000000Z"
          },
          "catalogVersion": 4194235871203344761,
          "hasMore": true
        }
        """;

    private const string DrainPageJson = """
        {
          "serverTimestamp": "2026-08-20T09:14:02.1837744Z",
          "templates": { "upserted": [], "deleted": [] },
          "clientPolicy": {
            "document": "{\"enforce\":{\"assistantFileToolsEnabled\":true}}",
            "updatedAt": "2026-08-19T08:55:10.0000000Z"
          },
          "catalogVersion": 4194235871203344761
        }
        """;

    private const string SentinelPolicyJson = """
        {
          "serverTimestamp": "2026-08-20T09:14:02.1837744Z",
          "templates": { "upserted": [], "deleted": [] },
          "clientPolicy": {
            "document": "{\"defaults\":{\"assistantDefaultWorkingDirectory\":\"policy-sentinel-zz9\"}}",
            "updatedAt": "2026-08-19T08:55:10.0000000Z"
          },
          "catalogVersion": 4194235871203344761
        }
        """;

    private const long OpaqueCatalogVersion = 4194235871203344761L;

    // --- The wire contract ---

    [Fact]
    public void Deserialize_WithClientPolicy_PopulatesTheDocumentAndTimestamp()
    {
        var response = JsonSerializer.Deserialize<SyncPullResponse>(FullPolicyJson, WireOptions);

        Assert.NotNull(response);
        Assert.NotNull(response!.ClientPolicy);
        // The document survives as one opaque string, unescaped but not reinterpreted — the client's
        // semantics turn on which keys it contains, and a typed round-trip cannot see that.
        Assert.Equal(PolicyDocument, response.ClientPolicy!.Document);
        Assert.Equal(new DateTime(2026, 8, 19, 8, 55, 10, DateTimeKind.Utc), response.ClientPolicy.UpdatedAt);
    }

    [Fact]
    public void Deserialize_WithoutTheKey_YieldsNull_NotAnEmptyDocument()
    {
        // The server omits nulls app-wide, so a fast-skipped catalog arrives as an ABSENT key. A `= new()`
        // initializer on the property would turn every idle pull into "the admin withdrew the policy".
        var response = JsonSerializer.Deserialize<SyncPullResponse>(CatalogSkippedJson, WireOptions);

        Assert.NotNull(response);
        Assert.Null(response!.ClientPolicy);
    }

    [Fact]
    public void Deserialize_PresentWithoutUpdatedAt_IsDistinguishableFromAbsent()
    {
        var response = JsonSerializer.Deserialize<SyncPullResponse>(EmptyPolicyJson, WireOptions);

        Assert.NotNull(response);
        Assert.NotNull(response!.ClientPolicy);
        Assert.Equal("{}", response.ClientPolicy!.Document);
        // Absent, not null — "this group has never had a policy" and "the admin cleared it" both land here.
        Assert.Null(response.ClientPolicy.UpdatedAt);
    }

    [Fact]
    public void Serialize_OmitsClientPolicy_WhenNull()
    {
        var absent = JsonSerializer.Serialize(
            new SyncPullResponse { ServerTimestamp = DateTime.UtcNow }, ServerOptions);
        var present = JsonSerializer.Serialize(
            new SyncPullResponse
            {
                ServerTimestamp = DateTime.UtcNow,
                ClientPolicy = new SyncClientPolicySnapshot { Document = PolicyDocument },
            },
            ServerOptions);

        Assert.DoesNotContain("clientPolicy", absent, StringComparison.OrdinalIgnoreCase);
        // Positive control: the key is spelled that way and is not suppressed outright.
        Assert.Contains("clientPolicy", present, StringComparison.OrdinalIgnoreCase);
    }

    // --- The apply contract (PullPageAsync) ---

    private readonly IAuthService _authService = Substitute.For<IAuthService>();
    private readonly ISettingsService _settingsService = Substitute.For<ISettingsService>();
    private readonly ITemplateService _templateService = Substitute.For<ITemplateService>();
    private readonly IProviderService _providerService = Substitute.For<IProviderService>();
    private readonly IHistoryService _historyService = Substitute.For<IHistoryService>();
    private readonly IMemoryService _memoryService = Substitute.For<IMemoryService>();
    private readonly IHttpClientFactory _httpClientFactory = Substitute.For<IHttpClientFactory>();
    private readonly IScheduledJobService _scheduledJobService = Substitute.For<IScheduledJobService>();
    private readonly IPersonaService _personaService = Substitute.For<IPersonaService>();
    private readonly IPolicyService _policyService = Substitute.For<IPolicyService>();

    /// <summary>Every document handed to <c>ReplaceServerPolicyAsync</c>, in call order.</summary>
    private readonly List<string> _storedDocuments = [];

    /// <summary>The delete tracker gets its own directory so a pending-delete file left by another test cannot leak into the push body.</summary>
    private SyncClientService CreateSut(ILogger<SyncClientService>? logger = null)
    {
        var dpapiHelper = Substitute.For<DpapiHelper>(NullLogger<DpapiHelper>.Instance);
        var mapper = new SyncMapper(dpapiHelper);
        var trackerDir = Path.Combine(Path.GetTempPath(), "pia-client-policy-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(trackerDir);

        _trackerDirs.Add(trackerDir);
        var deleteTracker = new SyncDeleteTrackerService(trackerDir, NullLogger<SyncDeleteTrackerService>.Instance);

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
        _personaService.GetPersonasAsync().Returns(Array.Empty<Persona>());
        _personaService.ReplaceManagedPersonasAsync(Arg.Any<IReadOnlyList<Persona>>()).Returns(Task.CompletedTask);
        _policyService.ReplaceServerPolicyAsync(Arg.Do<string>(d => _storedDocuments.Add(d)))
            .Returns(Task.CompletedTask);

        return new SyncClientService(
            _authService, _settingsService, _templateService,
            _providerService, _historyService, _memoryService,
            mapper, _httpClientFactory,
            logger ?? NullLogger<SyncClientService>.Instance,
            deleteTracker,
            scheduledJobService: _scheduledJobService,
            personaService: _personaService,
            policyService: _policyService);
    }

    private static async Task InvokePullChangesAsync(SyncClientService sut, HttpClient client, AppSettings settings)
    {
        var method = typeof(SyncClientService)
            .GetMethod("PullChangesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(sut, [client, "http://test", settings])!;
    }

    /// <summary>Settings of a client that has already synced once and initialized both catalog channels.</summary>
    private static AppSettings SyncedSettings() => new()
    {
        LastSyncTimestamp = new DateTime(2026, 8, 20, 9, 0, 0, DateTimeKind.Utc),
        LastCatalogVersion = OpaqueCatalogVersion,
        LastPullETag = "\"v9-c4194235871203344761-s0\"",
        ManagedPersonaStoreInitialized = true,
        ClientPolicyInitialized = true,
    };

    [Fact]
    public async Task Pull_NonNullChannel_StoresTheDocumentExactlyOnce()
    {
        // Two pages, both carrying a policy: the channel rides the catalog block, which only the first page
        // requests, so a drain page's document must be ignored rather than overwrite the real one.
        var sut = CreateSut();
        var handler = new RecordingPullHandler(
            (HttpStatusCode.OK, FirstOfTwoPagesJson, null),
            (HttpStatusCode.OK, DrainPageJson, null));
        using var client = new HttpClient(handler);

        await InvokePullChangesAsync(sut, client, SyncedSettings());

        // Non-vacuity: the drain page really was fetched, so the single store below is the first-page guard.
        Assert.Equal(2, handler.RequestUris.Count);
        await _policyService.Received(1).ReplaceServerPolicyAsync(Arg.Any<string>());
        Assert.Equal(PolicyDocument, Assert.Single(_storedDocuments));
        Assert.DoesNotContain(DrainPageDocument, _storedDocuments);
    }

    [Fact]
    public async Task Pull_AbsentKey_NeverStoresADocument()
    {
        // An absent key means the catalog fast-skip fired. Storing "{}" here — what a "normalize null to
        // empty" reading produces — would withdraw the group's policy on every idle cycle.
        var sut = CreateSut();
        var handler = new RecordingPullHandler((HttpStatusCode.OK, CatalogSkippedJson, null));
        using var client = new HttpClient(handler);

        await InvokePullChangesAsync(sut, client, SyncedSettings());

        await _policyService.DidNotReceive().ReplaceServerPolicyAsync(Arg.Any<string>());
        Assert.Empty(_storedDocuments);
    }

    [Fact]
    public async Task Pull_EmptyDocument_IsStoredAsAuthoritative()
    {
        var sut = CreateSut();
        var handler = new RecordingPullHandler((HttpStatusCode.OK, EmptyPolicyJson, null));
        using var client = new HttpClient(handler);

        await InvokePullChangesAsync(sut, client, SyncedSettings());

        await _policyService.Received(1).ReplaceServerPolicyAsync("{}");
        Assert.Equal("{}", Assert.Single(_storedDocuments));
    }

    [Fact]
    public async Task Pull_NotModified_NeverStoresADocument()
    {
        // A 304 carries no body, so there is no document to store; treating it as an empty one would clear
        // the cache on every idle cycle.
        var sut = CreateSut();
        var handler = new RecordingPullHandler((HttpStatusCode.NotModified, "", null));
        using var client = new HttpClient(handler);
        var settings = SyncedSettings();

        await InvokePullChangesAsync(sut, client, settings);

        await _policyService.DidNotReceive().ReplaceServerPolicyAsync(Arg.Any<string>());
        // Non-vacuity: the request really was conditional, so the 304 was the server answering the ETag
        // rather than the handler short-circuiting something else.
        Assert.Equal(settings.LastPullETag, handler.IfNoneMatch[0]);
    }

    [Fact]
    public async Task Pull_StoreThrows_KeepsTheOldETag_SoTheRetryRefetchesTheDocument()
    {
        // Both conditional tokens are persisted only after every apply returned. Storing them on a failed
        // page would let the server fast-skip a document this page never cached.
        var sut = CreateSut();
        const string ServerETag = "\"v10-c4194235871203344761-s0\"";
        _policyService.ReplaceServerPolicyAsync(Arg.Any<string>())
            .Returns(Task.FromException(new InvalidOperationException("cache write failed")), Task.CompletedTask);
        var handler = new RecordingPullHandler(
            (HttpStatusCode.OK, FullPolicyJson, ServerETag),
            (HttpStatusCode.OK, FullPolicyJson, ServerETag));
        using var client = new HttpClient(handler);
        var settings = SyncedSettings();
        var originalETag = settings.LastPullETag;
        settings.LastCatalogVersion = 1;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => InvokePullChangesAsync(sut, client, settings));

        Assert.Equal(originalETag, settings.LastPullETag);
        Assert.Equal(1, settings.LastCatalogVersion);

        // The retry echoes the ETag that already mismatched, so the server answers 200 with the document
        // again — and NOW both tokens move.
        await InvokePullChangesAsync(sut, client, settings);

        Assert.Equal(originalETag, handler.IfNoneMatch[1]);
        Assert.Equal(ServerETag, settings.LastPullETag);
        Assert.Equal(OpaqueCatalogVersion, settings.LastCatalogVersion);
        Assert.Equal(PolicyDocument, _storedDocuments[^1]);
    }

    // --- First-run rule ---

    [Fact]
    public async Task Pull_FirstRunWithUninitializedPolicy_IsUnconditional_ThenRevertsToConditional()
    {
        var sut = CreateSut();
        var handler = new RecordingPullHandler(
            (HttpStatusCode.OK, FullPolicyJson, null),
            (HttpStatusCode.OK, CatalogSkippedJson, null));
        using var client = new HttpClient(handler);

        // A profile upgraded from a build that predates the policy channel: it has a stored catalogVersion,
        // a stored ETag and an initialized managed store, so the new flag is the only thing forcing this.
        var settings = SyncedSettings();
        settings.ClientPolicyInitialized = false;

        await InvokePullChangesAsync(sut, client, settings);

        Assert.DoesNotContain("catalogVersion=", handler.RequestUris[0]);
        Assert.Null(handler.IfNoneMatch[0]);
        Assert.Equal(PolicyDocument, Assert.Single(_storedDocuments));
        Assert.True(settings.ClientPolicyInitialized);

        // The opaque token is echoed back verbatim on the next pull — no truncation, no re-derivation.
        await InvokePullChangesAsync(sut, client, settings);

        Assert.Contains($"catalogVersion={OpaqueCatalogVersion}", handler.RequestUris[1]);
        Assert.Equal("\"v9-c4194235871203344761-s0\"", handler.IfNoneMatch[1]);
    }

    [Fact]
    public async Task Pull_FirstRunAgainstAPreUpgradeServer_StillClosesTheLatch()
    {
        // A pre-upgrade server has no policy channel at all, so latching only on a non-null block would keep
        // every future pull unconditional and permanently lose the 304 fast path.
        var sut = CreateSut();
        var handler = new RecordingPullHandler((HttpStatusCode.OK, CatalogSkippedJson, null));
        using var client = new HttpClient(handler);

        var settings = SyncedSettings();
        settings.ClientPolicyInitialized = false;

        await InvokePullChangesAsync(sut, client, settings);

        Assert.DoesNotContain("catalogVersion=", handler.RequestUris[0]);
        Assert.True(settings.ClientPolicyInitialized);
        // Nothing was cached, though — an absent key still means "keep what is cached".
        await _policyService.DidNotReceive().ReplaceServerPolicyAsync(Arg.Any<string>());
    }

    // --- Privacy ---

    [Fact]
    public async Task Pull_NeverLogsTheDocumentContent()
    {
        // Admin-authored, so it may name internal hosts and paths, and users attach these logs to support
        // mail. Only the length may be logged.
        var logger = new CapturingLogger<SyncClientService>();
        var sut = CreateSut(logger);
        var handler = new RecordingPullHandler((HttpStatusCode.OK, SentinelPolicyJson, null));
        using var client = new HttpClient(handler);

        await InvokePullChangesAsync(sut, client, SyncedSettings());

        // Non-vacuity: the sentinel really did traverse the pull, and the logger really did capture.
        Assert.Equal(SentinelDocument, Assert.Single(_storedDocuments));
        Assert.NotEmpty(logger.Entries);
        Assert.DoesNotContain(
            logger.Entries,
            e => e.Message.Contains(Sentinel) || (e.Exception?.ToString().Contains(Sentinel) ?? false));
    }

    /// <summary>Records each request's URI and <c>If-None-Match</c>, because the first-run rule is about a header being absent.</summary>
    private sealed class RecordingPullHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body, string? ETag)> _responses;
        public List<string> RequestUris { get; } = [];
        public List<string?> IfNoneMatch { get; } = [];

        public RecordingPullHandler(params (HttpStatusCode Status, string Body, string? ETag)[] responses) =>
            _responses = new Queue<(HttpStatusCode, string, string?)>(responses);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!.ToString());
            IfNoneMatch.Add(request.Headers.IfNoneMatch.Count == 0
                ? null
                : request.Headers.IfNoneMatch.First().Tag);

            var (status, body, etag) = _responses.Count > 0
                ? _responses.Dequeue()
                : (HttpStatusCode.OK, CatalogSkippedJson, null);
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
