namespace Pia.Tests.ViewModels;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.E2EE;
using Pia.Services.Interfaces;
using Pia.Shared.E2EE;
using Pia.Tests.TestInfrastructure;
using Pia.ViewModels;
using Xunit;

/// <summary>Sign-out forgets the key, so both sign-in paths must take back this device's server copy before
/// they send the user through onboarding for a device the account still trusts.</summary>
public class E2EESignInKeyRestoreTests
{
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IAuthService _auth = Substitute.For<IAuthService>();
    private readonly ISyncClientService _sync = Substitute.For<ISyncClientService>();
    private readonly IDeviceManagementService _deviceMgmt = Substitute.For<IDeviceManagementService>();
    private readonly IDeviceKeyService _deviceKeys = Substitute.For<IDeviceKeyService>();

    public E2EESignInKeyRestoreTests()
    {
        _settings.GetSettingsAsync().Returns(new AppSettings());
        _auth.LoginAsync("google").Returns((true, (string?)null));
        _auth.IsLoggedIn.Returns(true);
        _auth.RequiresBusinessProfile.Returns(false);
        _deviceKeys.GetFingerprint().Returns("FP");
        _deviceMgmt.CheckE2EEStatusAsync().Returns(new E2EEStatusResponse { IsEnabled = true });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AccountSettings_OnboardsOnlyWhenTheKeyCannotBeTakenBack(bool restored)
    {
        _deviceMgmt.TryRestoreKeyAsync().Returns(restored);
        var sut = CreateAccountSettings();

        await sut.LoginWithGoogleCommand.ExecuteAsync(null);

        Assert.Equal(!restored, sut.IsE2EEOnboardingRequired);
        await _sync.Received(restored ? 1 : 0).PerformFirstSyncMigrationAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FirstRunWizard_OnboardsOnlyWhenTheKeyCannotBeTakenBack(bool restored)
    {
        _deviceMgmt.TryRestoreKeyAsync().Returns(restored);
        var sut = CreateWizard();

        await sut.LoginWithGoogleCommand.ExecuteAsync(null);

        Assert.Equal(!restored, sut.IsE2EEOnboardingRequired);
        await _sync.Received(restored ? 1 : 0).PerformFirstSyncMigrationAsync();
    }

    private E2EEOnboardingViewModel Onboarding() => new(
        _deviceMgmt, _deviceKeys, Substitute.For<IE2EEService>(), _sync, _settings,
        NullLogger<E2EEOnboardingViewModel>.Instance);

    private AccountSettingsViewModel CreateAccountSettings()
    {
        // AccountSettingsViewModel demands a captured context; inline keeps the assertions synchronous.
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());
        var loc = Substitute.For<ILocalizationService>();
        loc[Arg.Any<string>()].Returns("display");

        return new AccountSettingsViewModel(
            NullLogger<SettingsViewModel>.Instance, _settings, Substitute.For<IDialogService>(),
            Substitute.For<global::Wpf.Ui.ISnackbarService>(), _auth, _sync, loc, _deviceMgmt,
            _deviceKeys, Substitute.For<IMemoryService>(), Substitute.For<IPolicyService>(), Onboarding(),
            Substitute.For<IAccountDataService>(), Substitute.For<IFileDialogService>());
    }

    private FirstRunWizardViewModel CreateWizard()
    {
        var policy = Substitute.For<IPolicyService>();
        policy.IsLoginProviderAllowed(Arg.Any<string>()).Returns(true);
        var e2eeSetup = new E2EESetupStepViewModel(
            _deviceMgmt, _deviceKeys, _sync, Substitute.For<IOutputService>(),
            NullLogger<E2EESetupStepViewModel>.Instance);

        return new FirstRunWizardViewModel(
            _settings, Substitute.For<IMemoryService>(), Substitute.For<IVoiceInputService>(),
            Substitute.For<ILocalizationService>(), _auth, Substitute.For<IProviderService>(), _sync,
            _deviceMgmt, policy, Onboarding(), e2eeSetup,
            NullLogger<FirstRunWizardViewModel>.Instance);
    }
}
