using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.E2EE;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Pia.Shared.Sync;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Tests for the 2026-07-04 sync transfer-optimization client work (plan Sec 5.2-5.4): the shared
/// camelCase/null-eliding push serializer, gzip on both push sites, and the settings-hash gate
/// (unchanged settings omitted; a settings-only change still POSTs despite the no-change
/// short-circuit). Deterministic-hash tests live alongside as they gate this behavior.
/// </summary>
public class SyncClientServiceTransferOptimizationTests
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

    private SyncClientService CreateSut(IE2EEService? e2ee = null)
    {
        var dpapiHelper = Substitute.For<DpapiHelper>(NullLogger<DpapiHelper>.Instance);
        var mapper = new SyncMapper(dpapiHelper, e2ee);
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
        _scheduledJobService.GetModifiedSinceAsync(Arg.Any<DateTime>())
            .Returns(Array.Empty<ScheduledJob>());
        _pluginService.GetPendingPreferenceChanges().Returns([]);

        return new SyncClientService(
            _authService, _settingsService, _templateService,
            _providerService, _historyService, _memoryService,
            mapper, _httpClientFactory,
            NullLogger<SyncClientService>.Instance,
            deleteTracker,
            scheduledJobService: _scheduledJobService,
            pluginService: _pluginService,
            e2ee: e2ee);
    }

    private static async Task<(int PushedCount, bool PushSucceeded, bool SentChanges)> InvokePushChangesAsync(SyncClientService sut, HttpClient client, AppSettings settings)
    {
        var method = typeof(SyncClientService)
            .GetMethod("PushChangesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return await (Task<(int PushedCount, bool PushSucceeded, bool SentChanges)>)method.Invoke(sut, [client, "http://test", settings])!;
    }

    private static HttpClient OkClient(CapturingHandler handler) => new(handler);

    // --- Sec 5.4: settings-hash gate on the delta push ---

    [Fact]
    public async Task DeltaPush_UnchangedSettings_OmitsSettingsFromBody()
    {
        var sut = CreateSut();
        // A plugin pref forces the POST so we can inspect the body; settings are unchanged.
        _pluginService.GetPendingPreferenceChanges().Returns(
            [new SyncPluginPreference { PluginId = Guid.NewGuid(), IsEnabled = true }]);

        var settings = new AppSettings();
        settings.LastPushedSettingsHash = SyncMapper.ComputeSettingsHash(settings);

        var handler = new CapturingHandler(HttpStatusCode.OK, PushResponseJson());
        using var client = OkClient(handler);

        var result = await InvokePushChangesAsync(sut, client, settings);

        Assert.Equal(1, handler.PushCount);
        Assert.True(result.PushSucceeded);
        using var doc = JsonDocument.Parse(handler.LastPushBody!);
        Assert.False(doc.RootElement.TryGetProperty("settings", out _),
            "unchanged settings must be omitted (null + WhenWritingNull elides the key)");
    }

    [Fact]
    public async Task DeltaPush_SettingsOnlyChange_StillPosts_AndIncludesSettings_AndPersistsHash()
    {
        var sut = CreateSut();
        // Nothing else is dirty (no prefs, no deletes, empty stores) — only the settings changed.
        var settings = new AppSettings { LastPushedSettingsHash = null };

        var handler = new CapturingHandler(HttpStatusCode.OK, PushResponseJson());
        using var client = OkClient(handler);

        var result = await InvokePushChangesAsync(sut, client, settings);

        // Must NOT be short-circuited despite pushedCount/deletes/prefs all being zero.
        Assert.Equal(1, handler.PushCount);
        Assert.True(result.PushSucceeded);
        Assert.True(result.SentChanges); // counts as activity for backoff purposes

        using var doc = JsonDocument.Parse(handler.LastPushBody!);
        Assert.True(doc.RootElement.TryGetProperty("settings", out _), "changed settings must be present");

        // The hash is persisted only after the successful push, so a subsequent identical push skips.
        Assert.Equal(SyncMapper.ComputeSettingsHash(settings), settings.LastPushedSettingsHash);
    }

    [Fact]
    public async Task DeltaPush_NoChangesAndUnchangedSettings_ShortCircuits()
    {
        var sut = CreateSut();
        var settings = new AppSettings();
        settings.LastPushedSettingsHash = SyncMapper.ComputeSettingsHash(settings);

        var handler = new CapturingHandler(HttpStatusCode.OK, PushResponseJson());
        using var client = OkClient(handler);

        var result = await InvokePushChangesAsync(sut, client, settings);

        Assert.Equal(0, handler.PushCount); // short-circuited: no entity/pref/delete/settings change
        Assert.True(result.PushSucceeded);
        Assert.False(result.SentChanges);
    }

    // --- Sec 5.2: shared serializer (camelCase + null elision) on the delta push body ---

    [Fact]
    public async Task DeltaPush_Body_IsCamelCase_AndElidesNulls()
    {
        var sut = CreateSut();
        var settings = new AppSettings { SyncDeviceId = null, LastPushedSettingsHash = null }; // settings change forces POST

        var handler = new CapturingHandler(HttpStatusCode.OK, PushResponseJson());
        using var client = OkClient(handler);

        await InvokePushChangesAsync(sut, client, settings);

        using var doc = JsonDocument.Parse(handler.LastPushBody!);
        var root = doc.RootElement;
        // camelCase naming policy.
        Assert.True(root.TryGetProperty("clientTimestamp", out _));
        Assert.True(root.TryGetProperty("isE2EEEncrypted", out _));
        Assert.False(root.TryGetProperty("ClientTimestamp", out _), "PascalCase must not appear");
        // Null elision: DeviceId was null, so the key is absent entirely.
        Assert.False(root.TryGetProperty("deviceId", out _), "null DeviceId must be elided by WhenWritingNull");
    }

    // --- Sec 5.3: gzip the first-sync push ---

    [Fact]
    public async Task FirstSyncPush_IsGzipped_AndDecompressesToValidJson()
    {
        var sut = CreateSut();
        var handler = new CapturingHandler(HttpStatusCode.OK, PushResponseJson());
        _httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));

        _authService.IsLoggedIn.Returns(true);
        _authService.GetAccessTokenAsync().Returns("token");
        _settingsService.GetSettingsAsync().Returns(new AppSettings { SyncEnabled = true, ServerUrl = "http://test" });

        await sut.PerformFirstSyncMigrationAsync();

        Assert.Equal(1, handler.PushCount);
        Assert.Contains("gzip", handler.LastPushContentEncoding);
        // Decompressed body parses as an object carrying the push envelope.
        using var doc = JsonDocument.Parse(handler.LastPushBody!);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.True(doc.RootElement.TryGetProperty("clientTimestamp", out _));
    }

    // --- First sync after a local-data clear: never push reset preferences over the account's ---

    private AppSettings ArrangeFirstSync(CapturingHandler handler, bool suppressSettingsPush)
    {
        _httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));
        _authService.IsLoggedIn.Returns(true);
        _authService.GetAccessTokenAsync().Returns("token");
        var settings = new AppSettings
        {
            SyncEnabled = true,
            ServerUrl = "http://test",
            SuppressSettingsPushAfterReset = suppressSettingsPush
        };
        _settingsService.GetSettingsAsync().Returns(settings);
        return settings;
    }

    private static string PullResponseJson(SyncSettings? accountSettings = null) =>
        JsonSerializer.Serialize(new SyncPullResponse { ServerTimestamp = DateTime.UtcNow, Settings = accountSettings });

    [Fact]
    public async Task FirstSync_AfterReset_AppliesTheAccountsSettings()
    {
        var sut = CreateSut();
        var handler = new CapturingHandler(HttpStatusCode.OK, PushResponseJson(),
            HttpStatusCode.OK, PullResponseJson(new SyncSettings { Theme = (int)AppTheme.Dark }));
        var settings = ArrangeFirstSync(handler, suppressSettingsPush: true);

        await sut.PerformFirstSyncMigrationAsync();

        Assert.Equal(AppTheme.Dark, settings.Theme);
    }

    [Fact]
    public async Task FirstSync_AfterReset_OmitsSettings_ThenClearsTheFlag()
    {
        var sut = CreateSut();
        var handler = new CapturingHandler(HttpStatusCode.OK, PushResponseJson(), HttpStatusCode.OK, PullResponseJson());
        var settings = ArrangeFirstSync(handler, suppressSettingsPush: true);

        await sut.PerformFirstSyncMigrationAsync();

        Assert.NotEmpty(handler.PushBodies);
        foreach (var body in handler.PushBodies)
        {
            using var doc = JsonDocument.Parse(body);
            Assert.False(doc.RootElement.TryGetProperty("settings", out _), "reset preferences must not be pushed");
        }
        Assert.Null(settings.LastPushedSettingsHash);
        Assert.NotNull(settings.LastSyncTimestamp);
        Assert.False(settings.SuppressSettingsPushAfterReset);
    }

    [Fact]
    public async Task FirstSync_AfterReset_PullFails_KeepsTheFlag()
    {
        var sut = CreateSut();
        var handler = new CapturingHandler(HttpStatusCode.OK, PushResponseJson(), HttpStatusCode.InternalServerError, "{}");
        var settings = ArrangeFirstSync(handler, suppressSettingsPush: true);

        await sut.PerformFirstSyncMigrationAsync();

        Assert.Null(settings.LastSyncTimestamp);
        Assert.True(settings.SuppressSettingsPushAfterReset);
    }

    [Fact]
    public async Task FirstSync_AfterReset_UndecryptableAccountSettings_KeepsTheFlag()
    {
        var e2ee = Substitute.For<IE2EEService>();
        e2ee.IsReady().Returns(true);
        e2ee.DecryptRecord<SyncSettings>(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(_ => throw new System.Security.Cryptography.CryptographicException("bad tag"));
        var sut = CreateSut(e2ee);
        var handler = new CapturingHandler(HttpStatusCode.OK, PushResponseJson(), HttpStatusCode.OK,
            PullResponseJson(new SyncSettings { EncryptedPayload = "cipher", WrappedDek = "dek" }));
        var settings = ArrangeFirstSync(handler, suppressSettingsPush: true);
        settings.SyncUserId = "user-1";

        await sut.PerformFirstSyncMigrationAsync();

        Assert.NotNull(settings.LastSyncTimestamp);
        Assert.True(settings.SuppressSettingsPushAfterReset);
    }

    [Fact]
    public async Task DeltaPush_AfterReset_OmitsSettings()
    {
        var sut = CreateSut();
        // A plugin pref forces the POST so the body can be inspected.
        _pluginService.GetPendingPreferenceChanges().Returns(
            [new SyncPluginPreference { PluginId = Guid.NewGuid(), IsEnabled = true }]);
        var settings = new AppSettings { LastPushedSettingsHash = null, SuppressSettingsPushAfterReset = true };

        var handler = new CapturingHandler(HttpStatusCode.OK, PushResponseJson());
        using var client = OkClient(handler);

        var result = await InvokePushChangesAsync(sut, client, settings);

        Assert.Equal(1, handler.PushCount);
        Assert.True(result.PushSucceeded);
        using var doc = JsonDocument.Parse(handler.LastPushBody!);
        Assert.False(doc.RootElement.TryGetProperty("settings", out _), "reset preferences must not be pushed");
        Assert.Null(settings.LastPushedSettingsHash);
        Assert.True(settings.SuppressSettingsPushAfterReset);
    }

    [Fact]
    public async Task FirstSync_WithoutReset_StillSendsSettings()
    {
        var sut = CreateSut();
        var handler = new CapturingHandler(HttpStatusCode.OK, PushResponseJson(), HttpStatusCode.OK, PullResponseJson());
        var settings = ArrangeFirstSync(handler, suppressSettingsPush: false);

        await sut.PerformFirstSyncMigrationAsync();

        using var doc = JsonDocument.Parse(handler.PushBodies[0]);
        Assert.True(doc.RootElement.TryGetProperty("settings", out _), "the first sync must send local settings");
        Assert.Equal(SyncMapper.ComputeSettingsHash(settings), settings.LastPushedSettingsHash);
    }

    private static string PushResponseJson() =>
        JsonSerializer.Serialize(new SyncPushResponse { ServerTimestamp = DateTime.UtcNow });

    /// <summary>Captures the gzip-decompressed push bodies; pulls answer 304 unless a pull response is given.</summary>
    private sealed class CapturingHandler(
        HttpStatusCode pushStatus,
        string pushBody,
        HttpStatusCode pullStatus = HttpStatusCode.NotModified,
        string? pullBody = null) : HttpMessageHandler
    {
        public int PushCount { get; private set; }
        public string? LastPushBody { get; private set; }
        public List<string> PushBodies { get; } = [];
        public string LastPushContentEncoding { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("/api/sync/push"))
            {
                PushCount++;
                if (request.Content is not null)
                {
                    LastPushContentEncoding = string.Join(",", request.Content.Headers.ContentEncoding);
                    var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                    LastPushBody = request.Content.Headers.ContentEncoding.Contains("gzip")
                        ? Decompress(bytes)
                        : System.Text.Encoding.UTF8.GetString(bytes);
                    PushBodies.Add(LastPushBody);
                }
                return new HttpResponseMessage(pushStatus)
                {
                    Content = new StringContent(pushBody, System.Text.Encoding.UTF8, "application/json")
                };
            }

            if (pullBody is null)
                return new HttpResponseMessage(pullStatus);
            return new HttpResponseMessage(pullStatus)
            {
                Content = new StringContent(pullBody, System.Text.Encoding.UTF8, "application/json")
            };
        }

        private static string Decompress(byte[] bytes)
        {
            using var input = new MemoryStream(bytes);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return System.Text.Encoding.UTF8.GetString(output.ToArray());
        }
    }
}
