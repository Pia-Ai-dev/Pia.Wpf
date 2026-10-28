using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
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

/// <summary>A revoked device must stop the knocking: the server says device_revoked on every
/// sync write and read, and each of those answers must end background sync with one
/// notice.</summary>
public class SyncClientServiceDeviceGoneTests
{
    private readonly IAuthService _authService = Substitute.For<IAuthService>();
    private readonly IDeviceKeyService _deviceKeys = Substitute.For<IDeviceKeyService>();
    private readonly IDeviceManagementService _deviceMgmt = Substitute.For<IDeviceManagementService>();
    private readonly IHistoryService _historyService = Substitute.For<IHistoryService>();
    private readonly IHttpClientFactory _httpClientFactory = Substitute.For<IHttpClientFactory>();
    private readonly IMemoryService _memoryService = Substitute.For<IMemoryService>();
    private readonly IPluginService _pluginService = Substitute.For<IPluginService>();
    private readonly IProviderService _providerService = Substitute.For<IProviderService>();
    private readonly ISettingsService _settingsService = Substitute.For<ISettingsService>();
    private readonly ITemplateService _templateService = Substitute.For<ITemplateService>();

    [Fact]
    public async Task ASecondSyncSession_RaisesAgain()
    {
        var sut = CreateSut(HttpStatusCode.Forbidden, """{"error":"device_revoked"}""");
        var count = 0;
        sut.CurrentDeviceRevoked += (_, _) => count++;

        using (var client = JsonClient(HttpStatusCode.Forbidden, """{"error":"device_revoked"}"""))
            await InvokePushChangesAsync(sut, client);
        _authService.LoginStateChanged += Raise.Event<EventHandler<bool>>(this, true);
        sut.StartBackgroundSync();
        using (var client = JsonClient(HttpStatusCode.Forbidden, """{"error":"device_revoked"}"""))
            await InvokePushChangesAsync(sut, client);

        Assert.Equal(2, count);
        sut.Dispose();
    }

    [Fact]
    public async Task RevocationWithoutSubscribers_ForgetsStoredAndCachedKeysAndRetainsTheNotice()
    {
        var e2ee = new E2EEService(
            new CryptoService(), _deviceKeys, new IdentityDpapi(), _settingsService,
            NullLogger<E2EEService>.Instance);
        using var sut = CreateSut(HttpStatusCode.Forbidden, """{"error":"device_revoked"}""", e2ee);
        var stored = await _settingsService.GetSettingsAsync();
        stored.IsE2EEEnabled = true;
        stored.E2EEUmkVersion = 2;
        stored.E2EERecoveryConfigured = true;
        await e2ee.StoreUmkAsync(Enumerable.Repeat((byte)42, 32).ToArray());
        var cached = e2ee.LoadUmk()!;
        sut.StartBackgroundSync();

        using var client = JsonClient(HttpStatusCode.Forbidden, """{"error":"device_revoked"}""");
        await InvokePushChangesAsync(sut, client);

        Assert.False(sut.IsSyncActive);
        Assert.False(stored.IsE2EEEnabled);
        Assert.Null(stored.E2EEEncryptedUmk);
        Assert.Equal(0, stored.E2EEUmkVersion);
        Assert.False(stored.E2EERecoveryConfigured);
        Assert.Equal(new byte[32], cached);
        Assert.True(sut.PendingDeviceRevocation?.WasRevoked);
        Assert.True(sut.IsE2EEOnboardingRequired);

        sut.StartBackgroundSync();
        Assert.False(sut.IsSyncActive);
        sut.NotifyE2EEOnboardingCompleted();
        Assert.Null(sut.PendingDeviceRevocation);
        sut.StartBackgroundSync();
        Assert.True(sut.IsSyncActive);
    }

    [Fact]
    public async Task SignOut_ClearsTheRetainedRemovalNotice()
    {
        using var sut = CreateSut(HttpStatusCode.Forbidden, """{"error":"device_revoked"}""");
        using var client = JsonClient(HttpStatusCode.Forbidden, """{"error":"device_revoked"}""");
        await InvokePushChangesAsync(sut, client);

        _authService.LoginStateChanged += Raise.Event<EventHandler<bool>>(this, false);

        Assert.Null(sut.PendingDeviceRevocation);
        Assert.False(sut.IsE2EEOnboardingRequired);
    }

    // The first-sync push shares the verdict: it throws after the notice, exactly once per session.
    [Fact]
    public async Task FirstSyncPush_OnDeviceRevoked_NoticesOnceAndStaysFailed()
    {
        var sut = CreateSut(HttpStatusCode.Forbidden, """{"error":"device_revoked"}""");
        var count = 0;
        sut.CurrentDeviceRevoked += (_, _) => count++;

        await Assert.ThrowsAsync<HttpRequestException>(() => sut.PerformFirstSyncMigrationAsync());
        using (var client = JsonClient(HttpStatusCode.Forbidden, """{"error":"device_revoked"}"""))
            await InvokePushChangesAsync(sut, client);

        Assert.Equal(1, count);
        sut.Dispose();
    }

    [Theory]
    [InlineData(true)]  // a Revoked row stays marked as revoked
    [InlineData(false)] // absent from the list entirely: the deleted-or-expired shape
    public async Task PendingDevicesCheck_TellsRevokedApartFromMissing(bool revokedRow)
    {
        var sut = CreateSut(HttpStatusCode.OK, "{}");
        _deviceKeys.GetDeviceId().Returns("device-1");
        _deviceMgmt.GetDevicesAsync().Returns(new Shared.E2EE.DeviceListResponse
        {
            Devices = revokedRow
                ? [new Shared.E2EE.DeviceInfo
                    {
                        DeviceId = "device-1", DeviceName = "PC", Status = Shared.E2EE.DeviceStatus.Revoked,
                        AgreementPublicKey = "k", SigningPublicKey = "k",
                    }]
                : [],
        });

        var seen = new List<bool>();
        sut.CurrentDeviceRevoked += (_, e) => seen.Add(e.WasRevoked);

        await InvokeCheckForPendingDevicesAsync(sut);

        Assert.Equal([revokedRow], seen);
        sut.Dispose();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PendingDevicesCheck_ReplacesAnActiveDeviceOnlyWhenItsKeysAreGone(bool keysGone)
    {
        var sut = CreateSut(HttpStatusCode.OK, "{}");
        _deviceKeys.GetDeviceId().Returns("device-1");
        var self = new Shared.E2EE.DeviceInfo
        {
            DeviceId = "device-1", DeviceName = "PC", Status = Shared.E2EE.DeviceStatus.Active,
            AgreementPublicKey = "k", SigningPublicKey = "k",
        };
        _deviceMgmt.GetDevicesAsync().Returns(new Shared.E2EE.DeviceListResponse { Devices = [self] });
        _deviceMgmt.HoldsOtherKeysThan(self).Returns(keysGone);
        var revoked = 0;
        sut.CurrentDeviceRevoked += (_, _) => revoked++;

        await InvokeCheckForPendingDevicesAsync(sut);
        await InvokeCheckForPendingDevicesAsync(sut);

        await _deviceMgmt.Received(keysGone ? 1 : 0).ReplaceLostDeviceAsync();
        Assert.Equal(0, revoked);
        sut.Dispose();
    }

    [Fact]
    public async Task Pull_OnDeviceRevoked_RaisesTheNotice()
    {
        var sut = CreateSut(HttpStatusCode.Forbidden, """{"error":"device_revoked"}""");

        var revoked = false;
        var wasRevoked = false;
        sut.CurrentDeviceRevoked += (_, e) => (revoked, wasRevoked) = (true, e.WasRevoked);

        using (var client = JsonClient(HttpStatusCode.Forbidden, """{"error":"device_revoked"}"""))
            await InvokePullPageAsync(sut, client);

        Assert.True(revoked);
        Assert.True(wasRevoked);
        sut.Dispose();
    }

    [Fact]
    public async Task Push_OnDeviceRevoked_StopsBackgroundSync_AndRaisesTheNoticeOnce()
    {
        var sut = CreateSut(HttpStatusCode.Forbidden, """{"error":"device_revoked"}""");
        sut.StartBackgroundSync();
        Assert.True(sut.IsSyncActive);

        var notices = new List<bool>();
        sut.CurrentDeviceRevoked += (_, e) => notices.Add(e.WasRevoked);

        using (var client = JsonClient(HttpStatusCode.Forbidden, """{"error":"device_revoked"}"""))
            await InvokePushChangesAsync(sut, client);

        Assert.Equal([true], notices);
        Assert.False(sut.IsSyncActive);
        sut.Dispose();
    }

    private static async Task InvokeCheckForPendingDevicesAsync(SyncClientService sut)
    {
        var method = typeof(SyncClientService)
            .GetMethod("CheckForPendingDevicesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(sut, null)!;
    }

    private static async Task InvokePullPageAsync(SyncClientService sut, HttpClient client)
    {
        var method = typeof(SyncClientService)
            .GetMethod("PullPageAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(sut, [client, "http://test", new AppSettings(), DateTime.UtcNow, true])!;
    }

    private static async Task InvokePushChangesAsync(SyncClientService sut, HttpClient client)
    {
        var method = typeof(SyncClientService)
            .GetMethod("PushChangesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(sut, [client, "http://test", new AppSettings()])!;
    }

    private static HttpClient JsonClient(HttpStatusCode status, string body) =>
        new(new StubHandler(status, body));

    private SyncClientService CreateSut(HttpStatusCode status, string body, IE2EEService? e2ee = null)
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
        _pluginService.GetPendingPreferenceChanges().Returns(
            [new SyncPluginPreference { PluginId = Guid.NewGuid(), IsEnabled = true }]);

        _httpClientFactory.CreateClient(Arg.Any<string>())
            .Returns(_ => new HttpClient(new StubHandler(status, body), disposeHandler: true));
        _authService.IsLoggedIn.Returns(true);
        _authService.GetAccessTokenAsync().Returns("token");
        _settingsService.GetSettingsAsync().Returns(new AppSettings
        {
            SyncEnabled = true,
            ServerUrl = "http://test",
        });

        return new SyncClientService(
            _authService, _settingsService, _templateService,
            _providerService, _historyService, _memoryService,
            mapper, _httpClientFactory,
            NullLogger<SyncClientService>.Instance,
            deleteTracker,
            e2ee: e2ee,
            deviceMgmt: _deviceMgmt,
            deviceKeys: _deviceKeys,
            pluginService: _pluginService);
    }

    private sealed class IdentityDpapi() : DpapiHelper(NullLogger<DpapiHelper>.Instance)
    {
        public override string Encrypt(string plainText) => plainText;
        public override string Decrypt(string encryptedText) => encryptedText;
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
    }
}
