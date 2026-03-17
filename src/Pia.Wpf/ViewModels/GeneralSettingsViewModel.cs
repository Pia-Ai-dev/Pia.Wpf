using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Interfaces;
using System.Collections.ObjectModel;

namespace Pia.ViewModels;

public partial class GeneralSettingsViewModel : ObservableObject
{
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly ISettingsService _settingsService;
    private readonly ITranscriptionService _transcriptionService;
    private readonly IDialogService _dialogService;
    private readonly ITrayIconService _trayIconService;
    private readonly ITtsService _ttsService;
    private readonly Wpf.Ui.ISnackbarService _snackbarService;
    private readonly ILocalizationService _localizationService;
    private bool _isLoading;

    public GeneralSettingsViewModel(
        ILogger<SettingsViewModel> logger,
        ISettingsService settingsService,
        ITranscriptionService transcriptionService,
        IDialogService dialogService,
        ITrayIconService trayIconService,
        ITtsService ttsService,
        Wpf.Ui.ISnackbarService snackbarService,
        ILocalizationService localizationService)
    {
        _logger = logger;
        _settingsService = settingsService;
        _transcriptionService = transcriptionService;
        _dialogService = dialogService;
        _trayIconService = trayIconService;
        _ttsService = ttsService;
        _snackbarService = snackbarService;
        _localizationService = localizationService;
    }

    // Appearance
    [ObservableProperty]
    private TargetLanguage _uiLanguage;

    [ObservableProperty]
    private bool _startMinimized;

    // Hotkeys
    [ObservableProperty]
    private string _optimizeHotkeyDisplayText = "Ctrl+Alt+O";

    [ObservableProperty]
    private string _assistantHotkeyDisplayText = "";

    [ObservableProperty]
    private string _researchHotkeyDisplayText = "";

    private KeyboardShortcut _optimizeHotkey = KeyboardShortcut.DefaultCtrlAltO();
    private KeyboardShortcut? _assistantHotkey = KeyboardShortcut.DefaultCtrlAltP();
    private KeyboardShortcut? _researchHotkey = KeyboardShortcut.DefaultCtrlAltR();

    // Speech
    [ObservableProperty]
    private WhisperModelSize _whisperModel;

    [ObservableProperty]
    private TargetSpeechLanguage _targetSpeechLanguage;

    [ObservableProperty]
    private ObservableCollection<TtsVoice> _ttsVoices = new();

    [ObservableProperty]
    private string _selectedVoiceKey = "en_US-lessac-medium";

    // Inner tab index
    [ObservableProperty]
    private int _selectedInnerTabIndex;

    public IEnumerable<WhisperModelSize> WhisperModels => Enum.GetValues<WhisperModelSize>();
    public IEnumerable<TargetSpeechLanguage> TargetSpeechLanguages => Enum.GetValues<TargetSpeechLanguage>();
    public IEnumerable<TargetLanguage> UiLanguages => Enum.GetValues<TargetLanguage>();

    partial void OnUiLanguageChanged(TargetLanguage value)
    {
        if (!_isLoading)
        {
            _localizationService.SetLanguage(value);
            SafeFireAndForget(SaveSettingsAsync());
        }
    }

    partial void OnStartMinimizedChanged(bool value)
    {
        if (!_isLoading) SafeFireAndForget(SaveSettingsAsync());
    }

    partial void OnWhisperModelChanged(WhisperModelSize value)
    {
        if (!_isLoading) SafeFireAndForget(SaveSettingsAsync());
    }

    partial void OnTargetSpeechLanguageChanged(TargetSpeechLanguage value)
    {
        if (!_isLoading) SafeFireAndForget(SaveSettingsAsync());
    }

    public async Task InitializeAsync()
    {
        _isLoading = true;

        var settings = await _settingsService.GetSettingsAsync();
        UiLanguage = _localizationService.CurrentLanguage;
        StartMinimized = settings.StartMinimized;
        WhisperModel = settings.WhisperModel;
        TargetSpeechLanguage = settings.TargetSpeechLanguage;

        _optimizeHotkey = settings.OptimizeHotkey;
        OptimizeHotkeyDisplayText = _optimizeHotkey.DisplayText;
        _assistantHotkey = settings.AssistantHotkey;
        AssistantHotkeyDisplayText = _assistantHotkey?.DisplayText ?? _localizationService["Msg_Settings_HotkeyNotSet"];
        _researchHotkey = settings.ResearchHotkey;
        ResearchHotkeyDisplayText = _researchHotkey?.DisplayText ?? _localizationService["Msg_Settings_HotkeyNotSet"];

        // Load TTS
        SelectedVoiceKey = settings.TtsVoiceModelKey;
        await LoadTtsVoicesAsync();

        _isLoading = false;
    }

    [RelayCommand]
    private async Task CaptureOptimizeHotkeyAsync()
    {
        var shortcut = await _dialogService.ShowHotkeyCaptureDialogAsync();
        if (shortcut != null && !HasInternalConflict(shortcut, WindowMode.Optimize))
        {
            _optimizeHotkey = shortcut;
            OptimizeHotkeyDisplayText = shortcut.DisplayText;
            await SaveSettingsAsync();
            _trayIconService.UpdateHotkey(WindowMode.Optimize, _optimizeHotkey);
        }
    }

    [RelayCommand]
    private async Task CaptureAssistantHotkeyAsync()
    {
        var shortcut = await _dialogService.ShowHotkeyCaptureDialogAsync();
        if (shortcut != null && !HasInternalConflict(shortcut, WindowMode.Assistant))
        {
            _assistantHotkey = shortcut;
            AssistantHotkeyDisplayText = shortcut.DisplayText;
            await SaveSettingsAsync();
            _trayIconService.UpdateHotkey(WindowMode.Assistant, _assistantHotkey);
        }
    }

    [RelayCommand]
    private async Task CaptureResearchHotkeyAsync()
    {
        var shortcut = await _dialogService.ShowHotkeyCaptureDialogAsync();
        if (shortcut != null && !HasInternalConflict(shortcut, WindowMode.Research))
        {
            _researchHotkey = shortcut;
            ResearchHotkeyDisplayText = shortcut.DisplayText;
            await SaveSettingsAsync();
            _trayIconService.UpdateHotkey(WindowMode.Research, _researchHotkey);
        }
    }

    [RelayCommand]
    private async Task ClearOptimizeHotkeyAsync()
    {
        _optimizeHotkey = KeyboardShortcut.DefaultCtrlAltO();
        OptimizeHotkeyDisplayText = _optimizeHotkey.DisplayText;
        await SaveSettingsAsync();
        _trayIconService.UpdateHotkey(WindowMode.Optimize, _optimizeHotkey);
    }

    [RelayCommand]
    private async Task ClearAssistantHotkeyAsync()
    {
        _assistantHotkey = KeyboardShortcut.DefaultCtrlAltP();
        AssistantHotkeyDisplayText = _localizationService["Msg_Settings_HotkeyNotSet"];
        await SaveSettingsAsync();
        _trayIconService.UpdateHotkey(WindowMode.Assistant, null);
    }

    [RelayCommand]
    private async Task ClearResearchHotkeyAsync()
    {
        _researchHotkey = KeyboardShortcut.DefaultCtrlAltR();
        ResearchHotkeyDisplayText = _localizationService["Msg_Settings_HotkeyNotSet"];
        await SaveSettingsAsync();
        _trayIconService.UpdateHotkey(WindowMode.Research, null);
    }

    private bool HasInternalConflict(KeyboardShortcut shortcut, WindowMode targetMode)
    {
        var allHotkeys = new Dictionary<WindowMode, KeyboardShortcut?>
        {
            { WindowMode.Optimize, _optimizeHotkey },
            { WindowMode.Assistant, _assistantHotkey },
            { WindowMode.Research, _researchHotkey }
        };

        foreach (var (mode, existing) in allHotkeys)
        {
            if (mode == targetMode || existing is null)
                continue;

            if (existing.Modifiers == shortcut.Modifiers && existing.VirtualKeyCode == shortcut.VirtualKeyCode)
            {
                _snackbarService.Show(_localizationService["Msg_Settings_Conflict"], _localizationService.Format("Msg_Settings_HotkeyAlreadyAssigned", mode), Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(3));
                return true;
            }
        }

        return false;
    }

    [RelayCommand]
    private async Task DownloadWhisperModelAsync()
    {
        var modelName = Services.TranscriptionService.GetModelName(WhisperModel);

        var downloadCancellationToken = new CancellationTokenSource();
        var progress = new Progress<ModelDownloadProgress>();

        try
        {
            var downloadTask = _transcriptionService.DownloadModelAsync(WhisperModel, progress, downloadCancellationToken.Token);
            var dialogTask = _dialogService.ShowModelDownloadDialogAsync(modelName, progress, downloadCancellationToken.Token);

            var completedTask = await Task.WhenAny(downloadTask, dialogTask);
            var wasCancelled = downloadCancellationToken.Token.IsCancellationRequested;

            if (wasCancelled)
            {
                _snackbarService.Show(_localizationService["Msg_Cancelled"], _localizationService["Msg_Settings_ModelDownloadCancelled"], Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(3));
            }
            else
            {
                await downloadTask;
                _snackbarService.Show(_localizationService["Msg_Success"], _localizationService["Msg_Settings_ModelDownloadCompleted"], Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
            }
        }
        catch (Exception ex)
        {
            _snackbarService.Show(_localizationService["Msg_Error"], _localizationService.Format("Msg_Settings_ModelDownloadFailed", ex.Message), Wpf.Ui.Controls.ControlAppearance.Danger, null, TimeSpan.FromSeconds(3));
        }
        finally
        {
            downloadCancellationToken?.Dispose();
        }
    }

    private async Task LoadTtsVoicesAsync()
    {
        var voices = await _ttsService.GetAvailableVoicesAsync();
        TtsVoices.Clear();
        foreach (var voice in voices)
        {
            voice.IsSelected = voice.Key == SelectedVoiceKey;
            TtsVoices.Add(voice);
        }
    }

    [RelayCommand]
    private async Task DownloadVoiceAsync(TtsVoice? voice)
    {
        if (voice is null || voice.IsDownloaded || voice.IsDownloading)
            return;

        voice.IsDownloading = true;
        voice.DownloadProgress = 0;

        try
        {
            var progress = new Progress<TtsDownloadProgress>(p =>
            {
                voice.DownloadProgress = p.PercentComplete;
            });

            await _ttsService.DownloadVoiceAsync(voice.Key, progress);
            voice.IsDownloaded = true;
            _snackbarService.Show(_localizationService["Msg_Success"], _localizationService.Format("Msg_Settings_VoiceDownloaded", voice.DisplayName), Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download voice {VoiceKey}", voice.Key);
            _snackbarService.Show(_localizationService["Msg_Error"], _localizationService.Format("Msg_Settings_VoiceDownloadFailed", ex.Message), Wpf.Ui.Controls.ControlAppearance.Danger, null, TimeSpan.FromSeconds(3));
        }
        finally
        {
            voice.IsDownloading = false;
        }
    }

    [RelayCommand]
    private async Task SelectVoiceAsync(TtsVoice? voice)
    {
        if (voice is null || !voice.IsDownloaded)
            return;

        foreach (var v in TtsVoices)
            v.IsSelected = false;

        voice.IsSelected = true;
        SelectedVoiceKey = voice.Key;

        try
        {
            await _ttsService.SetVoiceAsync(voice.Key);
            _snackbarService.Show(_localizationService["Msg_Settings_VoiceChanged"], _localizationService.Format("Msg_Settings_NowUsingVoice", voice.DisplayName), Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set voice {VoiceKey}", voice.Key);
            _snackbarService.Show(_localizationService["Msg_Error"], _localizationService.Format("Msg_Settings_VoiceSetFailed", ex.Message), Wpf.Ui.Controls.ControlAppearance.Danger, null, TimeSpan.FromSeconds(3));
        }
    }

    private async Task SaveSettingsAsync()
    {
        var settings = await _settingsService.GetSettingsAsync();
        settings.UiLanguage = UiLanguage;
        settings.StartMinimized = StartMinimized;
        settings.WhisperModel = WhisperModel;
        settings.TargetSpeechLanguage = TargetSpeechLanguage;
        settings.OptimizeHotkey = _optimizeHotkey;
        settings.AssistantHotkey = _assistantHotkey;
        settings.ResearchHotkey = _researchHotkey;
        await _settingsService.SaveSettingsAsync(settings);
    }

    private async void SafeFireAndForget(Task task)
    {
        try { await task; }
        catch (Exception ex) { _logger.LogError(ex, "Background operation failed"); }
    }
}
