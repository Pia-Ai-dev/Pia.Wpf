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

/// <summary>
/// <c>FirstRunWizardWindow</c> is the ONE view <c>00-OVERVIEW.md</c> (Row 12) names as unparseable under
/// the shared STA test host: its <c>Icon="pack://application:,,,/Resources/Icons/Pia.ico"</c> is an
/// authority-only pack URI, which resolves against <c>Application.ResourceAssembly</c> — and that getter
/// latches to whichever assembly reads it FIRST, which under this host is <c>Pia.Wpf.Tests</c>, not
/// <c>Pia.Wpf</c>. The prior fix attempt (assigning <c>Application.ResourceAssembly</c> in
/// <c>WpfStaHost</c>) was tried and reverted — a static-mutation approach fighting a static that was
/// already latched by the read guarding it.
/// <para>
/// This fact takes a different approach that the revert does not bear on: qualify the pack URIs
/// themselves with <c>Pia.Wpf;component</c>, so resolution never consults
/// <c>Application.ResourceAssembly</c> at all. <see cref="Pia.Views.WizardSteps.WelcomeStep"/>, nested here at
/// step 0, carries the same defect for <c>Resources/Images/Pia_Persona.png</c> — fixing both is what lets
/// this window (and the wizard step tree under it) construct under the shared host.
/// </para>
/// </summary>
[Collection("WpfApplicationStatic")]
public class FirstRunWizardWindowParseTests
{
    /// <summary>
    /// A floor, not a count: WelcomeStep alone (UiLanguages, UiLanguage) plus the window's own nav/step
    /// bindings comfortably clears this. Kept low on purpose — this fact exists to prove the window
    /// PARSES at all, not to pin every binding in seven nested wizard steps.
    /// </summary>
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
        // The regression this fact guards: an authority-only pack URI Icon (or, nested one level down in
        // WelcomeStep, ImageBrush) throws IOException("Cannot locate resource '...'") the moment
        // InitializeComponent runs under a host whose entry assembly isn't Pia.Wpf. WpfStaHost.Run
        // rethrows on the calling thread with the original stack, so a regression here surfaces as this
        // fact failing with that exact IOException, not a timeout.
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

        // Proves the nested WelcomeStep (step 0, no DataContext of its own) was actually walked: its
        // UiLanguages/UiLanguage bindings only resolve against FirstRunWizardViewModel if the walker
        // correctly treated the inherited context as the parent's.
        Assert.Contains(bindings, b => b.Contains("=UiLanguages [FirstRunWizardViewModel]"));

        var unresolved = bindings.Where(b => b.EndsWith("UNRESOLVED", StringComparison.Ordinal)).ToArray();
        Assert.True(unresolved.Length == 0,
            "these Binding paths in FirstRunWizardWindow.xaml (including WelcomeStep) do not resolve to a " +
            "public property on FirstRunWizardViewModel, so they bind to nothing and fail silently at " +
            $"runtime: {string.Join(", ", unresolved)}");
    }
}
