using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// T1-1's settings surface: the run-pool width slider (<see cref="AssistantSettingsViewModel.MaxParallelBackgroundRuns"/>).
/// Four facts, each about a step the slider needs and that nothing else covers — the parse test reads the
/// binding PATH without evaluating it, and <c>AppSettings</c>'s own tests cover the model, not the projection.
/// <para>
/// The clamp is the point rather than a nicety: the launcher's pool is built with the hard cap as its
/// semaphore <c>maxCount</c>, so an out-of-range width is not a cosmetic issue at the other end. The VM clamps
/// on the same pair the pool does, which is what keeps the slider from ever showing a width the pool is not
/// running at.
/// </para>
/// <para>
/// The five positional sub-ViewModels are constructed here rather than shared with
/// <c>AssistantSettingsRosterTests</c>: that fixture keeps its scheduled-jobs substitute on the instance for
/// its OWN wiring fact, so a shared base would couple two suites through mutable fixture state to save a
/// dozen lines of <c>Substitute.For</c>.
/// </para>
/// </summary>
public class AssistantSettingsRunPoolTests
{
    private static (AssistantSettingsViewModel Sut, AppSettings Stored, ISettingsService Service) Create(
        AppSettings? initial = null)
    {
        var stored = initial ?? new AppSettings();

        var settingsService = Substitute.For<ISettingsService>();
        settingsService.GetSettingsAsync().Returns(_ => Task.FromResult(stored));
        settingsService.SaveSettingsAsync(Arg.Any<AppSettings>()).Returns(Task.CompletedTask);

        var localization = Substitute.For<ILocalizationService>();
        localization.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns("display");
        localization[Arg.Any<string>()].Returns("display");

        var workingDirectoryService = Substitute.For<IWorkingDirectoryService>();
        workingDirectoryService.ListSubfolders(Arg.Any<string>()).Returns(Array.Empty<string>());

        var dialogService = Substitute.For<IDialogService>();

        var providersVm = new ProvidersSettingsViewModel(
            null!, NullLogger<SettingsViewModel>.Instance,
            Substitute.For<IProviderService>(), settingsService, dialogService,
            Substitute.For<global::Wpf.Ui.ISnackbarService>(), Substitute.For<IAuthService>(),
            localization, Substitute.For<IPolicyService>());

        var personasVm = new PersonaSettingsViewModel(
            NullLogger<SettingsViewModel>.Instance, Substitute.For<IPersonaService>(),
            Substitute.For<IProviderService>(), Substitute.For<ITextOptimizationService>(),
            dialogService, Substitute.For<global::Wpf.Ui.ISnackbarService>(), localization,
            Substitute.For<IAuthService>());

        var toolPermissionsVm = new ToolPermissionsSettingsViewModel(
            Substitute.For<IToolPermissionService>(), Substitute.For<IPluginService>(),
            NullLogger<SettingsViewModel>.Instance);

        var meetingVm = new MeetingSettingsViewModel(
            NullLogger<SettingsViewModel>.Instance, settingsService, localization);

        var scheduledJobs = Substitute.For<IScheduledJobService>();
        scheduledJobs.GetAllAsync().Returns(Array.Empty<ScheduledJob>());
        var scheduledProviders = Substitute.For<IProviderService>();
        scheduledProviders.GetProvidersAsync().Returns(Array.Empty<AiProvider>());
        var scheduledJobsVm = new ScheduledJobsSettingsViewModel(
            scheduledJobs, Substitute.For<IScheduledJobRunner>(),
            scheduledProviders, localization, NullLogger<SettingsViewModel>.Instance);

        var sut = new AssistantSettingsViewModel(
            providersVm, personasVm, toolPermissionsVm, meetingVm, scheduledJobsVm,
            NullLogger<SettingsViewModel>.Instance, settingsService, Substitute.For<IAssistantChatService>(),
            dialogService, localization, Substitute.For<IAssistantFolderRelocationService>(),
            workingDirectoryService, personaService: null);

        return (sut, stored, settingsService);
    }

    [Fact]
    public async Task Initialize_ReClampsAStoredWidthThatIsOutOfRange()
    {
        // A stored 0 is the case the read-side clamp exists for: a settings document written by an older build
        // has no member at all and deserializes to 0, and a pool of width 0 has no permits and nothing that
        // could ever release one. The slider must not show that width either.
        var (sut, _, _) = Create(new AppSettings { MaxParallelBackgroundRuns = 0 });

        await sut.InitializeAsync();

        Assert.Equal(AppSettings.MinParallelBackgroundRuns, sut.MaxParallelBackgroundRuns);
    }

    [Fact]
    public async Task Initialize_LoadsAStoredWidthUnchangedWhenItIsInRange()
    {
        // The discriminator for the fact above: without this one, a VM that ignored the setting entirely and
        // always showed the default would pass the clamp test whenever the default was in range.
        var (sut, _, _) = Create(new AppSettings { MaxParallelBackgroundRuns = 5 });

        await sut.InitializeAsync();

        Assert.Equal(5, sut.MaxParallelBackgroundRuns);
    }

    /// <summary>
    /// REVIEW FIX. The Slider is bound to the property, which raises on load; the TextBlock beneath it
    /// (<c>AssistantView.xaml</c>) is bound to the computed <c>…Display</c>, which
    /// <c>OnMaxParallelBackgroundRunsChanged</c> deliberately does NOT raise while <c>_isLoading</c>. The post-load
    /// refresh block is therefore the only thing that can move the label, and it was omitted there — so a stored
    /// width of 5 showed a slider at 5 above a label still reading the default, until the user dragged it.
    /// <para>
    /// Asserted as a raised NOTIFICATION, not as a string: this fixture's <c>ILocalizationService.Format</c>
    /// returns "display" for every key, so any assertion on the text would pass on a VM that never notified.
    /// </para>
    /// <para>Neutralize: delete the <c>OnPropertyChanged(nameof(MaxParallelBackgroundRunsDisplay))</c> line from
    /// <c>InitializeAsync</c>'s refresh block → red.</para>
    /// </summary>
    [Fact]
    public async Task Initialize_RaisesTheWidthReadout_SoTheLabelAgreesWithTheSlider()
    {
        var (sut, _, _) = Create(new AppSettings { MaxParallelBackgroundRuns = 5 });

        var raised = new List<string>();
        sut.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

        await sut.InitializeAsync();

        Assert.Contains(nameof(sut.MaxParallelBackgroundRunsDisplay), raised);
    }

    /// <summary>
    /// The clamp, and the SAVE CALL that makes a new width live.
    /// <para>
    /// REVIEW FIX on the second half. It used to poll the shared <c>AppSettings</c> instance the fixture hands back
    /// from every <c>GetSettingsAsync</c>, which <c>SaveSettingsAsync</c> mutates IN PLACE before it reaches
    /// <c>ISettingsService.SaveSettingsAsync</c> — so deleting that call left the test green while nothing was
    /// written and <c>SettingsChanged</c> never fired, which is precisely the failure that kills T1-1's live
    /// resize (<c>HeadlessRunLauncher.OnSettingsChanged</c> is the only thing that resizes the pool without a
    /// restart). What is observed now is the call itself, recorded with its payload.
    /// </para>
    /// <para>
    /// Recorded rather than asserted with <c>Received()</c>: the save is fire-and-forget from the setter, so a
    /// <c>Received()</c> read taken the moment the value appears can precede the call by a scheduling quantum.
    /// The recorder is re-stubbed AFTER <c>InitializeAsync</c> so no save made during load can satisfy it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SettingTheWidth_ClampsToTheCap_AndSavesTheClampedValueThroughTheService()
    {
        var (sut, _, settingsService) = Create();
        await sut.InitializeAsync();

        var savedWidths = new List<int>();
        settingsService.SaveSettingsAsync(Arg.Any<AppSettings>()).Returns(ci =>
        {
            lock (savedWidths) savedWidths.Add(((AppSettings)ci[0]).MaxParallelBackgroundRuns);
            return Task.CompletedTask;
        });

        sut.MaxParallelBackgroundRuns = AppSettings.MaxParallelBackgroundRunsCap + 4;

        // The setter re-enters with the clamped value (the Scheduled* pattern), so the surface never holds an
        // out-of-range width even for one notification.
        Assert.Equal(AppSettings.MaxParallelBackgroundRunsCap, sut.MaxParallelBackgroundRuns);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (savedWidths)
                if (savedWidths.Contains(AppSettings.MaxParallelBackgroundRunsCap)) break;
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        lock (savedWidths)
            Assert.Contains(AppSettings.MaxParallelBackgroundRunsCap, savedWidths);
    }
}
