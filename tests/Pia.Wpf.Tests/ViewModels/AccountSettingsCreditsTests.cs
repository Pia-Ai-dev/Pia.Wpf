namespace Pia.Tests.ViewModels;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Credits;
using Pia.Services.E2EE;
using Pia.Services.Interfaces;
using Pia.Tests.TestInfrastructure;
using Pia.ViewModels;
using Xunit;

public class AccountSettingsCreditsTests
{
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IAuthService _auth = Substitute.For<IAuthService>();
    private readonly ISyncClientService _sync = Substitute.For<ISyncClientService>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly IDeviceManagementService _deviceMgmt = Substitute.For<IDeviceManagementService>();
    private readonly IDeviceKeyService _deviceKeys = Substitute.For<IDeviceKeyService>();
    private readonly IMemoryService _memory = Substitute.For<IMemoryService>();
    private readonly IPolicyService _policy = Substitute.For<IPolicyService>();
    private readonly ICreditStatusService _credits = Substitute.For<ICreditStatusService>();

    public AccountSettingsCreditsTests()
    {
        _settings.GetSettingsAsync().Returns(new AppSettings());
        _loc[Arg.Any<string>()].Returns("{0} {1}");
        _loc["Settings_Credits_Resets"].Returns("{0}");
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
                NullLogger<E2EEOnboardingViewModel>.Instance),
            Substitute.For<IAccountDataService>(), Substitute.For<IFileDialogService>(), _credits);
    }

    private static CreditStatusResponse Limited(bool? suspended = null) => new(
        true, suspended, null, null, new CreditWindowDto(100, 40, DateTime.UtcNow.AddDays(3)), null, null, null);

    [Fact]
    public async Task ALimitedAnswer_ShowsTheCard()
    {
        _credits.GetAsync(Arg.Any<CancellationToken>()).Returns(Limited());
        var sut = CreateSut();

        await sut.RefreshCreditsAsync();

        Assert.True(sut.HasCredits);
        Assert.Equal("weekly", Assert.Single(sut.CreditMeters).Key);
        Assert.False(sut.IsCreditTierSuspended);
    }

    [Fact]
    public async Task ASuspendedAnswer_RaisesTheWarning()
    {
        _credits.GetAsync(Arg.Any<CancellationToken>()).Returns(Limited(suspended: true));
        var sut = CreateSut();

        await sut.RefreshCreditsAsync();

        Assert.True(sut.IsCreditTierSuspended);
    }

    [Fact]
    public async Task ALaterUnlimitedAnswer_HidesTheCard()
    {
        _credits.GetAsync(Arg.Any<CancellationToken>()).Returns(Limited(), new CreditStatusResponse(
            false, null, null, null, null, null, null, null));
        var sut = CreateSut();

        await sut.RefreshCreditsAsync();
        await sut.RefreshCreditsAsync();

        Assert.False(sut.HasCredits);
        Assert.Empty(sut.CreditMeters);
    }

    [Fact]
    public async Task NoAnswer_HidesTheCard()
    {
        _credits.GetAsync(Arg.Any<CancellationToken>()).Returns((CreditStatusResponse?)null);
        var sut = CreateSut();

        await sut.RefreshCreditsAsync();

        Assert.False(sut.HasCredits);
    }

    [Fact]
    public async Task SigningOut_ClearsTheCard()
    {
        _credits.GetAsync(Arg.Any<CancellationToken>()).Returns(Limited(suspended: true));
        var sut = CreateSut();
        await sut.RefreshCreditsAsync();

        _auth.LoginStateChanged += Raise.Event<EventHandler<bool>>(_auth, false);

        Assert.False(sut.HasCredits);
        Assert.False(sut.IsCreditTierSuspended);
        Assert.Empty(sut.CreditMeters);
    }

    [Fact]
    public async Task SigningIn_FetchesTheCard()
    {
        _credits.GetAsync(Arg.Any<CancellationToken>()).Returns(Limited());
        var sut = CreateSut();

        _auth.LoginStateChanged += Raise.Event<EventHandler<bool>>(_auth, true);

        await _credits.Received(1).GetAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AFetchThatLandsAfterSignOut_DoesNotRefillTheCard()
    {
        var pending = new TaskCompletionSource<CreditStatusResponse?>();
        _credits.GetAsync(Arg.Any<CancellationToken>()).Returns(pending.Task);
        var sut = CreateSut();

        var refresh = sut.RefreshCreditsAsync();
        _auth.LoginStateChanged += Raise.Event<EventHandler<bool>>(_auth, false);
        pending.SetResult(Limited());
        await refresh;

        Assert.False(sut.HasCredits);
        Assert.False(sut.IsCreditTierSuspended);
        Assert.Empty(sut.CreditMeters);
    }
}
