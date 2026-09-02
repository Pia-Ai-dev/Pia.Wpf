using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>The retention slider snaps to 7-day ticks. The VM has to put a loaded value on that same grid
/// itself, or WPF's own coercion fires the TwoWay write-back and merely opening the page rewrites a
/// policy-supplied window.</summary>
public class AssistantSettingsRetentionTests
{
    private static (AssistantSettingsViewModel Sut, ISettingsService Service) Create(int storedDays)
    {
        var stored = new AppSettings { ChatHistoryRetentionDays = storedDays };

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
            Substitute.For<global::Wpf.Ui.ISnackbarService>(), localization,
            Substitute.For<IAuthService>(), settingsService, Substitute.For<IPolicyService>());

        var toolPermissionsVm = new ToolPermissionsSettingsViewModel(
            Substitute.For<IToolPermissionService>(), Substitute.For<IPluginService>(),
            NullLogger<SettingsViewModel>.Instance);

        var meetingVm = new MeetingSettingsViewModel(
            NullLogger<SettingsViewModel>.Instance, settingsService, localization,
            Substitute.For<IPolicyService>());

        var sut = new AssistantSettingsViewModel(
            providersVm, personasVm, toolPermissionsVm, meetingVm,
            NullLogger<SettingsViewModel>.Instance, settingsService,
            Substitute.For<IAssistantChatService>(), dialogService, localization,
            Substitute.For<IAssistantFolderRelocationService>(), workingDirectoryService,
            Substitute.For<IPolicyService>(), personaService: null);

        return (sut, settingsService);
    }

    [Theory]
    [InlineData(14, 14, 14)]                                                       // already on the grid
    [InlineData(AppSettings.DefaultChatHistoryRetentionDays, 180, 182)]            // 180 is not a whole number of weeks
    [InlineData(45, 45, 42)]                                                       // thumb rounds, window does not
    [InlineData(0, AppSettings.MinChatHistoryRetentionDays, 7)]                    // below the slider floor
    [InlineData(5000, AppSettings.MaxChatHistoryRetentionDaysCap, 730)]            // the cap is the top endpoint
    [InlineData(729, 729, 728)]                                                    // nearest tick wins a tie with the cap
    public async Task Initialize_KeepsTheStoredWindowAndSnapsOnlyTheThumb(
        int stored, int expectedWindow, int expectedThumb)
    {
        var (sut, _) = Create(stored);

        await sut.InitializeAsync();

        Assert.Equal(expectedWindow, sut.ChatHistoryRetentionDays);
        Assert.Equal(expectedThumb, sut.RetentionSliderDays);
    }

    [Fact]
    public async Task TheSliderWritingBackItsOwnRenderPosition_ChangesNothing()
    {
        // What WPF does after coercing an off-grid Value: pushes 42 back for a stored 45. Not a gesture.
        var (sut, service) = Create(45);
        await sut.InitializeAsync();

        sut.RetentionSliderDays = 42;

        Assert.Equal(45, sut.ChatHistoryRetentionDays);
        await service.DidNotReceive().SaveSettingsAsync(Arg.Any<AppSettings>());
    }

    [Fact]
    public async Task DraggingToADifferentTick_WritesTheWindowThrough()
    {
        // The discriminator: without this, a setter that ignored everything would pass the fact above.
        var (sut, _) = Create(45);
        await sut.InitializeAsync();

        sut.RetentionSliderDays = 49;

        Assert.Equal(49, sut.ChatHistoryRetentionDays);
    }

    [Fact]
    public async Task Initialize_DoesNotSaveTheLoadedWindow()
    {
        var (sut, service) = Create(45);

        await sut.InitializeAsync();

        await service.DidNotReceive().SaveSettingsAsync(Arg.Any<AppSettings>());
    }
}
