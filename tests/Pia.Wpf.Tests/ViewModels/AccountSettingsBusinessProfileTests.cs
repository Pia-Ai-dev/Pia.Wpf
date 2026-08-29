namespace Pia.Tests.ViewModels;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.E2EE;
using Pia.Services.Interfaces;
using Pia.Tests.TestInfrastructure;
using Pia.ViewModels;
using Xunit;

/// <summary>
/// The trader-declaration card sits on a settings page that outlives the account it was filled in for.
/// </summary>
public class AccountSettingsBusinessProfileTests
{
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IAuthService _auth = Substitute.For<IAuthService>();
    private readonly ISyncClientService _sync = Substitute.For<ISyncClientService>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly IDeviceManagementService _deviceMgmt = Substitute.For<IDeviceManagementService>();
    private readonly IDeviceKeyService _deviceKeys = Substitute.For<IDeviceKeyService>();
    private readonly IMemoryService _memory = Substitute.For<IMemoryService>();
    private readonly IPolicyService _policy = Substitute.For<IPolicyService>();

    public AccountSettingsBusinessProfileTests()
    {
        _settings.GetSettingsAsync().Returns(new AppSettings());
        _loc[Arg.Any<string>()].Returns("display");
    }

    private AccountSettingsViewModel CreateSut()
    {
        // AccountSettingsViewModel demands a captured context; inline keeps the assertions synchronous.
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());

        return new AccountSettingsViewModel(
            NullLogger<SettingsViewModel>.Instance, _settings, Substitute.For<IDialogService>(),
            Substitute.For<global::Wpf.Ui.ISnackbarService>(), _auth, _sync, _loc, _deviceMgmt,
            _deviceKeys, _memory, _policy,
            new E2EEOnboardingViewModel(
                _deviceMgmt, _deviceKeys, Substitute.For<IE2EEService>(), _sync, _settings,
                NullLogger<E2EEOnboardingViewModel>.Instance));
    }

    [Fact]
    public void SigningOut_ClearsThePreviousAccountsDeclarationAndCompanyName()
    {
        var sut = CreateSut();
        sut.RequiresBusinessProfile = true;
        sut.CompanyNameInput = "Contoso GmbH";
        sut.BusinessProfileError = "outstanding";

        _auth.LoginStateChanged += Raise.Event<EventHandler<bool>>(_auth, false);

        Assert.False(sut.RequiresBusinessProfile);
        Assert.Equal("", sut.CompanyNameInput);
        Assert.Null(sut.BusinessProfileError);
    }

    /// <summary>Non-vacuity for the reset: the same three fields survive a raise that is not a sign-out.</summary>
    [Fact]
    public void SigningIn_LeavesTheDeclarationFormAlone()
    {
        var sut = CreateSut();
        sut.RequiresBusinessProfile = true;
        sut.CompanyNameInput = "Contoso GmbH";
        sut.BusinessProfileError = "outstanding";

        _auth.LoginStateChanged += Raise.Event<EventHandler<bool>>(_auth, true);

        Assert.True(sut.RequiresBusinessProfile);
        Assert.Equal("Contoso GmbH", sut.CompanyNameInput);
        Assert.Equal("outstanding", sut.BusinessProfileError);
    }

    [Fact]
    public async Task SigningIn_TakesTheDeclarationFromTheLoginResponse_NotASecondProbe()
    {
        _auth.LoginAsync("google").Returns((true, (string?)null));
        _auth.IsLoggedIn.Returns(true);
        _auth.RequiresBusinessProfile.Returns(true);

        var sut = CreateSut();
        await sut.LoginWithGoogleCommand.ExecuteAsync(null);

        Assert.True(sut.RequiresBusinessProfile);
        await _auth.DidNotReceive().RequiresBusinessProfileAsync();
        await _deviceMgmt.DidNotReceive().CheckE2EEStatusAsync();
    }

    [Fact]
    public async Task LoadProbe_ThatCannotAnswer_LeavesTheDeclarationOutstanding()
    {
        _auth.IsLoggedIn.Returns(true);
        _auth.RequiresBusinessProfileAsync().Returns((bool?)null);

        var sut = CreateSut();
        sut.RequiresBusinessProfile = true;

        await sut.InitializeAsync();
        await Eventually.TrueAsync(
            () => _auth.ReceivedCalls().Any(
                call => call.GetMethodInfo().Name == nameof(IAuthService.RequiresBusinessProfileAsync)),
            "the load-time trader-declaration probe to run",
            TestContext.Current.CancellationToken);

        Assert.True(sut.RequiresBusinessProfile);
    }

    /// <summary>Non-vacuity for the null case: an answer of "yes" does reach the same field.</summary>
    [Fact]
    public async Task LoadProbe_FindsTheDeclarationAStoredTokenCannotCarry()
    {
        _auth.IsLoggedIn.Returns(true);
        _auth.RequiresBusinessProfileAsync().Returns((bool?)true);

        var sut = CreateSut();

        await sut.InitializeAsync();
        await Eventually.TrueAsync(
            () => sut.RequiresBusinessProfile,
            "the load-time trader-declaration probe to mark the declaration outstanding",
            TestContext.Current.CancellationToken);
    }
}
