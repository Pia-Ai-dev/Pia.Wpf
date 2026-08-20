using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Models;
using Pia.Paths;
using Pia.Services.Interfaces;
using Pia.ViewModels.Models;
using System.Collections.ObjectModel;
using System.IO;

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

    /// <summary>Bind IsEnabled to Policy[nameof(AppSettings.X)] to grey a control out while policy enforces it.</summary>
    public PolicyLock Policy { get; }
    private readonly ISyncClientService _syncClientService;
    private bool _isLoading;

    public PrivacySettingsViewModel PrivacyVm { get; }

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
        PrivacySettingsViewModel privacyVm,
        ISyncClientService syncClientService)
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
        Policy = new PolicyLock(policyService);
        _syncClientService = syncClientService;
        PrivacyVm = privacyVm;

        _uiLanguage = _localizationService.CurrentLanguage;
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

    [ObservableProperty]
    private bool _autoCaptureSelectedText;

    // Hotkeys
    [ObservableProperty]
    private string _optimizeHotkeyDisplayText = "Ctrl+Alt+O";

    [ObservableProperty]
    private string _assistantHotkeyDisplayText = "";

    [ObservableProperty]
    private string _fastPathHotkeyDisplayText = "";

    private KeyboardShortcut _optimizeHotkey = KeyboardShortcut.DefaultCtrlAltO();
    private KeyboardShortcut? _assistantHotkey = KeyboardShortcut.DefaultCtrlAltP();
    private KeyboardShortcut? _fastPathHotkey;

    // Speech
    [ObservableProperty]
    private SttBackend _sttBackend;

    [ObservableProperty]
    private WhisperModelSize _whisperModel;

    [ObservableProperty]
    private TargetSpeechLanguage _targetSpeechLanguage;

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

    partial void OnAutoCaptureSelectedTextChanged(bool value)
    {
        if (!_isLoading) SaveSettingsAsync().SafeFireAndForget(_logger);
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

    public async Task InitializeAsync()
    {
        _isLoading = true;

        var settings = await _settingsService.GetSettingsAsync();
        UiLanguage = _localizationService.CurrentLanguage;
        StartMinimized = settings.StartMinimized;
        LaunchAtStartup = settings.LaunchAtStartup;
        AutoCaptureSelectedText = settings.AutoCaptureSelectedText;
        SttBackend = settings.SttBackend;
        WhisperModel = settings.WhisperModel;
        TargetSpeechLanguage = settings.TargetSpeechLanguage;

        _optimizeHotkey = settings.OptimizeHotkey;
        OptimizeHotkeyDisplayText = _optimizeHotkey.DisplayText;
        _assistantHotkey = settings.AssistantHotkey;
        AssistantHotkeyDisplayText = _assistantHotkey?.DisplayText ?? _localizationService["Msg_Settings_HotkeyNotSet"];
        _fastPathHotkey = settings.FastPathHotkey;
        FastPathHotkeyDisplayText = _fastPathHotkey?.DisplayText ?? _localizationService["Msg_Settings_HotkeyNotSet"];

        // Load TTS
        SelectedVoiceKey = settings.TtsVoiceModelKey;
        await LoadTtsVoicesAsync();

        _isLoading = false;

        await PrivacyVm.InitializeAsync();
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
    private async Task CaptureFastPathHotkeyAsync()
    {
        var shortcut = await _dialogService.ShowHotkeyCaptureDialogAsync();
        if (shortcut != null && !HasInternalConflict(shortcut, "FastPath"))
        {
            _fastPathHotkey = shortcut;
            FastPathHotkeyDisplayText = shortcut.DisplayText;
            await SaveSettingsAsync();
            _trayIconService.UpdateFastPathHotkey(_fastPathHotkey);
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
        _assistantHotkey = null;
        AssistantHotkeyDisplayText = _localizationService["Msg_Settings_HotkeyNotSet"];
        await SaveSettingsAsync();
        _trayIconService.UpdateHotkey(WindowMode.Assistant, null);
    }


    [RelayCommand]
    private async Task ClearFastPathHotkeyAsync()
    {
        _fastPathHotkey = null;
        FastPathHotkeyDisplayText = _localizationService["Msg_Settings_HotkeyNotSet"];
        await SaveSettingsAsync();
        _trayIconService.UpdateFastPathHotkey(null);
    }

    private bool HasInternalConflict(KeyboardShortcut shortcut, WindowMode targetMode)
    {
        return HasInternalConflict(shortcut, targetMode.ToString());
    }

    private bool HasInternalConflict(KeyboardShortcut shortcut, string targetKey)
    {
        var allHotkeys = new Dictionary<string, KeyboardShortcut?>
        {
            { WindowMode.Optimize.ToString(), _optimizeHotkey },
            { WindowMode.Assistant.ToString(), _assistantHotkey },
            { "FastPath", _fastPathHotkey }
        };

        foreach (var (name, existing) in allHotkeys)
        {
            if (name == targetKey || existing is null)
                continue;

            if (existing.Modifiers == shortcut.Modifiers && existing.VirtualKeyCode == shortcut.VirtualKeyCode)
            {
                _snackbarService.Show(_localizationService["Msg_Settings_Conflict"], _localizationService.Format("Msg_Settings_HotkeyAlreadyAssigned", name), Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(3));
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

    // Reset app data
    [RelayCommand]
    private async Task ResetAppDataAsync()
    {
        var confirmed = await _dialogService.ShowConfirmationDialogAsync(
            _localizationService["Settings_ResetAppData_Confirm_Title"],
            _localizationService["Settings_ResetAppData_Confirm_Message"]);

        if (!confirmed)
            return;

        try
        {
            _syncClientService.StopBackgroundSync();

            var roamingDir = PiaPaths.RoamingDataDirectory;
            var localDir = PiaPaths.LocalDataDirectory;

            if (Directory.Exists(roamingDir))
                Directory.Delete(roamingDir, recursive: true);
            if (Directory.Exists(localDir))
                Directory.Delete(localDir, recursive: true);

            var exePath = Environment.ProcessPath;
            if (exePath is not null)
            {
                System.Diagnostics.Process.Start(exePath);
                Environment.Exit(0);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reset application data");
            _snackbarService.Show(
                _localizationService["Msg_Error"],
                ex.Message,
                Wpf.Ui.Controls.ControlAppearance.Danger,
                null,
                TimeSpan.FromSeconds(5));
        }
    }

    private async Task SaveSettingsAsync()
    {
        var settings = await _settingsService.GetSettingsAsync();
        settings.UiLanguage = UiLanguage;
        settings.StartMinimized = StartMinimized;
        settings.LaunchAtStartup = LaunchAtStartup;
        settings.AutoCaptureSelectedText = AutoCaptureSelectedText;
        settings.SttBackend = SttBackend;
        settings.WhisperModel = WhisperModel;
        settings.TargetSpeechLanguage = TargetSpeechLanguage;
        settings.OptimizeHotkey = _optimizeHotkey;
        settings.AssistantHotkey = _assistantHotkey;
        settings.FastPathHotkey = _fastPathHotkey;
        await _settingsService.SaveSettingsAsync(settings);
    }

}
