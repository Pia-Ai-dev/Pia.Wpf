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

// The repair is a one-shot: it resets the sync cursor so rows an old build blanked are pulled again.
// Burning the marker on a cycle that never ran would strand those rows blank for good, so every path
// that cannot actually resync has to leave it armed.
public class SyncBlankedRowRepairTests
{
    private readonly IAuthService _authService = Substitute.For<IAuthService>();
    private readonly ISettingsService _settingsService = Substitute.For<ISettingsService>();
    private readonly IProviderService _providerService = Substitute.For<IProviderService>();
    private readonly IHttpClientFactory _httpClientFactory = Substitute.For<IHttpClientFactory>();
    private readonly IE2EEService _e2ee = Substitute.For<IE2EEService>();
    private AppSettings _settings = new();

    private SyncClientService CreateSut()
    {
        var dpapi = Substitute.For<DpapiHelper>(NullLogger<DpapiHelper>.Instance);
        _settingsService.GetSettingsAsync().Returns(_ => _settings);
        _settingsService.SaveSettingsAsync(Arg.Do<AppSettings>(s => _settings = s)).Returns(Task.CompletedTask);

        return new SyncClientService(
            _authService, _settingsService, Substitute.For<ITemplateService>(),
            _providerService, Substitute.For<IHistoryService>(), Substitute.For<IMemoryService>(),
            new SyncMapper(dpapi, _e2ee), _httpClientFactory,
            NullLogger<SyncClientService>.Instance,
            new SyncDeleteTrackerService(Path.GetTempPath(), NullLogger<SyncDeleteTrackerService>.Instance),
            e2ee: _e2ee);
    }

    // The tell: PiaCloud-typed, but not the well-known Pia Cloud id.
    private static AiProvider BlankedRow() => new()
    {
        Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
        Name = "",
        Endpoint = "",
        ProviderType = AiProviderType.PiaCloud,
    };

    [Fact]
    public async Task Repair_whenTheSyncCycleCannotRun_leavesTheOneShotArmed()
    {
        _authService.IsLoggedIn.Returns(true);
        _authService.GetAccessTokenAsync().Returns((string?)null); // makes SyncNowAsync return null
        _providerService.GetProvidersAsync().Returns([BlankedRow()]);
        _settings = new AppSettings
        {
            SyncEnabled = true,
            ServerUrl = "http://test",
            LastSyncTimestamp = DateTime.UtcNow,
        };

        var repaired = await CreateSut().RepairBlankedSyncRowsAsync();

        Assert.False(repaired);
        Assert.Null(_settings.BlankedSyncRowRepairAt);
    }

    // Before onboarding a resync only meets the pull refusal, so it must not consume the one attempt.
    [Fact]
    public async Task Repair_whenE2EEIsNotReady_doesNotResyncAndStaysArmed()
    {
        _authService.IsLoggedIn.Returns(true);
        _e2ee.IsReady().Returns(false);
        _providerService.GetProvidersAsync().Returns([BlankedRow()]);
        var cursor = DateTime.UtcNow;
        _settings = new AppSettings
        {
            SyncEnabled = true,
            ServerUrl = "http://test",
            IsE2EEEnabled = true,
            LastSyncTimestamp = cursor,
        };

        var repaired = await CreateSut().RepairBlankedSyncRowsAsync();

        Assert.False(repaired);
        Assert.Null(_settings.BlankedSyncRowRepairAt);
        Assert.Equal(cursor, _settings.LastSyncTimestamp); // cursor untouched
    }

    [Fact]
    public async Task Repair_onAHealthyProfile_doesNothing()
    {
        _authService.IsLoggedIn.Returns(true);
        _e2ee.IsReady().Returns(true);
        _providerService.GetProvidersAsync().Returns([new AiProvider
        {
            Id = ProviderService.PiaCloudProviderId,
            Name = "Pia Cloud",
            Endpoint = "",
            ProviderType = AiProviderType.PiaCloud,
        }]);
        var cursor = DateTime.UtcNow;
        _settings = new AppSettings
        {
            SyncEnabled = true,
            ServerUrl = "http://test",
            LastSyncTimestamp = cursor,
        };

        var repaired = await CreateSut().RepairBlankedSyncRowsAsync();

        Assert.False(repaired);
        Assert.Equal(cursor, _settings.LastSyncTimestamp);
    }

    // Already repaired once: never resync again, however the profile looks now.
    [Fact]
    public async Task Repair_afterItHasRun_doesNotRunTwice()
    {
        _authService.IsLoggedIn.Returns(true);
        _e2ee.IsReady().Returns(true);
        _providerService.GetProvidersAsync().Returns([BlankedRow()]);
        var cursor = DateTime.UtcNow;
        _settings = new AppSettings
        {
            SyncEnabled = true,
            ServerUrl = "http://test",
            LastSyncTimestamp = cursor,
            BlankedSyncRowRepairAt = DateTime.UtcNow.AddDays(-1),
        };

        var repaired = await CreateSut().RepairBlankedSyncRowsAsync();

        Assert.False(repaired);
        Assert.Equal(cursor, _settings.LastSyncTimestamp);
    }
}
