namespace Pia.Tests.ViewModels;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.E2EE;
using Pia.Services.Interfaces;
using Pia.Shared.E2EE;
using Pia.ViewModels;
using Xunit;

/// <summary>
/// An account that still owes its trader declaration gets 403s from every data endpoint, so the wizard
/// has to hold the user on the account step instead of walking them into a setup that cannot work.
/// </summary>
public class FirstRunWizardBusinessProfileTests
{
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly IAuthService _auth = Substitute.For<IAuthService>();
    private readonly ISyncClientService _sync = Substitute.For<ISyncClientService>();
    private readonly IDeviceManagementService _deviceMgmt = Substitute.For<IDeviceManagementService>();
    private readonly IDeviceKeyService _deviceKeys = Substitute.For<IDeviceKeyService>();
    private readonly IPolicyService _policy = Substitute.For<IPolicyService>();

    public FirstRunWizardBusinessProfileTests()
    {
        _settings.GetSettingsAsync().Returns(new AppSettings());
        _deviceKeys.GetFingerprint().Returns("FP");
        _policy.IsLoginProviderAllowed(Arg.Any<string>()).Returns(true);
    }

    private FirstRunWizardViewModel CreateSut() => new(
        _settings, Substitute.For<IMemoryService>(), Substitute.For<IVoiceInputService>(), _loc, _auth,
        Substitute.For<IProviderService>(), _sync, _deviceMgmt, _policy,
        new E2EEOnboardingViewModel(
            _deviceMgmt, _deviceKeys, Substitute.For<IE2EEService>(), _sync, _settings,
            NullLogger<E2EEOnboardingViewModel>.Instance),
        new E2EESetupStepViewModel(
            _deviceMgmt, _deviceKeys, _sync, Substitute.For<IOutputService>(),
            NullLogger<E2EESetupStepViewModel>.Instance),
        NullLogger<FirstRunWizardViewModel>.Instance);

    private async Task<FirstRunWizardViewModel> SignedInSut(bool declarationOutstanding)
    {
        _auth.LoginWithPasswordAsync("a@example.com", "pw").Returns((true, (string?)null));
        _auth.RequiresBusinessProfile.Returns(declarationOutstanding);
        _deviceMgmt.CheckE2EEStatusAsync().Returns(new E2EEStatusResponse { IsEnabled = false });

        var sut = CreateSut();
        sut.LoginEmailInput = "a@example.com";
        sut.LoginPassword = "pw";
        await sut.LoginWithPasswordCommand.ExecuteAsync(null);
        return sut;
    }

    [Fact]
    public async Task OutstandingDeclaration_BlocksNextOnTheAccountStep()
    {
        var sut = await SignedInSut(declarationOutstanding: true);
        sut.CurrentStep = 1;

        Assert.True(sut.RequiresBusinessProfile);
        Assert.False(sut.NextOrFinishCommand.CanExecute(null));
    }

    [Fact]
    public async Task OutstandingDeclaration_StillLetsTheUserSkipTheWizard()
    {
        var sut = await SignedInSut(declarationOutstanding: true);
        sut.CurrentStep = 1;

        Assert.True(sut.SkipCommand.CanExecute(null));
    }

    /// <summary>Non-vacuity for the block: the same signed-in state leaves Next alive once nothing is owed.</summary>
    [Fact]
    public async Task SatisfiedDeclaration_LeavesNextOnTheAccountStepEnabled()
    {
        var sut = await SignedInSut(declarationOutstanding: false);
        sut.CurrentStep = 1;

        Assert.False(sut.RequiresBusinessProfile);
        Assert.True(sut.NextOrFinishCommand.CanExecute(null));
    }

    [Fact]
    public async Task SatisfyingTheDeclaration_ReEnablesNextWithoutAnotherUserAction()
    {
        var sut = await SignedInSut(declarationOutstanding: true);
        sut.CurrentStep = 1;

        var raises = 0;
        sut.NextOrFinishCommand.CanExecuteChanged += (_, _) => raises++;

        sut.RequiresBusinessProfile = false;

        Assert.True(raises > 0, "the Next button was never told its CanExecute had changed");
        Assert.True(sut.NextOrFinishCommand.CanExecute(null));
    }

    [Fact]
    public async Task OutstandingDeclaration_DoesNotOfferTheE2EESetupStep()
    {
        var sut = await SignedInSut(declarationOutstanding: true);

        Assert.True(sut.IsLoggedIn);
        Assert.False(sut.IsE2EESetupVisible);
        Assert.Equal(5, sut.VisibleStepCount);
        // The account's E2EE state was never read, so the step cannot be riding an unprobed default.
        await _deviceMgmt.DidNotReceive().CheckE2EEStatusAsync();
    }

    /// <summary>Non-vacuity for the hidden step: the same login does offer it once nothing is owed.</summary>
    [Fact]
    public async Task SatisfiedDeclaration_OffersTheE2EESetupStep()
    {
        var sut = await SignedInSut(declarationOutstanding: false);

        Assert.True(sut.IsE2EESetupVisible);
        Assert.Equal(6, sut.VisibleStepCount);
        await _deviceMgmt.Received(1).CheckE2EEStatusAsync();
    }

    [Fact]
    public async Task LoadProbe_FindsTheDeclarationAStoredTokenCannotCarry()
    {
        _auth.RequiresBusinessProfileAsync().Returns((bool?)true);
        var sut = CreateSut();

        await sut.InitializeAsync();

        Assert.True(sut.RequiresBusinessProfile);
    }

    [Fact]
    public async Task LoadProbe_ThatCannotAnswer_LeavesTheDeclarationOutstanding()
    {
        _auth.RequiresBusinessProfileAsync().Returns((bool?)null);
        var sut = CreateSut();
        sut.RequiresBusinessProfile = true;

        await sut.InitializeAsync();

        await _auth.Received(1).RequiresBusinessProfileAsync();
        Assert.True(sut.RequiresBusinessProfile);
    }

    /// <summary>Non-vacuity for the null case: an answer of "no" does reach the same field.</summary>
    [Fact]
    public async Task LoadProbe_ThatSaysNothingIsOwed_ClearsTheDeclaration()
    {
        _auth.RequiresBusinessProfileAsync().Returns((bool?)false);
        var sut = CreateSut();
        sut.RequiresBusinessProfile = true;

        await sut.InitializeAsync();

        Assert.False(sut.RequiresBusinessProfile);
    }

    [Fact]
    public async Task LoadProbe_OnARestoredSession_ShowsTheAccountAsSignedIn()
    {
        _auth.RequiresBusinessProfileAsync().Returns((bool?)true);
        _auth.IsLoggedIn.Returns(true);
        _auth.UserEmail.Returns("a@example.com");
        var sut = CreateSut();

        await sut.InitializeAsync();
        sut.CurrentStep = 1;

        // Next is blocked here, so the step has to show the account rather than the sign-in buttons.
        Assert.False(sut.NextOrFinishCommand.CanExecute(null));
        Assert.True(sut.IsLoggedIn);
        Assert.Equal("a@example.com", sut.LoginEmail);
    }

    [Fact]
    public async Task LoadProbe_WithNoStoredSession_LeavesTheStepSignedOut()
    {
        _auth.RequiresBusinessProfileAsync().Returns((bool?)null);
        _auth.IsLoggedIn.Returns(false);
        var sut = CreateSut();

        await sut.InitializeAsync();

        Assert.False(sut.IsLoggedIn);
    }
}
