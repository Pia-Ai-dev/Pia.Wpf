using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.E2EE;
using Pia.Services.Interfaces;
using Pia.Shared.E2EE;
using Pia.ViewModels;
using Wpf.Ui;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>Authority-only pack URIs resolve against <c>Application.ResourceAssembly</c>, which latches to whichever
/// assembly reads it first, so this window and its wizard steps qualify theirs with <c>Pia.Wpf;component</c>.</summary>
[Collection("WpfApplicationStatic")]
public class FirstRunWizardWindowParseTests
{
    /// <summary>A non-vacuity floor, not a count: the point is that the window parses at all.</summary>
    private const int MinimumBoundPaths = 5;

    private static Pia.Views.FirstRunWizardWindow CreateSut()
    {
        var settings = Substitute.For<ISettingsService>();
        var memory = Substitute.For<IMemoryService>();
        var voice = Substitute.For<IVoiceInputService>();
        var loc = Substitute.For<ILocalizationService>();
        var auth = Substitute.For<IAuthService>();
        var providers = Substitute.For<IProviderService>();
        var sync = Substitute.For<ISyncClientService>();
        var deviceMgmt = Substitute.For<IDeviceManagementService>();
        var deviceKeys = Substitute.For<IDeviceKeyService>();
        var output = Substitute.For<IOutputService>();
        var policy = Substitute.For<IPolicyService>();
        policy.IsLoginProviderAllowed(Arg.Any<string>()).Returns(true);
        settings.GetSettingsAsync().Returns(new AppSettings());
        deviceKeys.GetFingerprint().Returns("FP");

        var onboardingVm = new E2EEOnboardingViewModel(
            deviceMgmt, deviceKeys, Substitute.For<IE2EEService>(),
            sync, settings, NullLogger<E2EEOnboardingViewModel>.Instance);
        var e2eeSetupVm = new E2EESetupStepViewModel(
            deviceMgmt, deviceKeys, sync, output,
            NullLogger<E2EESetupStepViewModel>.Instance);

        var viewModel = new FirstRunWizardViewModel(
            settings, memory, voice, loc, auth, providers, sync,
            deviceMgmt, policy, onboardingVm, e2eeSetupVm,
            NullLogger<FirstRunWizardViewModel>.Instance);

        return new Pia.Views.FirstRunWizardWindow(
            viewModel,
            Substitute.For<IContentDialogService>(),
            Substitute.For<ISnackbarService>());
    }

    [Fact]
    public void Constructs_WithoutThrowing()
    {
        // An authority-only pack URI throws IOException("Cannot locate resource") from InitializeComponent when the
        // entry assembly isn't Pia.Wpf; WpfStaHost.Run rethrows it here rather than timing out.
        var window = WpfStaHost.Run(CreateSut);
        Assert.NotNull(window);
    }

    [Fact]
    public void EveryBindingPath_ResolvesOnTheViewModelThatMarkupRootsItAt()
    {
        var bindings = WpfStaHost.Run(() =>
            BindingPathWalker.Describe(CreateSut(), typeof(FirstRunWizardViewModel)));

        Assert.True(bindings.Length >= MinimumBoundPaths,
            $"only {bindings.Length} bound paths were found in the parsed FirstRunWizardWindow, which is " +
            $"below the non-vacuity floor of {MinimumBoundPaths}. The walk is logical, so suspect a " +
            "container that no longer reports logical children rather than a genuine removal.");

        // The nested WelcomeStep has no DataContext of its own, so this resolves only if the walker used the parent's.
        Assert.Contains(bindings, b => b.Contains("=UiLanguages [FirstRunWizardViewModel]"));

        var unresolved = bindings.Where(b => b.EndsWith("UNRESOLVED", StringComparison.Ordinal)).ToArray();
        Assert.True(unresolved.Length == 0,
            "these Binding paths in FirstRunWizardWindow.xaml (including WelcomeStep) do not resolve to a " +
            "public property on FirstRunWizardViewModel, so they bind to nothing and fail silently at " +
            $"runtime: {string.Join(", ", unresolved)}");
    }
}
