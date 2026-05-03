using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Services.LiveTranscription;
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
    private readonly IAutostartService _autostartService;
    private readonly IPolicyService _policyService;
    private readonly IFileDialogService _fileDialogService;
    private bool _isLoading;

    public GeneralSettingsViewModel(
        ILogger<SettingsViewModel> logger,
        ISettingsService settingsService,
        ITranscriptionService transcriptionService,
        IDialogService dialogService,
        ITrayIconService trayIconService,
        ITtsService ttsService,
        Wpf.Ui.ISnackbarService snackbarService,
        ILocalizationService localizationService,
        IAutostartService autostartService,
        IPolicyService policyService,
        IFileDialogService fileDialogService)
    {
        _logger = logger;
        _settingsService = settingsService;
        _transcriptionService = transcriptionService;
        _dialogService = dialogService;
        _trayIconService = trayIconService;
        _ttsService = ttsService;
        _snackbarService = snackbarService;
        _localizationService = localizationService;
        _autostartService = autostartService;
        _policyService = policyService;
        _fileDialogService = fileDialogService;

        _uiLanguage = _localizationService.CurrentLanguage;
        _meetingTranscriptFolder = MeetingTranscriptPaths.DefaultMeetingFolder;
    }

    // Enterprise policy enforcement
    public bool IsUiLanguageEnforced => _policyService.IsEnforced(nameof(AppSettings.UiLanguage));
    public bool IsStartMinimizedEnforced => _policyService.IsEnforced(nameof(AppSettings.StartMinimized));
    public bool IsLaunchAtStartupEnforced => _policyService.IsEnforced(nameof(AppSettings.LaunchAtStartup));
    public bool IsSttBackendEnforced => _policyService.IsEnforced(nameof(AppSettings.SttBackend));
    public bool IsWhisperModelEnforced => _policyService.IsEnforced(nameof(AppSettings.WhisperModel));
    public bool IsTargetSpeechLanguageEnforced => _policyService.IsEnforced(nameof(AppSettings.TargetSpeechLanguage));
    public bool IsThemeEnforced => _policyService.IsEnforced(nameof(AppSettings.Theme));

    // Appearance
    [ObservableProperty]
    private TargetLanguage _uiLanguage;

    [ObservableProperty]
    private bool _startMinimized;

    [ObservableProperty]
    private bool _launchAtStartup;

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
    private SttBackend _sttBackend;

    [ObservableProperty]
    private WhisperModelSize _whisperModel;

    [ObservableProperty]
    private TargetSpeechLanguage _targetSpeechLanguage;

    // Meeting transcripts
    [ObservableProperty]
    private string _meetingTranscriptFolder = string.Empty;

    public string MeetingTranscriptFolderDefault => MeetingTranscriptPaths.DefaultMeetingFolder;

    public bool IsWhisperSelected => SttBackend == SttBackend.Whisper;
    public bool IsParakeetSelected => SttBackend == SttBackend.Parakeet;

    [ObservableProperty]
    private ObservableCollection<TtsVoice> _ttsVoices = new();

    [ObservableProperty]
    private string _selectedVoiceKey = "en_US-lessac-medium";

    // Inner tab index
    [ObservableProperty]
    private int _selectedInnerTabIndex;

    public IEnumerable<SttBackend> SttBackends => Enum.GetValues<SttBackend>();
    public IEnumerable<WhisperModelSize> WhisperModels => Enum.GetValues<WhisperModelSize>();
    public IEnumerable<TargetSpeechLanguage> TargetSpeechLanguages => Enum.GetValues<TargetSpeechLanguage>();
    public IEnumerable<TargetLanguage> UiLanguages => Enum.GetValues<TargetLanguage>();

    partial void OnUiLanguageChanged(TargetLanguage value)
    {
        if (!_isLoading)
        {
            _localizationService.SetLanguage(value);
            SaveSettingsAsync().SafeFireAndForget(_logger);
        }
    }

    partial void OnStartMinimizedChanged(bool value)
    {
        if (!_isLoading) SaveSettingsAsync().SafeFireAndForget(_logger);
    }

    partial void OnLaunchAtStartupChanged(bool value)
    {
        if (_isLoading) return;

        if (value)
            _autostartService.Enable();
        else
            _autostartService.Disable();

        SaveSettingsAsync().SafeFireAndForget(_logger);
    }

    partial void OnSttBackendChanged(SttBackend value)
    {
        OnPropertyChanged(nameof(IsWhisperSelected));
        OnPropertyChanged(nameof(IsParakeetSelected));
        if (!_isLoading) SaveSettingsAsync().SafeFireAndForget(_logger);
    }

    partial void OnWhisperModelChanged(WhisperModelSize value)
    {
        if (!_isLoading) SaveSettingsAsync().SafeFireAndForget(_logger);
    }

    partial void OnTargetSpeechLanguageChanged(TargetSpeechLanguage value)
    {
        if (!_isLoading) SaveSettingsAsync().SafeFireAndForget(_logger);
    }

    partial void OnMeetingTranscriptFolderChanged(string value)
    {
        if (!_isLoading) SaveSettingsAsync().SafeFireAndForget(_logger);
    }

    [RelayCommand]
    private void BrowseMeetingFolder()
    {
        var initial = string.IsNullOrWhiteSpace(MeetingTranscriptFolder)
            ? MeetingTranscriptPaths.DefaultMeetingFolder
            : MeetingTranscriptFolder;
        var picked = _fileDialogService.PromptSelectFolder(
            _localizationService["Settings_MeetingTranscriptFolder_Browse"],
            initial);
        if (!string.IsNullOrWhiteSpace(picked))
            MeetingTranscriptFolder = picked!;
    }

    [RelayCommand]
    private void ResetMeetingFolder()
    {
        MeetingTranscriptFolder = MeetingTranscriptPaths.DefaultMeetingFolder;
    }

    public async Task InitializeAsync()
    {
        _isLoading = true;

        var settings = await _settingsService.GetSettingsAsync();
        UiLanguage = _localizationService.CurrentLanguage;
        StartMinimized = settings.StartMinimized;
        LaunchAtStartup = settings.LaunchAtStartup;
        SttBackend = settings.SttBackend;
        WhisperModel = settings.WhisperModel;
        TargetSpeechLanguage = settings.TargetSpeechLanguage;
        MeetingTranscriptFolder = string.IsNullOrWhiteSpace(settings.MeetingTranscriptFolder)
            ? MeetingTranscriptPaths.DefaultMeetingFolder
            : settings.MeetingTranscriptFolder!;

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
        await DownloadModelInternalAsync(
            modelName,
            (progress, ct) => _transcriptionService.DownloadModelAsync(WhisperModel, progress, ct));
    }

    [RelayCommand]
    private async Task DownloadParakeetModelAsync()
    {
        await DownloadModelInternalAsync(
            _localizationService["Settings_Parakeet_DisplayName"],
            (progress, ct) => _transcriptionService.DownloadParakeetModelAsync(progress, ct));
    }

    private async Task DownloadModelInternalAsync(
        string modelDisplayName,
        Func<IProgress<ModelDownloadProgress>, CancellationToken, Task> downloadFn)
    {
        // Two CTS: userCancelCts fires when the user clicks Cancel; dialogCloseCts is what
        // the dialog watches and we cancel it ourselves when the download finishes (success
        // or error) so the dialog auto-dismisses.
        var userCancelCts = new CancellationTokenSource();
        var dialogCloseCts = CancellationTokenSource.CreateLinkedTokenSource(userCancelCts.Token);
        var progress = new Progress<ModelDownloadProgress>();

        try
        {
            var downloadTask = downloadFn(progress, userCancelCts.Token);
            var dialogTask = _dialogService.ShowModelDownloadDialogAsync(modelDisplayName, progress, dialogCloseCts.Token);

            try
            {
                await downloadTask.ConfigureAwait(true);
                _snackbarService.Show(_localizationService["Msg_Success"], _localizationService.Format("Msg_Settings_ModelDownloadCompleted", modelDisplayName), Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
            }
            catch (OperationCanceledException) when (userCancelCts.IsCancellationRequested)
            {
                _snackbarService.Show(_localizationService["Msg_Cancelled"], _localizationService["Msg_Settings_ModelDownloadCancelled"], Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(3));
            }
            finally
            {
                // Always dismiss the dialog when the download future completes.
                dialogCloseCts.Cancel();
                try { await dialogTask.ConfigureAwait(true); } catch { /* dialog already hidden */ }
            }
        }
        catch (Exception ex)
        {
            _snackbarService.Show(_localizationService["Msg_Error"], _localizationService.Format("Msg_Settings_ModelDownloadFailed", ex.Message), Wpf.Ui.Controls.ControlAppearance.Danger, null, TimeSpan.FromSeconds(3));
        }
        finally
        {
            dialogCloseCts.Dispose();
            userCancelCts.Dispose();
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
        settings.LaunchAtStartup = LaunchAtStartup;
        settings.SttBackend = SttBackend;
        settings.WhisperModel = WhisperModel;
        settings.TargetSpeechLanguage = TargetSpeechLanguage;
        settings.OptimizeHotkey = _optimizeHotkey;
        settings.AssistantHotkey = _assistantHotkey;
        settings.ResearchHotkey = _researchHotkey;
        // Empty / whitespace / "matches default" all collapse to null so the JSON stays clean
        // and the resolver picks the default automatically.
        settings.MeetingTranscriptFolder =
            string.IsNullOrWhiteSpace(MeetingTranscriptFolder) ||
            string.Equals(MeetingTranscriptFolder, MeetingTranscriptPaths.DefaultMeetingFolder, StringComparison.OrdinalIgnoreCase)
                ? null
                : MeetingTranscriptFolder;
        await _settingsService.SaveSettingsAsync(settings);
    }

}
