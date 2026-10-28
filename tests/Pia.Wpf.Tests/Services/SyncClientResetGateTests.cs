using System.IO;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.E2EE;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

public class SyncClientResetGateTests
{
    private readonly IAuthService _auth = Substitute.For<IAuthService>();
    private readonly ISettingsService _settingsService = Substitute.For<ISettingsService>();
    private readonly IHttpClientFactory _httpClientFactory = Substitute.For<IHttpClientFactory>();
    private readonly ILocalDataResetGate _gate = Substitute.For<ILocalDataResetGate>();
    private readonly DateTime _cursor = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly AppSettings _settings;

    public SyncClientResetGateTests()
    {
        _settings = new AppSettings { SyncEnabled = true, ServerUrl = "http://test", LastSyncTimestamp = _cursor };
        _settingsService.GetSettingsAsync().Returns(_settings);
        _auth.IsLoggedIn.Returns(true);
        _auth.GetAccessTokenAsync().Returns("token");
        _gate.IsResetPending.Returns(true);
    }

    [Fact]
    public async Task EveryEntryPoint_WhileAClearIsPending_StaysOffTheServer()
    {
        using var sut = CreateSut();

        Assert.Null(await sut.SyncNowAsync(TestContext.Current.CancellationToken));
        Assert.Null(await sut.ForceFullResyncAsync());
        await sut.PerformFirstSyncMigrationAsync();
        sut.StartBackgroundSync();

        Assert.False(sut.IsSyncActive);
        Assert.Equal(_cursor, _settings.LastSyncTimestamp);
        _httpClientFactory.DidNotReceiveWithAnyArgs().CreateClient(default!);
        await _settingsService.DidNotReceive().SaveSettingsAsync(Arg.Any<AppSettings>());
    }

    [Fact]
    public void BackgroundSync_WithNoClearPending_Starts()
    {
        _gate.IsResetPending.Returns(false);
        using var sut = CreateSut();

        sut.StartBackgroundSync();

        Assert.True(sut.IsSyncActive);
        sut.StopBackgroundSync();
    }

    private SyncClientService CreateSut()
    {
        var e2ee = Substitute.For<IE2EEService>();
        return new SyncClientService(
            _auth, _settingsService, Substitute.For<ITemplateService>(),
            Substitute.For<IProviderService>(), Substitute.For<IHistoryService>(), Substitute.For<IMemoryService>(),
            new SyncMapper(Substitute.For<DpapiHelper>(NullLogger<DpapiHelper>.Instance), e2ee), _httpClientFactory,
            NullLogger<SyncClientService>.Instance,
            new SyncDeleteTrackerService(Path.GetTempPath(), NullLogger<SyncDeleteTrackerService>.Instance),
            e2ee: e2ee,
            resetGate: _gate);
    }
}
