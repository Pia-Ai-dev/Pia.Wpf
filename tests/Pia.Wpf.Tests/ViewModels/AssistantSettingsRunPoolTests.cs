using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>The VM clamps the width on the same pair the launcher's pool uses for its semaphore, so the slider can
/// never show a width the pool is not running at.</summary>
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

        var sut = new AssistantSettingsViewModel(
            providersVm, personasVm, toolPermissionsVm, meetingVm,
            NullLogger<SettingsViewModel>.Instance, settingsService, Substitute.For<IAssistantChatService>(),
            dialogService, localization, Substitute.For<IAssistantFolderRelocationService>(),
            workingDirectoryService, personaService: null);

        return (sut, stored, settingsService);
    }

    [Fact]
    public async Task Initialize_ReClampsAStoredWidthThatIsOutOfRange()
    {
        // A settings document from an older build has no member at all and deserializes to 0; a pool of width 0 has
        // no permits and nothing that could ever release one.
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

    /// <summary>The <c>…Display</c> readout is not raised while loading, so only the post-load refresh can move the
    /// label; asserted as a notification because this fixture's <c>Format</c> returns "display" for every key.</summary>
    [Fact]
    public async Task Initialize_RaisesTheWidthReadout_SoTheLabelAgreesWithTheSlider()
    {
        var (sut, _, _) = Create(new AppSettings { MaxParallelBackgroundRuns = 5 });

        var raised = new List<string>();
        sut.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

        await sut.InitializeAsync();

        Assert.Contains(nameof(sut.MaxParallelBackgroundRunsDisplay), raised);
    }

    /// <summary>The save is fire-and-forget from the setter, so the call is recorded rather than read with
    /// <c>Received()</c>, and the recorder is re-stubbed after load so no save made during load can satisfy it.</summary>
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
