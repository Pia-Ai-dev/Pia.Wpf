using System;
using System.Linq;
using System.Text.Json;
using Pia.Models;
using Pia.Services;
using Xunit;

namespace Pia.Tests.Models;

public class AppSettingsLocalResetTests
{
    [Fact]
    public void SyncedPreferenceNames_MatchTheSyncPayloadExactly()
    {
        var payloadNames = SyncMapper.BuildSettingsPlainPayload(new AppSettings())
            .GetType().GetProperties().Select(p => p.Name).Order();

        Assert.Equal(payloadNames, AppSettings.SyncedPreferenceNames.Order());
    }

    [Fact]
    public void ResetSyncedData_RestoresTheDefaultPayload_AndKeepsDeviceState()
    {
        var s = new AppSettings
        {
            Theme = AppTheme.Dark,
            DefaultTemplateId = Guid.NewGuid(),
            AutoTypeDelayMs = 999,
            StartMinimized = true,
            AssistantDefaultWorkingDirectory = "X",
            HasCompletedFirstRunWizard = true,
            ServerUrl = "https://example.test",
            SyncDeviceId = "dev-1",
            LastCatalogVersion = 7,
            ManagedPersonaStoreInitialized = true,
            AssistantChatsEncryptedResendAt = DateTime.UtcNow,
            BlankedSyncRowRepairAt = DateTime.UtcNow,
        };
        s.ModeProviderDefaults[WindowMode.Assistant] = Guid.NewGuid();
        s.ModePersonaDefaults[WindowMode.Assistant] = Guid.NewGuid();

        s.ResetSyncedData();

        Assert.Equal(
            JsonSerializer.Serialize(SyncMapper.BuildSettingsPlainPayload(new AppSettings())),
            JsonSerializer.Serialize(SyncMapper.BuildSettingsPlainPayload(s)));
        Assert.True(s.HasCompletedFirstRunWizard);
        Assert.Equal("https://example.test", s.ServerUrl);
        Assert.Equal("dev-1", s.SyncDeviceId);
        Assert.Null(s.LastCatalogVersion);
        Assert.False(s.ManagedPersonaStoreInitialized);
        Assert.Null(s.AssistantChatsEncryptedResendAt);
        Assert.Null(s.BlankedSyncRowRepairAt);
        Assert.True(s.SuppressSettingsPushAfterReset);
    }

    [Fact]
    public void ForgetE2EEKey_KeepsTheAccountsRequirement()
    {
        var s = new AppSettings { SyncUserId = "user-1", IsE2EEEnabled = true, E2EEEncryptedUmk = "umk" };

        s.ForgetE2EEKey();

        Assert.False(s.IsE2EEEnabled);
        Assert.Contains("user-1", s.E2EERequiredAccountIds);
    }

    [Fact]
    public void ForgetE2EEKey_WithoutAnActiveKey_MarksNothing()
    {
        var s = new AppSettings { SyncUserId = "user-1" };

        s.ForgetE2EEKey();

        Assert.Empty(s.E2EERequiredAccountIds);
    }

    [Fact]
    public void ClearAccountState_DropsTokensIdentityCursorsAndKey()
    {
        var s = new AppSettings
        {
            SyncEnabled = true,
            EncryptedRefreshToken = "r",
            EncryptedAccessToken = "a",
            SyncUserId = "u",
            LastSyncTimestamp = DateTime.UtcNow,
            LastPullETag = "e",
            IsE2EEEnabled = true,
            E2EEEncryptedUmk = "k",
        };

        s.ClearAccountState();

        Assert.False(s.SyncEnabled);
        Assert.Null(s.EncryptedRefreshToken);
        Assert.Null(s.EncryptedAccessToken);
        Assert.Null(s.SyncUserId);
        Assert.Null(s.LastSyncTimestamp);
        Assert.Null(s.LastPullETag);
        Assert.False(s.IsE2EEEnabled);
        Assert.Null(s.E2EEEncryptedUmk);
    }
}
