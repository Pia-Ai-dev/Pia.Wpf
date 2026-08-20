namespace Pia.Tests.ViewModels;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.E2EE;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Xunit;

/// <summary>
/// The wizard is the only provider-creation path outside the Providers tab, and on a freshly deployed
/// machine it is the first screen — so <c>allowProviderManagement: false</c> has to reach it.
/// </summary>
public class FirstRunWizardProviderLockTests
{
    private static FirstRunWizardViewModel CreateSut(AppSettings stored, IProviderService providers)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(_ => Task.FromResult(stored));

        var sync = Substitute.For<ISyncClientService>();
        var deviceMgmt = Substitute.For<IDeviceManagementService>();
        var deviceKeys = Substitute.For<IDeviceKeyService>();
        deviceKeys.GetFingerprint().Returns("FP");

        var policy = Substitute.For<IPolicyService>();
        policy.IsLoginProviderAllowed(Arg.Any<string>()).Returns(true);

        var onboarding = new E2EEOnboardingViewModel(
            deviceMgmt, deviceKeys, Substitute.For<IE2EEService>(), sync, settings,
            NullLogger<E2EEOnboardingViewModel>.Instance);
        var e2eeSetup = new E2EESetupStepViewModel(
            deviceMgmt, deviceKeys, sync, Substitute.For<IOutputService>(),
            NullLogger<E2EESetupStepViewModel>.Instance);

        return new FirstRunWizardViewModel(
            settings, Substitute.For<IMemoryService>(), Substitute.For<IVoiceInputService>(),
            Substitute.For<ILocalizationService>(), Substitute.For<IAuthService>(), providers, sync,
            deviceMgmt, policy, onboarding, e2eeSetup,
            NullLogger<FirstRunWizardViewModel>.Instance);
    }

    private static async Task<FirstRunWizardViewModel> LoadedSut(AppSettings stored, IProviderService providers)
    {
        var sut = CreateSut(stored, providers);
        // The policy read is kicked off from the ctor; wait for it rather than racing it.
        for (var i = 0; i < 100 && sut.IsProviderStepVisible != stored.AllowProviderManagement; i++)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        return sut;
    }

    [Fact]
    public async Task Unlocked_ShowsTheProviderStep()
    {
        var sut = await LoadedSut(new AppSettings(), Substitute.For<IProviderService>());

        Assert.False(sut.IsLoggedIn);
        Assert.True(sut.IsProviderStepVisible);
        Assert.Equal(6, sut.VisibleStepCount);
    }

    [Fact]
    public async Task LockedByPolicy_HidesTheProviderStep()
    {
        var sut = await LoadedSut(
            new AppSettings { AllowProviderManagement = false }, Substitute.For<IProviderService>());

        Assert.False(sut.IsLoggedIn);
        Assert.False(sut.IsProviderStepVisible);
        Assert.Equal(5, sut.VisibleStepCount);
    }

    [Fact]
    public async Task LockedByPolicy_CompletingTheWizardCreatesNoProvider()
    {
        var providers = Substitute.For<IProviderService>();
        var sut = await LoadedSut(new AppSettings { AllowProviderManagement = false }, providers);

        // The state the persist block keys off — a connection test that passed on the provider form.
        sut.ConnectionTestPassed = true;
        sut.CurrentStep = FirstRunWizardViewModel.TotalSteps - 1;
        await sut.NextOrFinishCommand.ExecuteAsync(null);

        await providers.DidNotReceive().AddProviderAsync(Arg.Any<AiProvider>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task Unlocked_CompletingTheWizardStillCreatesTheProvider()
    {
        // Non-vacuity for the fact above: without this, a wizard that never persisted would also pass.
        var providers = Substitute.For<IProviderService>();
        var sut = await LoadedSut(new AppSettings(), providers);

        sut.ConnectionTestPassed = true;
        sut.CurrentStep = FirstRunWizardViewModel.TotalSteps - 1;
        await sut.NextOrFinishCommand.ExecuteAsync(null);

        await providers.Received(1).AddProviderAsync(Arg.Any<AiProvider>(), Arg.Any<string?>());
    }
}
