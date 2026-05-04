namespace Pia.Tests.ViewModels;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.E2EE;
using Pia.Services.Interfaces;
using Pia.Shared.E2EE;
using Pia.ViewModels;
using Xunit;

public class FirstRunWizardViewModelTests
{
    private readonly ISettingsService _settings;
    private readonly IMemoryService _memory;
    private readonly IVoiceInputService _voice;
    private readonly ILocalizationService _loc;
    private readonly IAuthService _auth;
    private readonly IProviderService _providers;
    private readonly ISyncClientService _sync;
    private readonly IDeviceManagementService _deviceMgmt;
    private readonly IDeviceKeyService _deviceKeys;
    private readonly IOutputService _output;
    private readonly IPolicyService _policy;
    private readonly E2EEOnboardingViewModel _onboardingVm;
    private readonly E2EESetupStepViewModel _e2eeSetupVm;

    public FirstRunWizardViewModelTests()
    {
        _settings = Substitute.For<ISettingsService>();
        _memory = Substitute.For<IMemoryService>();
        _voice = Substitute.For<IVoiceInputService>();
        _loc = Substitute.For<ILocalizationService>();
        _auth = Substitute.For<IAuthService>();
        _providers = Substitute.For<IProviderService>();
        _sync = Substitute.For<ISyncClientService>();
        _deviceMgmt = Substitute.For<IDeviceManagementService>();
        _deviceKeys = Substitute.For<IDeviceKeyService>();
        _output = Substitute.For<IOutputService>();
        _policy = Substitute.For<IPolicyService>();
        _policy.IsLoginProviderAllowed(Arg.Any<string>()).Returns(true);

        _settings.GetSettingsAsync().Returns(new AppSettings());
        _deviceKeys.GetFingerprint().Returns("FP");

        _onboardingVm = new E2EEOnboardingViewModel(
            _deviceMgmt, _deviceKeys, Substitute.For<IE2EEService>(),
            _sync, _settings, NullLogger<E2EEOnboardingViewModel>.Instance);
        _e2eeSetupVm = new E2EESetupStepViewModel(
            _deviceMgmt, _deviceKeys, _sync, _output,
            NullLogger<E2EESetupStepViewModel>.Instance);
    }

    private FirstRunWizardViewModel CreateSut() => new(
        _settings, _memory, _voice, _loc, _auth, _providers, _sync,
        _deviceMgmt, _policy, _onboardingVm, _e2eeSetupVm,
        NullLogger<FirstRunWizardViewModel>.Instance);

    [Fact]
    public void NotLoggedIn_ShouldNotShowE2EEStep()
    {
        var sut = CreateSut();

        Assert.False(sut.IsLoggedIn);
        Assert.False(sut.IsE2EESetupVisible);
        Assert.Equal(6, sut.VisibleStepCount);
    }

    [Fact]
    public async Task LoggedInCloudUser_AccountE2EEOff_ShouldShowE2EEStep()
    {
        _auth.LoginWithPasswordAsync("a@example.com", "pw").Returns((true, (string?)null));
        _auth.UserDisplayName.Returns("Alice");
        _auth.UserEmail.Returns("a@example.com");
        _deviceMgmt.CheckE2EEStatusAsync().Returns(new E2EEStatusResponse { IsEnabled = false });

        var sut = CreateSut();
        sut.LoginEmailInput = "a@example.com";
        sut.LoginPassword = "pw";
        await sut.LoginWithPasswordCommand.ExecuteAsync(null);

        Assert.True(sut.IsLoggedIn);
        Assert.True(sut.IsE2EESetupVisible);
        Assert.Equal(6, sut.VisibleStepCount);
        // First sync NOT started yet — deferred to E2EE step
        await _sync.DidNotReceive().PerformFirstSyncMigrationAsync();
    }

    [Fact]
    public async Task LoggedInCloudUser_AccountE2EEAlreadyOn_AndUmkAvailable_ShouldNotShowE2EEStep()
    {
        _auth.LoginWithPasswordAsync("a@example.com", "pw").Returns((true, (string?)null));
        _deviceMgmt.CheckE2EEStatusAsync().Returns(new E2EEStatusResponse { IsEnabled = true });
        _deviceMgmt.IsInitialized().Returns(true);

        var sut = CreateSut();
        sut.LoginEmailInput = "a@example.com";
        sut.LoginPassword = "pw";
        await sut.LoginWithPasswordCommand.ExecuteAsync(null);

        Assert.True(sut.IsLoggedIn);
        Assert.False(sut.IsE2EESetupVisible);
        Assert.Equal(5, sut.VisibleStepCount);
        await _sync.Received(1).PerformFirstSyncMigrationAsync();
    }

    [Fact]
    public async Task Next_FromAccountStep_ShouldGoToE2EEStep_WhenCloudUserNoE2EE()
    {
        _auth.LoginWithPasswordAsync("a@example.com", "pw").Returns((true, (string?)null));
        _deviceMgmt.CheckE2EEStatusAsync().Returns(new E2EEStatusResponse { IsEnabled = false });

        var sut = CreateSut();
        sut.LoginEmailInput = "a@example.com";
        sut.LoginPassword = "pw";
        await sut.LoginWithPasswordCommand.ExecuteAsync(null);
        sut.CurrentStep = 1;

        await sut.NextOrFinishCommand.ExecuteAsync(null);

        Assert.Equal(2, sut.CurrentStep);
    }

    [Fact]
    public async Task Next_FromAccountStep_ShouldSkipToModesStep_WhenE2EEAlreadyOn()
    {
        _auth.LoginWithPasswordAsync("a@example.com", "pw").Returns((true, (string?)null));
        _deviceMgmt.CheckE2EEStatusAsync().Returns(new E2EEStatusResponse { IsEnabled = true });
        _deviceMgmt.IsInitialized().Returns(true);

        var sut = CreateSut();
        sut.LoginEmailInput = "a@example.com";
        sut.LoginPassword = "pw";
        await sut.LoginWithPasswordCommand.ExecuteAsync(null);
        sut.CurrentStep = 1;

        await sut.NextOrFinishCommand.ExecuteAsync(null);

        Assert.Equal(4, sut.CurrentStep);
    }

    [Fact]
    public async Task Back_FromE2EEStep_PreBootstrap_ShouldReturnToAccountStep()
    {
        _auth.LoginWithPasswordAsync("a@example.com", "pw").Returns((true, (string?)null));
        _deviceMgmt.CheckE2EEStatusAsync().Returns(new E2EEStatusResponse { IsEnabled = false });

        var sut = CreateSut();
        sut.LoginEmailInput = "a@example.com";
        sut.LoginPassword = "pw";
        await sut.LoginWithPasswordCommand.ExecuteAsync(null);
        sut.CurrentStep = 2;

        Assert.True(sut.BackCommand.CanExecute(null));
        sut.BackCommand.Execute(null);

        Assert.Equal(1, sut.CurrentStep);
    }

    [Fact]
    public async Task Back_FromE2EEStep_PostBootstrap_ShouldBeDisabled()
    {
        _auth.LoginWithPasswordAsync("a@example.com", "pw").Returns((true, (string?)null));
        _deviceMgmt.CheckE2EEStatusAsync().Returns(new E2EEStatusResponse { IsEnabled = false });

        var sut = CreateSut();
        sut.LoginEmailInput = "a@example.com";
        sut.LoginPassword = "pw";
        await sut.LoginWithPasswordCommand.ExecuteAsync(null);
        sut.CurrentStep = 2;
        _e2eeSetupVm.State = E2EESetupState.SavingRecoveryCode;

        Assert.False(sut.BackCommand.CanExecute(null));
    }
}
