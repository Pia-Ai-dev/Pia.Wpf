using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Pia.Models;
using Pia.Navigation;
using Pia.Services.Interfaces;
using Pia.Services.Operators;
using Pia.Shared.Operators;
using Pia.Tests.TestInfrastructure;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>The background-assignment entry is an AND: assistant mode and a server that offers the surface.</summary>
public class MainWindowViewModelTests
{
    private readonly INavigationService _navigation = Substitute.For<INavigationService>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IAssignmentApiClient _assignments = Substitute.For<IAssignmentApiClient>();
    private readonly IAuthService _auth = Substitute.For<IAuthService>();
    private readonly IPolicyService _policy = Substitute.For<IPolicyService>();

    private MainWindowViewModel CreateSut(WindowMode mode)
    {
        if (SynchronizationContext.Current is null)
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());

        _settings.GetSettingsAsync().Returns(new AppSettings());

        return new MainWindowViewModel(
            NullLogger<MainWindowViewModel>.Instance,
            _navigation,
            _settings,
            Substitute.For<IThemeService>(),
            Substitute.For<IWindowManagerService>(),
            Substitute.For<IUpdateService>(),
            Substitute.For<IProviderService>(),
            _auth,
            Substitute.For<ISyncClientService>(),
            _assignments,
            _policy)
        {
            Mode = mode,
        };
    }

    private void SurfaceOffers(params string[] skillNames) =>
        _assignments.GetSurfaceAsync(Arg.Any<CancellationToken>()).Returns(
            new AssignmentSurface(
                true,
                skillNames.Select(n => new AssignmentSkill(n, n, "research", [])).ToList()));

    private static async Task InitializeAsync(MainWindowViewModel vm)
    {
        await vm.InitializeAsync();
        await vm.PendingAssignmentSurfaceProbe;
    }

    [Fact]
    public async Task AnAvailableSurface_ShowsTheEntry_InAssistantMode()
    {
        SurfaceOffers("research");
        using var vm = CreateSut(WindowMode.Assistant);

        await InitializeAsync(vm);

        Assert.True(vm.IsAssignmentsNavVisible);
    }

    [Fact]
    public async Task AHiddenSurface_HidesTheEntry_InAssistantMode()
    {
        _assignments.GetSurfaceAsync(Arg.Any<CancellationToken>()).Returns(AssignmentSurface.Hidden);
        using var vm = CreateSut(WindowMode.Assistant);

        await InitializeAsync(vm);

        Assert.False(vm.IsAssignmentsNavVisible);
    }

    [Fact]
    public async Task AnAvailableSurface_StillHidesTheEntry_InOptimizeMode()
    {
        SurfaceOffers("research");
        using var vm = CreateSut(WindowMode.Optimize);

        await InitializeAsync(vm);

        Assert.False(vm.IsAssignmentsNavVisible);
    }

    [Fact]
    public async Task AProbeThatThrows_HidesTheEntry()
    {
        _assignments.GetSurfaceAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("no server"));
        using var vm = CreateSut(WindowMode.Assistant);

        await InitializeAsync(vm);

        Assert.False(vm.IsAssignmentsNavVisible);
    }

    /// <summary>Pia commonly starts before the user is signed in, when the probe can only answer "hidden".</summary>
    [Fact]
    public async Task ASurfaceThatBecomesAvailableAtSignIn_ShowsTheEntryWithoutARestart()
    {
        _assignments.GetSurfaceAsync(Arg.Any<CancellationToken>()).Returns(AssignmentSurface.Hidden);
        using var vm = CreateSut(WindowMode.Assistant);
        await InitializeAsync(vm);
        Assert.False(vm.IsAssignmentsNavVisible);

        SurfaceOffers("research");
        _auth.LoginStateChanged += Raise.Event<EventHandler<bool>>(_auth, true);
        await vm.PendingAssignmentSurfaceProbe;

        Assert.True(vm.IsAssignmentsNavVisible);
    }

    [Fact]
    public void TheEntry_NavigatesToTheAssignmentsView()
    {
        using var vm = CreateSut(WindowMode.Assistant);

        vm.NavigationCommand.Execute("Assignments");

        _navigation.Received(1).NavigateTo<AssignmentsViewModel>();
    }

    /// <summary>The route string has to equal the ViewModel's type name minus "ViewModel", because that is how
    /// <c>CurrentNavigationItem</c> derives the sidebar's active-item key. Nothing else enforces it, and a
    /// mismatch fails silently as a dead highlight.</summary>
    [Fact]
    public void TheEntry_NavigatesToTheRoutinesView()
    {
        using var vm = CreateSut(WindowMode.Assistant);

        vm.NavigationCommand.Execute("Routines");

        _navigation.Received(1).NavigateTo<RoutinesViewModel>();
        Assert.Equal("Routines", nameof(RoutinesViewModel).Replace("ViewModel", ""));
    }

    /// <summary>The sidebar label stays "Memory" while the route string is "Vault" — they differ on purpose.</summary>
    [Fact]
    public void TheEntry_NavigatesToTheVaultView()
    {
        using var vm = CreateSut(WindowMode.Assistant);

        vm.NavigationCommand.Execute("Vault");

        _navigation.Received(1).NavigateTo<VaultViewModel>();
        Assert.Equal("Vault", nameof(VaultViewModel).Replace("ViewModel", ""));
    }

    [Fact]
    public void TheAssistantShortcut_StillReachesTheVault()
    {
        using var vm = CreateSut(WindowMode.Assistant);

        vm.NavigationCommand.Execute("Shortcut2");

        _navigation.Received(1).NavigateTo<VaultViewModel>();
    }

    [Fact]
    public void TheAssistantShortcuts_KeepTheirDestinations()
    {
        using var vm = CreateSut(WindowMode.Assistant);

        vm.NavigationCommand.Execute("Shortcut3");
        vm.NavigationCommand.Execute("Shortcut4");

        _navigation.Received(1).NavigateTo<RemindersViewModel>();
        _navigation.Received(1).NavigateTo<SettingsViewModel>();
        _navigation.DidNotReceive().NavigateTo<AssignmentsViewModel>();
    }

    // The theme toggle is a nav item, not a Settings control, so the lock has to live on the command.
    [Fact]
    public void AnEnforcedTheme_DisablesTheThemeToggle()
    {
        _policy.IsEnforced(nameof(AppSettings.Theme)).Returns(true);

        using var vm = CreateSut(WindowMode.Assistant);

        Assert.False(vm.ToggleThemeCommand.CanExecute(null));
    }

    [Fact]
    public void TheThemeToggle_ReEvaluatesWhenTheLocksMove()
    {
        // The handler marshals, and CreateSut's plain context posts to the thread pool.
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());
        try
        {
            using var vm = CreateSut(WindowMode.Assistant);
            Assert.True(vm.ToggleThemeCommand.CanExecute(null));

            var reEvaluations = 0;
            vm.ToggleThemeCommand.CanExecuteChanged += (_, _) => reEvaluations++;
            _policy.IsEnforced(nameof(AppSettings.Theme)).Returns(true);
            _policy.LocksChanged += Raise.EventWith(EventArgs.Empty);

            Assert.Equal(1, reEvaluations);
            Assert.False(vm.ToggleThemeCommand.CanExecute(null));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
}
