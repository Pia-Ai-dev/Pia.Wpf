using System.Threading;
using System.Windows.Input;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// The Settings row for the Optimize hotkey. It is the only global hotkey with a built-in default, which is
/// why reset and remove are two verbs here rather than one: reset puts Ctrl+Alt+O back, remove leaves the
/// mode with no hotkey at all.
/// </summary>
public sealed class GeneralSettingsOptimizeHotkeyTests
{
    private static readonly KeyboardShortcut Chosen = new(KeyModifiers.Control | KeyModifiers.Alt, Key.K, 0x4B);

    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
    private readonly ITrayIconService _tray = Substitute.For<ITrayIconService>();
    private readonly ILocalizationService _localization = Substitute.For<ILocalizationService>();
    private readonly AppSettings _stored = new();

    public GeneralSettingsOptimizeHotkeyTests()
    {
        _settings.GetSettingsAsync().Returns(_ => _stored);
        _localization.CurrentLanguage.Returns(TargetLanguage.EN);
        _localization[Arg.Any<string>()].Returns(c => (string)c[0]);
        _localization.Format(Arg.Any<string>(), Arg.Any<object[]>()).Returns(c => (string)c[0]);
        _dialogs.ShowHotkeyCaptureDialogAsync().Returns(Chosen);
    }

    [Fact]
    public async Task RemoveHotkey_SavesNull_AndUnregisters()
    {
        _stored.OptimizeHotkey = Chosen;
        var vm = await LoadedVm();

        await vm.RemoveOptimizeHotkeyCommand.ExecuteAsync(null);

        await _settings.Received(1).SaveSettingsAsync(Arg.Is<AppSettings>(s => s.OptimizeHotkey == null));
        _tray.Received(1).UpdateHotkey(WindowMode.Optimize, null);
        Assert.Equal("Msg_Settings_HotkeyNotSet", vm.OptimizeHotkeyDisplayText);
    }

    [Fact]
    public async Task ResetHotkey_PutsTheDefaultBack()
    {
        _stored.OptimizeHotkey = null;
        var vm = await LoadedVm();

        await vm.ClearOptimizeHotkeyCommand.ExecuteAsync(null);

        var expected = KeyboardShortcut.DefaultCtrlAltO();
        await _settings.Received(1).SaveSettingsAsync(Arg.Is<AppSettings>(s => s.OptimizeHotkey == expected));
        _tray.Received(1).UpdateHotkey(WindowMode.Optimize, expected);
        Assert.Equal(expected.DisplayText, vm.OptimizeHotkeyDisplayText);
    }

    [Fact]
    public async Task ApplySettings_ShowsNotSet_WhenTheHotkeyWasRemoved()
    {
        _stored.OptimizeHotkey = null;

        var vm = await LoadedVm();

        Assert.Equal("Msg_Settings_HotkeyNotSet", vm.OptimizeHotkeyDisplayText);
    }

    /// <summary>A removed hotkey frees the combination, so the next capture may take it.</summary>
    [Fact]
    public async Task CaptureAnotherHotkey_DoesNotConflictWithARemovedOne()
    {
        _stored.OptimizeHotkey = null;
        _stored.AssistantHotkey = null;
        var vm = await LoadedVm();

        await vm.CaptureAssistantHotkeyCommand.ExecuteAsync(null);

        await _settings.Received(1).SaveSettingsAsync(Arg.Is<AppSettings>(s => s.AssistantHotkey == Chosen));
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
            Substitute.For<ITtsService>(), Substitute.For<Wpf.Ui.ISnackbarService>(), _localization,
            Substitute.For<IAutostartService>(), policy,
            new PrivacySettingsViewModel(logger, _settings, policy),
            Substitute.For<ISyncClientService>(),
            Substitute.For<IDiagnosticsExportService>());

        await vm.InitializeAsync();
        _settings.ClearReceivedCalls();
        return vm;
    }
}
