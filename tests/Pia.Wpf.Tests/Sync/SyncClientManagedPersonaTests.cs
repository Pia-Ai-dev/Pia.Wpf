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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Pia.Shared.Sync;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Sync;

/// <summary>Pinned against raw JSON: the absent-key-vs-present-and-empty distinction is a serialization property, so
/// constructing objects would test nothing.</summary>
public class SyncClientManagedPersonaTests : IDisposable
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

    private const string FullSnapshotJson = """
        {
          "serverTimestamp": "2026-08-01T09:14:02.1837744Z",
          "templates": { "upserted": [], "deleted": [] },
          "personas": { "upserted": [], "deleted": [] },
          "providers": { "upserted": [], "deleted": [] },
          "sessions": { "added": [], "deleted": [] },
          "memories": { "upserted": [], "deleted": [] },
          "todos": { "upserted": [], "deleted": [] },
          "kanbanColumns": { "upserted": [], "deleted": [] },
          "scheduledJobs": { "upserted": [], "deleted": [] },
          "researchSessions": { "upserted": [], "deleted": [] },
          "plugins": { "upserted": [], "deleted": [] },
          "catalogVersion": 4194235871203344761,
          "managedPersonas": {
            "personas": [
              {
                "id": "6f1b3f2a-9c44-4d1e-8b77-2a0d5e91c4aa",
                "name": "Brandvoice",
                "tagline": "Rewrites anything in our house voice",
                "systemPrompt": "You are the company's brand voice editor. Rewrite the user's draft ...",
                "guardrails": "Never invent product claims. Never use superlatives without a source.",
                "outputFormat": "Return only the rewritten text, no preamble.",
                "expertise": ["copywriting", "brand", "editing"],
                "archetype": "creative",
                "emoji": "🎨",
                "accentColor": "#7A5AF8",
                "toolScope": 2,
                "reasoningEffort": 4,
                "schemaVersion": 1,
                "createdAt": "2026-07-30T11:02:41.0000000Z",
                "updatedAt": "2026-08-01T08:55:10.0000000Z",
                "isManaged": true
              }
            ],
            "recentlyRemoved": ["b2c7e0d1-5f38-42aa-9a10-71c0d4e9f001"]
          }
        }
        """;

    // The group has no managed personas. Present-and-empty is authoritative: CLEAR the store.
    private const string EmptySnapshotJson = """
        {
          "serverTimestamp": "2026-08-01T09:20:11.4410920Z",
          "catalogVersion": 4194235871203344761,
          "managedPersonas": { "personas": [], "recentlyRemoved": [] }
        }
        """;

    // Catalog fast-skip fired: no managedPersonas key at all. KEEP the store.
    private const string CatalogSkippedJson = """
        {
          "serverTimestamp": "2026-08-01T09:21:44.9910110Z",
          "templates": { "upserted": [], "deleted": [] },
          "personas": { "upserted": [], "deleted": [] },
          "catalogVersion": 4194235871203344761
        }
        """;

    private const long OpaqueCatalogVersion = 4194235871203344761L;
    private static readonly Guid BrandvoiceId = Guid.Parse("6f1b3f2a-9c44-4d1e-8b77-2a0d5e91c4aa");

    // --- The wire contract ---

    [Fact]
    public void Deserialize_WithManagedPersonas_PopulatesTheSnapshotAndEveryRowField()
    {
        var response = JsonSerializer.Deserialize<SyncPullResponse>(FullSnapshotJson, WireOptions);

        Assert.NotNull(response);
        Assert.NotNull(response!.ManagedPersonas);
        var row = Assert.Single(response.ManagedPersonas!.Personas);
        Assert.Equal(BrandvoiceId, row.Id);
        Assert.Equal("Brandvoice", row.Name);
        Assert.Equal("Rewrites anything in our house voice", row.Tagline);
        Assert.StartsWith("You are the company's brand voice editor.", row.SystemPrompt);
        Assert.Equal("Never invent product claims. Never use superlatives without a source.", row.Guardrails);
        Assert.Equal("Return only the rewritten text, no preamble.", row.OutputFormat);
        Assert.Equal(new List<string> { "copywriting", "brand", "editing" }, row.Expertise);
        Assert.Equal("creative", row.Archetype);
        Assert.Equal("🎨", row.Emoji);
        Assert.Equal("#7A5AF8", row.AccentColor);
        Assert.Equal(2, row.ToolScope);
        // 4, not 3: the client enum is None/Minimal/Low/Medium/High/XHigh, so High is 4. The
        // decode fact below asserts ReasoningEffort.High, and the wire int has to agree with it.
        Assert.Equal(4, row.ReasoningEffort);
        Assert.Equal(1, row.SchemaVersion);
        Assert.True(row.IsManaged);
        // recentlyRemoved is read but never consumed as the removal mechanism.
        Assert.Equal(
            Guid.Parse("b2c7e0d1-5f38-42aa-9a10-71c0d4e9f001"),
            Assert.Single(response.ManagedPersonas.RecentlyRemoved));
    }

    [Fact]
    public void Deserialize_WithoutTheKey_YieldsNull_NotAnEmptySnapshot()
    {
        // The server applies WhenWritingNull app-wide, so "the catalog was skipped" arrives as an ABSENT key; a
        // `= new()` initializer on the property would turn every fast-skipped pull into "clear the store".
        var response = JsonSerializer.Deserialize<SyncPullResponse>(CatalogSkippedJson, WireOptions);

        Assert.NotNull(response);
        Assert.Null(response!.ManagedPersonas);
    }

    [Fact]
    public void Deserialize_PresentButEmpty_IsDistinguishableFromAbsent()
    {
        // "The admin unassigned my group from everything" arrives exactly like this and is authoritative, so
        // non-null + empty must not collapse into the absent case.
        var response = JsonSerializer.Deserialize<SyncPullResponse>(EmptySnapshotJson, WireOptions);

        Assert.NotNull(response);
        Assert.NotNull(response!.ManagedPersonas);
        Assert.Empty(response.ManagedPersonas!.Personas);
        Assert.Empty(response.ManagedPersonas.RecentlyRemoved);
    }

    [Fact]
    public void Deserialize_OldUpsertedDeletedShape_YieldsAnEmptyPersonasList()
    {
        // Every sibling channel is { upserted, deleted } and MERGES; this one is { personas, recentlyRemoved } and
        // REPLACES, so a server still on the old shape must read as zero personas rather than be half-read.
        const string legacyJson = """
            {
              "serverTimestamp": "2026-08-01T09:14:02.1837744Z",
              "catalogVersion": 4194235871203344761,
              "managedPersonas": {
                "upserted": [
                  { "id": "6f1b3f2a-9c44-4d1e-8b77-2a0d5e91c4aa", "name": "Brandvoice", "isManaged": true }
                ],
                "deleted": ["b2c7e0d1-5f38-42aa-9a10-71c0d4e9f001"]
              }
            }
            """;

        var response = JsonSerializer.Deserialize<SyncPullResponse>(legacyJson, WireOptions);

        Assert.NotNull(response);
        Assert.NotNull(response!.ManagedPersonas);
        Assert.Empty(response.ManagedPersonas!.Personas);
        Assert.Empty(response.ManagedPersonas.RecentlyRemoved);
        // The snapshot type must not have grown the merge-shaped members either.
        var members = typeof(SyncManagedPersonaSnapshot).GetProperties().Select(p => p.Name).ToHashSet();
        Assert.DoesNotContain("Upserted", members);
        Assert.DoesNotContain("Deleted", members);
    }

    [Fact]
    public void CatalogVersion_RoundTripsAnOpaqueLongWithoutLoss()
    {
        // Not a counter: the server folds the caller's group into it, so int truncation would silently make every
        // echo mismatch and every pull unconditional.
        var response = JsonSerializer.Deserialize<SyncPullResponse>(FullSnapshotJson, WireOptions);

        Assert.Equal(OpaqueCatalogVersion, response!.CatalogVersion);
        // Non-vacuity: the fixture has to be outside int range. Written as an inequality on a truncating round-trip
        // because ordering two catalog versions is exactly the bug this channel forbids.
        Assert.NotEqual(OpaqueCatalogVersion, unchecked((long)(int)OpaqueCatalogVersion));

        var reSerialized = JsonSerializer.Serialize(response, WireOptions);
        var again = JsonSerializer.Deserialize<SyncPullResponse>(reSerialized, WireOptions);
        Assert.Equal(OpaqueCatalogVersion, again!.CatalogVersion);
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

    /// <summary>Snapshots every list handed to <c>ReplaceManagedPersonasAsync</c>, in call order.</summary>
    private readonly List<IReadOnlyList<Persona>> _replaceCalls = [];

    /// <summary>The delete tracker gets its own directory so a pending-delete file left by another test cannot leak into the push body.</summary>
    private SyncClientService CreateSut()
    {
        var dpapiHelper = Substitute.For<DpapiHelper>(NullLogger<DpapiHelper>.Instance);
        var mapper = new SyncMapper(dpapiHelper);
        var trackerDir = Path.Combine(Path.GetTempPath(), "pia-managed-persona-tests", Guid.NewGuid().ToString("N"));
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
        _personaService.ReplaceManagedPersonasAsync(Arg.Do<IReadOnlyList<Persona>>(p => _replaceCalls.Add(p)))
            .Returns(Task.CompletedTask);

        return new SyncClientService(
            _authService, _settingsService, _templateService,
            _providerService, _historyService, _memoryService,
            mapper, _httpClientFactory,
            NullLogger<SyncClientService>.Instance,
            deleteTracker,
            scheduledJobService: _scheduledJobService,
            personaService: _personaService);
    }

    private static async Task InvokePullChangesAsync(SyncClientService sut, HttpClient client, AppSettings settings)
    {
        var method = typeof(SyncClientService)
            .GetMethod("PullChangesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(sut, [client, "http://test", settings])!;
    }

    private static async Task<(int PushedCount, bool PushSucceeded, bool SentChanges)> InvokePushChangesAsync(
        SyncClientService sut, HttpClient client, AppSettings settings)
    {
        var method = typeof(SyncClientService)
            .GetMethod("PushChangesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return await (Task<(int PushedCount, bool PushSucceeded, bool SentChanges)>)method.Invoke(
            sut, [client, "http://test", settings])!;
    }

    /// <summary>Settings of a client that has already synced once and initialized both catalog channels.</summary>
    private static AppSettings SyncedSettings() => new()
    {
        LastSyncTimestamp = new DateTime(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc),
        LastCatalogVersion = OpaqueCatalogVersion,
        LastPullETag = "\"v9-c4194235871203344761-s0\"",
        ManagedPersonaStoreInitialized = true,
        ClientPolicyInitialized = true,
    };

    [Fact]
    public async Task Pull_NonNullChannel_ReplacesTheStoreExactlyOnce_WithTheMappedRows()
    {
        var sut = CreateSut();
        var handler = new RecordingPullHandler((HttpStatusCode.OK, FullSnapshotJson, null));
        using var client = new HttpClient(handler);

        await InvokePullChangesAsync(sut, client, SyncedSettings());

        await _personaService.Received(1).ReplaceManagedPersonasAsync(Arg.Any<IReadOnlyList<Persona>>());
        var applied = Assert.Single(_replaceCalls);
        var persona = Assert.Single(applied);
        Assert.Equal(BrandvoiceId, persona.Id);
        Assert.Equal("Brandvoice", persona.Name);
        Assert.Equal(PersonaToolScope.Full, persona.ToolScope);
        Assert.Equal(ReasoningEffort.High, persona.ReasoningEffort);
        Assert.True(persona.IsManaged);
        Assert.False(persona.IsBuiltIn);
        Assert.Null(persona.PreferredProviderId);
        // recentlyRemoved is NOT processed — it is confirmation, not the mechanism. The one row that arrived
        // is the whole store, so nothing else may be inferred from it.
        Assert.Single(applied);
    }

    [Fact]
    public async Task Pull_AbsentKey_NeverTouchesTheStore()
    {
        // An absent key means the catalog fast-skip fired; replacing with an empty list here — what a
        // "normalize null to empty" reading produces — would wipe every user's managed personas.
        var sut = CreateSut();
        var handler = new RecordingPullHandler((HttpStatusCode.OK, CatalogSkippedJson, null));
        using var client = new HttpClient(handler);

        await InvokePullChangesAsync(sut, client, SyncedSettings());

        await _personaService.DidNotReceive().ReplaceManagedPersonasAsync(Arg.Any<IReadOnlyList<Persona>>());
        Assert.Empty(_replaceCalls);
    }

    [Fact]
    public async Task Pull_PresentButEmptyChannel_ClearsTheStore()
    {
        var sut = CreateSut();
        var handler = new RecordingPullHandler((HttpStatusCode.OK, EmptySnapshotJson, null));
        using var client = new HttpClient(handler);

        await InvokePullChangesAsync(sut, client, SyncedSettings());

        await _personaService.Received(1).ReplaceManagedPersonasAsync(Arg.Any<IReadOnlyList<Persona>>());
        Assert.Empty(Assert.Single(_replaceCalls));
    }

    [Fact]
    public async Task Pull_NotModified_KeepsTheStore()
    {
        // A 304 carries no body, so there is no snapshot to apply; treating it as an empty one would clear the
        // store on every idle cycle.
        var sut = CreateSut();
        var handler = new RecordingPullHandler((HttpStatusCode.NotModified, "", null));
        using var client = new HttpClient(handler);
        var settings = SyncedSettings();

        await InvokePullChangesAsync(sut, client, settings);

        await _personaService.DidNotReceive().ReplaceManagedPersonasAsync(Arg.Any<IReadOnlyList<Persona>>());
        // Non-vacuity: the request really was conditional, so the 304 was the server answering the ETag
        // rather than the handler short-circuiting something else.
        Assert.Equal(settings.LastPullETag, handler.IfNoneMatch[0]);
    }

    [Fact]
    public async Task Pull_ApplyThrows_KeepsTheOldETag_SoTheRetryRefetchesTheSnapshot()
    {
        // Both conditional tokens are persisted only after every apply returned. Storing the new ETag on a failed
        // page would make the server recompute the identical string and answer 304 forever.
        var sut = CreateSut();
        const string ServerETag = "\"v10-c4194235871203344761-s0\"";
        _personaService.ReplaceManagedPersonasAsync(Arg.Any<IReadOnlyList<Persona>>())
            .Returns(Task.FromException(new InvalidOperationException("replace failed")), Task.CompletedTask);
        var handler = new RecordingPullHandler(
            (HttpStatusCode.OK, FullSnapshotJson, ServerETag),
            (HttpStatusCode.OK, FullSnapshotJson, ServerETag));
        using var client = new HttpClient(handler);
        var settings = SyncedSettings();
        var originalETag = settings.LastPullETag;
        settings.LastCatalogVersion = 1;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => InvokePullChangesAsync(sut, client, settings));

        Assert.Equal(originalETag, settings.LastPullETag);
        Assert.Equal(1, settings.LastCatalogVersion);

        // The retry is therefore unconditional in the only sense that matters: it echoes the ETag that
        // already mismatched, so the server answers 200 with the snapshot again — and NOW both tokens move.
        await InvokePullChangesAsync(sut, client, settings);

        Assert.Equal(originalETag, handler.IfNoneMatch[1]);
        Assert.Equal(ServerETag, settings.LastPullETag);
        Assert.Equal(OpaqueCatalogVersion, settings.LastCatalogVersion);
        // The snapshot really was re-delivered to the store on the retry, not just re-requested.
        Assert.Equal(BrandvoiceId, Assert.Single(_replaceCalls[^1]).Id);
    }

    // --- First-run rule ---

    [Fact]
    public async Task Pull_FirstRun_IsUnconditional_ThenRevertsToConditional()
    {
        var sut = CreateSut();
        var handler = new RecordingPullHandler(
            (HttpStatusCode.OK, FullSnapshotJson, null),
            (HttpStatusCode.OK, CatalogSkippedJson, null));
        using var client = new HttpClient(handler);

        // A profile upgraded from a build that predates the channel: it has a stored catalogVersion and a
        // stored ETag, but has never initialized its managed store.
        var settings = SyncedSettings();
        settings.ManagedPersonaStoreInitialized = false;

        await InvokePullChangesAsync(sut, client, settings);

        // Both conditional mechanisms suppressed for exactly this one request.
        Assert.DoesNotContain("catalogVersion=", handler.RequestUris[0]);
        Assert.Null(handler.IfNoneMatch[0]);
        // The snapshot arrived and applied, so the latch closes.
        Assert.True(settings.ManagedPersonaStoreInitialized);
        // The opaque token is echoed back verbatim on the next pull — no truncation, no re-derivation.
        await InvokePullChangesAsync(sut, client, settings);
        Assert.Contains($"catalogVersion={OpaqueCatalogVersion}", handler.RequestUris[1]);
        Assert.Equal("\"v9-c4194235871203344761-s0\"", handler.IfNoneMatch[1]);
    }

    [Fact]
    public async Task Pull_FirstRunAgainstAPreUpgradeServer_StillClosesTheLatch()
    {
        // A pre-upgrade server has no managedPersonas channel at all, so waiting for a non-null block would keep
        // every future pull unconditional and permanently lose the 304 fast path.
        var sut = CreateSut();
        var handler = new RecordingPullHandler((HttpStatusCode.OK, CatalogSkippedJson, null));
        using var client = new HttpClient(handler);

        var settings = SyncedSettings();
        settings.ManagedPersonaStoreInitialized = false;

        await InvokePullChangesAsync(sut, client, settings);

        Assert.DoesNotContain("catalogVersion=", handler.RequestUris[0]);
        Assert.True(settings.ManagedPersonaStoreInitialized);
        // Nothing was applied, though — an absent key still means "keep the store".
        await _personaService.DidNotReceive().ReplaceManagedPersonasAsync(Arg.Any<IReadOnlyList<Persona>>());
    }

    [Fact]
    public async Task Pull_FirstRunFailingWithNon2xx_LeavesTheLatchOpen()
    {
        // The forced pull has to actually succeed. A 5xx that closed the latch would burn the one
        // unconditional pull and leave the store empty behind a current catalogVersion.
        var sut = CreateSut();
        var handler = new RecordingPullHandler((HttpStatusCode.InternalServerError, "", null));
        using var client = new HttpClient(handler);

        var settings = SyncedSettings();
        settings.ManagedPersonaStoreInitialized = false;

        await InvokePullChangesAsync(sut, client, settings);

        Assert.False(settings.ManagedPersonaStoreInitialized);
    }

    [Fact]
    public async Task Pull_FirstRunReturning304_LeavesTheLatchOpen()
    {
        // Belt and braces: the forced pull omits If-None-Match, so a well-behaved server cannot answer
        // 304 here at all. If one does anyway, the latch must not close — a 304 carries no snapshot.
        var sut = CreateSut();
        var handler = new RecordingPullHandler((HttpStatusCode.NotModified, "", null));
        using var client = new HttpClient(handler);

        var settings = SyncedSettings();
        settings.ManagedPersonaStoreInitialized = false;

        await InvokePullChangesAsync(sut, client, settings);

        Assert.False(settings.ManagedPersonaStoreInitialized);
    }

    // --- Never push ---

    [Fact]
    public async Task DeltaPush_NeverIncludesAManagedPersona()
    {
        var sut = CreateSut();
        var lastSync = new DateTime(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc);
        var userPersonaId = Guid.NewGuid();

        // GetPersonasAsync returns built-ins ∪ managed ∪ user rows, so the push builder sees the managed row
        // and has to filter it out itself.
        _personaService.GetPersonasAsync().Returns(new List<Persona>
        {
            new()
            {
                Id = userPersonaId, Name = "Mine", SystemPrompt = "p",
                UpdatedAt = lastSync.AddMinutes(1), IsBuiltIn = false, IsManaged = false,
            },
            new()
            {
                Id = BrandvoiceId, Name = "Brandvoice", SystemPrompt = "p",
                UpdatedAt = lastSync.AddMinutes(1), IsBuiltIn = false, IsManaged = true,
            },
        });

        var handler = new CapturingPushHandler();
        using var client = new HttpClient(handler);

        var settings = SyncedSettings();
        settings.LastSyncTimestamp = lastSync;

        var result = await InvokePushChangesAsync(sut, client, settings);

        Assert.True(result.PushSucceeded);
        Assert.NotNull(handler.LastPushBody);
        using var doc = JsonDocument.Parse(handler.LastPushBody!);
        var upserted = doc.RootElement.GetProperty("personas").GetProperty("upserted");
        // Non-vacuity: the ordinary user persona IS pushed, so the absence below is a filter, not an
        // empty push.
        Assert.Equal(1, upserted.GetArrayLength());
        Assert.Equal(userPersonaId, upserted[0].GetProperty("id").GetGuid());
        Assert.DoesNotContain(BrandvoiceId.ToString(), handler.LastPushBody!);
    }

    [Fact]
    public async Task FirstSyncPush_NeverIncludesAManagedPersona()
    {
        // The first-sync migration has its own persona projection, so it needs its own !IsManaged filter or a
        // brand-new device uploads the whole managed catalog under the signing-in user's name.
        var sut = CreateSut();
        var userPersonaId = Guid.NewGuid();

        _personaService.GetPersonasAsync().Returns(new List<Persona>
        {
            new() { Id = userPersonaId, Name = "Mine", SystemPrompt = "p", IsBuiltIn = false, IsManaged = false },
            new() { Id = BrandvoiceId, Name = "Brandvoice", SystemPrompt = "p", IsBuiltIn = false, IsManaged = true },
        });

        var handler = new CapturingPushHandler();
        _httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));
        _authService.IsLoggedIn.Returns(true);
        _authService.GetAccessTokenAsync().Returns("token");
        _settingsService.GetSettingsAsync().Returns(new AppSettings { SyncEnabled = true, ServerUrl = "http://test" });

        await sut.PerformFirstSyncMigrationAsync();

        Assert.NotNull(handler.LastPushBody);
        using var doc = JsonDocument.Parse(handler.LastPushBody!);
        var upserted = doc.RootElement.GetProperty("personas").GetProperty("upserted");
        Assert.Equal(1, upserted.GetArrayLength());
        Assert.Equal(userPersonaId, upserted[0].GetProperty("id").GetGuid());
        Assert.DoesNotContain(BrandvoiceId.ToString(), handler.LastPushBody!);
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

    /// <summary>Captures the push body (gzipped by PostPushAsync) and answers with a canned 200.</summary>
    private sealed class CapturingPushHandler : HttpMessageHandler
    {
        public string? LastPushBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                if (request.Content.Headers.ContentEncoding.Contains("gzip"))
                {
                    using var input = new MemoryStream(bytes);
                    using var gzip = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
                    using var output = new MemoryStream();
                    await gzip.CopyToAsync(output, cancellationToken);
                    bytes = output.ToArray();
                }
                LastPushBody = Encoding.UTF8.GetString(bytes);
            }

            var body = JsonSerializer.Serialize(new SyncPushResponse { ServerTimestamp = DateTime.UtcNow });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
