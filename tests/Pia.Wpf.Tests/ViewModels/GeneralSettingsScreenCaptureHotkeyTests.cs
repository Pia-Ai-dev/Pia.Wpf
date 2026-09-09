using System.Threading;
using System.Windows.Input;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>The Settings row for the screen-capture hotkey: register first, save only when Windows agreed.</summary>
public sealed class GeneralSettingsScreenCaptureHotkeyTests
{
    private static readonly KeyboardShortcut Chosen = new(KeyModifiers.Control | KeyModifiers.Shift, Key.S, 0x53);

    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
    private readonly ITrayIconService _tray = Substitute.For<ITrayIconService>();
    private readonly ILocalizationService _localization = Substitute.For<ILocalizationService>();
    private readonly Wpf.Ui.ISnackbarService _snackbar = Substitute.For<Wpf.Ui.ISnackbarService>();
    private readonly AppSettings _stored = new();

    public GeneralSettingsScreenCaptureHotkeyTests()
    {
        _settings.GetSettingsAsync().Returns(_ => _stored);
        _localization.CurrentLanguage.Returns(TargetLanguage.EN);
        _localization[Arg.Any<string>()].Returns(c => (string)c[0]);
        _localization.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(c => (string)c[0]);
        _dialogs.ShowHotkeyCaptureDialogAsync().Returns(Chosen);
        _tray.UpdateScreenCaptureHotkey(Arg.Any<KeyboardShortcut?>()).Returns(true);
    }

    [Fact]
    public async Task CaptureHotkey_RegistersThenSaves()
    {
        var vm = await LoadedVm();

        await vm.CaptureScreenCaptureHotkeyCommand.ExecuteAsync(null);

        _tray.Received(1).UpdateScreenCaptureHotkey(Chosen);
        await _settings.Received(1).SaveSettingsAsync(Arg.Is<AppSettings>(s => s.ScreenCaptureHotkey == Chosen));
        Assert.Equal(Chosen.DisplayText, vm.ScreenCaptureHotkeyDisplayText);
    }

    [Fact]
    public async Task CaptureHotkey_RefusedByWindows_SaysSo_AndSavesNothing()
    {
        _tray.UpdateScreenCaptureHotkey(Arg.Any<KeyboardShortcut?>()).Returns(false);
        var vm = await LoadedVm();

        await vm.CaptureScreenCaptureHotkeyCommand.ExecuteAsync(null);

        _ = _localization.Received(1)["Msg_Settings_HotkeyUnavailable"];
        await _settings.DidNotReceive().SaveSettingsAsync(Arg.Any<AppSettings>());
        Assert.Equal("Msg_Settings_HotkeyNotSet", vm.ScreenCaptureHotkeyDisplayText);
    }

    [Fact]
    public async Task CaptureHotkey_TakenByFastPath_IsRefusedBeforeRegistering()
    {
        _stored.FastPathHotkey = Chosen;
        var vm = await LoadedVm();

        await vm.CaptureScreenCaptureHotkeyCommand.ExecuteAsync(null);

        _tray.DidNotReceive().UpdateScreenCaptureHotkey(Arg.Any<KeyboardShortcut?>());
        _localization.Received(1).Format("Msg_Settings_HotkeyAlreadyAssigned", Arg.Any<object[]>());
        await _settings.DidNotReceive().SaveSettingsAsync(Arg.Any<AppSettings>());
    }

    [Fact]
    public async Task CaptureHotkey_Cancelled_ChangesNothing()
    {
        _dialogs.ShowHotkeyCaptureDialogAsync().Returns((KeyboardShortcut?)null);
        var vm = await LoadedVm();

        await vm.CaptureScreenCaptureHotkeyCommand.ExecuteAsync(null);

        _tray.DidNotReceive().UpdateScreenCaptureHotkey(Arg.Any<KeyboardShortcut?>());
        await _settings.DidNotReceive().SaveSettingsAsync(Arg.Any<AppSettings>());
    }

    [Fact]
    public async Task ClearHotkey_SavesNull_AndUnregisters()
    {
        _stored.ScreenCaptureHotkey = Chosen;
        var vm = await LoadedVm();

        await vm.ClearScreenCaptureHotkeyCommand.ExecuteAsync(null);

        await _settings.Received(1).SaveSettingsAsync(Arg.Is<AppSettings>(s => s.ScreenCaptureHotkey == null));
        _tray.Received(1).UpdateScreenCaptureHotkey(null);
        Assert.Equal("Msg_Settings_HotkeyNotSet", vm.ScreenCaptureHotkeyDisplayText);
    }

    [Fact]
    public async Task ApplySettings_ShowsNotSet_WhenNoneIsConfigured()
    {
        var vm = await LoadedVm();

        Assert.Equal("Msg_Settings_HotkeyNotSet", vm.ScreenCaptureHotkeyDisplayText);
    }

    [Fact]
    public async Task ApplySettings_ShowsTheStoredCombination()
    {
        _stored.ScreenCaptureHotkey = Chosen;

        var vm = await LoadedVm();

        Assert.Equal(Chosen.DisplayText, vm.ScreenCaptureHotkeyDisplayText);
    }

    private async Task<GeneralSettingsViewModel> LoadedVm()
    {
        // UiThreadViewModel captures a context at construction.
        if (SynchronizationContext.Current is null)
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());

        var logger = NullLogger<SettingsViewModel>.Instance;
        var policy = Substitute.For<IPolicyService>();
        var vm = new GeneralSettingsViewModel(
            logger, _settings, Substitute.For<ITranscriptionService>(), _dialogs, _tray,
            Substitute.For<ITtsService>(), _snackbar, _localization,
            Substitute.For<IAutostartService>(), policy,
            new PrivacySettingsViewModel(logger, _settings, policy),
            Substitute.For<ISyncClientService>(),
            Substitute.For<IDiagnosticsExportService>());

        await vm.InitializeAsync();
        _settings.ClearReceivedCalls();
        return vm;
    }
}
