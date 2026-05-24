using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Models;
using Pia.Services.Exceptions;
using Pia.Services.Interfaces;
using Pia.Navigation;

namespace Pia.ViewModels;

public partial class OptimizeViewModel : ObservableObject, INavigationAware, IDisposable, IOptimizeFastPathHandle
{
    private bool _disposed;
    public event EventHandler? FocusInputRequested;

    [ObservableProperty]
    private bool _shouldFocusInput;

    private readonly ILogger<OptimizeViewModel> _logger;
    private readonly ITextOptimizationService _textOptimizationService;
    private readonly ITemplateService _templateService;
    private readonly ISettingsService _settingsService;
    private readonly IOutputService _outputService;
    private readonly IHistoryService _historyService;
    private readonly IVoiceInputService _voiceInputService;
    private readonly IProviderService _providerService;
    private readonly INavigationService _navigationService;
    private readonly IDialogService _dialogService;
    private readonly IWindowManagerService _windowManagerService;
    private readonly IWindowTrackingService _windowTrackingService;
    private readonly ILocalizationService _localizationService;
    private readonly Wpf.Ui.ISnackbarService _snackbarService;
    private readonly SynchronizationContext _syncContext;
    private CancellationTokenSource? _debounceCts;
    private CancellationTokenSource? _optimizationCancellationToken;
    private readonly TaskCompletionSource<bool> _readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _isInitialized;
    private Guid? _lastKnownDefaultTemplateId;

    private static readonly Dictionary<string, string> OptimizingMessageResourceKeys = new()
    {
        ["Business Email"] = "Optimizing_Messages_BusinessEmail",
        ["Community Article"] = "Optimizing_Messages_CommunityArticle",
        ["Message to Friend"] = "Optimizing_Messages_MessageToFriend",
        ["Default"] = "Optimizing_Messages_Default",
    };

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedTemplate))]
    private Guid _selectedTemplateId;

    public OptimizationTemplate? SelectedTemplate { get; private set; }

    [ObservableProperty]
    private bool _isOptimizing;

    [ObservableProperty]
    private bool _isComparisonView;

    [ObservableProperty]
    private string _optimizedText = string.Empty;

    [ObservableProperty]
    private string _selectedLanguage = "EN";

    public ObservableCollection<string> Languages { get; } = new ObservableCollection<string>(["EN", "DE", "FR"]);

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _showTemplatePrompt;

    [ObservableProperty]
    private string? _trackedWindowInfo;

    [ObservableProperty]
    private bool _showTrackedWindowIndicator;

    [ObservableProperty]
    private bool _showCopyButton = true;

    private ObservableCollection<OptimizationTemplate> _templates = new();

    public ObservableCollection<OptimizationTemplate> Templates => _templates;

    public IAsyncRelayCommand OptimizeCommand { get; }
    public IRelayCommand CancelOptimizationCommand { get; }
    public IAsyncRelayCommand ToggleRecordingCommand { get; }
    public IRelayCommand AcceptCommand { get; }
    public IRelayCommand RejectCommand { get; }
    public IAsyncRelayCommand CopyToClipboardCommand { get; }
    public IRelayCommand AdvancedModeCommand { get; }
    public IAsyncRelayCommand LoadTemplatesCommand { get; }
    public IRelayCommand ClearInputCommand { get; }
    public IRelayCommand<string> SendToModeCommand { get; }
    public IAsyncRelayCommand<IReadOnlyList<string>> HandleFilesDroppedCommand { get; }

    public Task ReadyAsync => _isInitialized ? Task.CompletedTask : _readyTcs.Task;

    public OptimizeViewModel(
        ILogger<OptimizeViewModel> logger,
        ITextOptimizationService textOptimizationService,
        ITemplateService templateService,
        ISettingsService settingsService,
        IOutputService outputService,
        IHistoryService historyService,
        IProviderService providerService,
        INavigationService navigationService,
        IDialogService dialogService,
        IWindowManagerService windowManagerService,
        IWindowTrackingService windowTrackingService,
        ILocalizationService localizationService,
        IVoiceInputService voiceInputService,
        Wpf.Ui.ISnackbarService snackbarService)
    {
        _logger = logger;
        _textOptimizationService = textOptimizationService;
        _templateService = templateService;
        _settingsService = settingsService;
        _outputService = outputService;
        _historyService = historyService;
        _providerService = providerService;
        _navigationService = navigationService;
        _dialogService = dialogService;
        _windowManagerService = windowManagerService;
        _windowTrackingService = windowTrackingService;
        _localizationService = localizationService;
        _voiceInputService = voiceInputService;
        _snackbarService = snackbarService;

        OptimizeCommand = new AsyncRelayCommand(ExecuteOptimize, CanExecuteOptimize);
        CancelOptimizationCommand = new RelayCommand(ExecuteCancelOptimization);
        ToggleRecordingCommand = new AsyncRelayCommand(ExecuteToggleRecording);
        AcceptCommand = new AsyncRelayCommand(ExecuteAcceptAsync);
        RejectCommand = new RelayCommand(ExecuteReject);
        CopyToClipboardCommand = new AsyncRelayCommand(ExecuteCopyToClipboard);
        AdvancedModeCommand = new RelayCommand(ExecuteAdvancedMode);
        LoadTemplatesCommand = new AsyncRelayCommand(ExecuteLoadTemplates);
        ClearInputCommand = new RelayCommand(ExecuteClearInput);
        SendToModeCommand = new RelayCommand<string>(ExecuteSendToMode);
        HandleFilesDroppedCommand = new AsyncRelayCommand<IReadOnlyList<string>>(ExecuteHandleFilesDropped);

        _syncContext = SynchronizationContext.Current ?? throw new InvalidOperationException("Must be created on UI thread");

        _settingsService.SettingsChanged += OnSettingsChanged;
        _templateService.TemplatesChanged += OnTemplatesChanged;

        PropertyChanged += OnPropertyChanged;
    }

    private async Task ExecuteOptimize()
    {
        _optimizationCancellationToken = new CancellationTokenSource();
        var dialogCancellationToken = new CancellationTokenSource();

        try
        {
            IsOptimizing = true;
            ErrorMessage = null;

            var messages = GetOptimizingMessages();

            var optimizationTask = RunOptimizationAsync(_optimizationCancellationToken.Token, dialogCancellationToken);
            var dialogTask = _dialogService.ShowOptimizingDialogAsync(messages, dialogCancellationToken.Token);

            var completedTask = await Task.WhenAny(optimizationTask, dialogTask);

            if (completedTask == dialogTask)
            {
                var dialogCompleted = await dialogTask;
                if (!dialogCompleted)
                {
                    _optimizationCancellationToken.Cancel();
                    _snackbarService.Show(_localizationService["Msg_Cancelled"], _localizationService["Msg_Optimize_Cancelled"], Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(4));
                }
            }
            else
            {
                await optimizationTask;
            }
        }
        finally
        {
            IsOptimizing = false;
            _optimizationCancellationToken?.Dispose();
            _optimizationCancellationToken = null;
            dialogCancellationToken.Dispose();
            OptimizeCommand.NotifyCanExecuteChanged();
        }
    }

    public async Task ShowOptimizingDialogAsync(CancellationToken cancellationToken)
    {
        await _dialogService.ShowOptimizingDialogAsync(GetOptimizingMessages(), cancellationToken);
    }

    public void PrepareForFastPath()
    {
        // Do NOT clear InputText - fast-path now respects the existing draft and surfaces
        // an "Insert anyway" snackbar via FastPathOptimizerService when input is non-empty.
        OptimizedText = string.Empty;
        IsComparisonView = false;
        ErrorMessage = null;
        OptimizeCommand.NotifyCanExecuteChanged();
    }

    public async Task<bool> RunFastPathOptimizeAsync(CancellationToken externalCt = default)
    {
        _optimizationCancellationToken = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        using var dialogCancellationToken = new CancellationTokenSource();

        try
        {
            IsOptimizing = true;
            ErrorMessage = null;
            IsComparisonView = false;
            OptimizedText = string.Empty;

            await RunOptimizationAsync(_optimizationCancellationToken.Token, dialogCancellationToken);
            return IsComparisonView;
        }
        finally
        {
            _optimizationCancellationToken?.Dispose();
            _optimizationCancellationToken = null;
            OptimizeCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task RunOptimizationAsync(CancellationToken cancellationToken, CancellationTokenSource dialogCancellation)
    {
        try
        {
            var provider = await _providerService.GetDefaultProviderForModeAsync(WindowMode.Optimize);
            var session = await _textOptimizationService.OptimizeTextAsync(
                InputText,
                SelectedTemplateId,
                provider?.Id,
                SelectedLanguage,
                nameof(WindowMode.Optimize),
                cancellationToken);

            _logger.LogDebug("Optimize: assigning OptimizedText length={Length}", session.OptimizedText.Length);
            OptimizedText = session.OptimizedText;
            await UpdateTrackedWindowInfoAsync();
            IsComparisonView = true;
            dialogCancellation.Cancel();
        }
        catch (OperationCanceledException)
        {
            // Already handled by dialog cancellation
        }
        catch (LlmTruncatedException)
        {
            dialogCancellation.Cancel();
            _snackbarService.Show(_localizationService["Msg_Warning"], _localizationService["Msg_Optimize_Truncated"], Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            dialogCancellation.Cancel();
            _snackbarService.Show(_localizationService["Msg_Error"], _localizationService.Format("Msg_Optimize_Failed", ex.Message), Wpf.Ui.Controls.ControlAppearance.Danger, null, TimeSpan.FromSeconds(4));
        }
    }

    private string[] GetOptimizingMessages()
    {
        var templateName = SelectedTemplate?.Name ?? "Default";
        if (!OptimizingMessageResourceKeys.TryGetValue(templateName, out var resourceKey))
        {
            resourceKey = OptimizingMessageResourceKeys["Default"];
        }
        var localized = _localizationService[resourceKey];
        return localized.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private bool CanExecuteOptimize()
    {
        return !string.IsNullOrWhiteSpace(InputText) && !IsOptimizing;
    }

    private void ExecuteCancelOptimization()
    {
        _optimizationCancellationToken?.Cancel();
    }

    private async Task ExecuteToggleRecording()
    {
        var transcription = await _voiceInputService.CaptureVoiceInputAsync();
        if (!string.IsNullOrWhiteSpace(transcription))
        {
            var voiceTagged = $"<voice>{transcription}</voice>";
            InputText = string.IsNullOrWhiteSpace(InputText)
                ? voiceTagged
                : $"{InputText.TrimEnd()} {voiceTagged}";
            OptimizeCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task ExecuteAcceptAsync()
    {
        await ExecuteAcceptCoreAsync(false);
    }

    public async Task<bool> RunFastPathAcceptAsync()
    {
        return await ExecuteAcceptCoreAsync(true);
    }

    private async Task<bool> ExecuteAcceptCoreAsync(bool preserveComparisonOnPasteFallback)
    {
        try
        {
            var settings = await _settingsService.GetSettingsAsync();
            var pasteFallbackFailed = false;

            switch (settings.DefaultOutputAction)
            {
                case OutputAction.CopyToClipboard:
                    await _outputService.CopyToClipboardAsync(OptimizedText);
                    break;
                case OutputAction.AutoType:
                    await _outputService.AutoTypeAsync(OptimizedText);
                    break;
                case OutputAction.PasteToPreviousWindow:
                    try
                    {
                        await _outputService.PasteToPreviousWindowAsync(OptimizedText);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Paste to previous window failed, falling back to clipboard");
                        await _outputService.CopyToClipboardAsync(OptimizedText);
                        pasteFallbackFailed = true;
                        var windowName = _windowTrackingService.GetTrackedWindowTitle() ?? _localizationService["Msg_Optimize_UnknownWindow"];
                        _snackbarService.Show(
                            _localizationService["Msg_Optimize_PasteFailed_Title"],
                            _localizationService.Format("Msg_Optimize_PasteFailed", windowName),
                            Wpf.Ui.Controls.ControlAppearance.Caution,
                            null,
                            TimeSpan.FromSeconds(5));
                    }
                    break;
            }

            if (pasteFallbackFailed && preserveComparisonOnPasteFallback)
                return false;

            InputText = string.Empty;
            IsComparisonView = false;
            OptimizedText = string.Empty;
            ErrorMessage = null;
            OptimizeCommand.NotifyCanExecuteChanged();
            return true;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Output failed: {ex.Message}";
            return false;
        }
    }

    public void ShowFastPathSnackbar(string messageKey)
    {
        _snackbarService.Show(
            _localizationService["Msg_Warning"],
            _localizationService[messageKey],
            Wpf.Ui.Controls.ControlAppearance.Caution,
            null,
            TimeSpan.FromSeconds(4));
    }

    public void ShowFastPathInsertAnywaySnackbar(string capturedText, Func<Task> onInsertAnyway)
    {
        SnackbarActionHelper.ShowWithAction(
            _snackbarService,
            _localizationService["Msg_Warning"],
            _localizationService["Msg_SelectionNotPastedInputNotEmpty"],
            _localizationService["Msg_SelectionNotPasted_InsertAnyway"],
            () =>
            {
                // Fire-and-forget; FastPathOptimizerService logs failures internally.
                onInsertAnyway().SafeFireAndForget(_logger);
            },
            Wpf.Ui.Controls.ControlAppearance.Caution,
            TimeSpan.FromSeconds(8));
    }

    private async Task UpdateTrackedWindowInfoAsync()
    {
        var settings = await _settingsService.GetSettingsAsync();

        switch (settings.DefaultOutputAction)
        {
            case OutputAction.PasteToPreviousWindow:
            case OutputAction.AutoType:
                if (_windowTrackingService.HasTrackedWindow)
                {
                    var title = _windowTrackingService.GetTrackedWindowTitle();
                    var process = _windowTrackingService.GetTrackedWindowProcessName();
                    TrackedWindowInfo = !string.IsNullOrEmpty(title) ? title : process ?? _localizationService["Msg_Optimize_UnknownWindow"];
                    ShowTrackedWindowIndicator = true;
                }
                else
                {
                    TrackedWindowInfo = null;
                    ShowTrackedWindowIndicator = false;
                }
                ShowCopyButton = true;
                break;
            case OutputAction.CopyToClipboard:
                TrackedWindowInfo = _localizationService["Msg_Optimize_SentToClipboard"];
                ShowTrackedWindowIndicator = true;
                ShowCopyButton = false;
                break;
            default:
                TrackedWindowInfo = null;
                ShowTrackedWindowIndicator = false;
                ShowCopyButton = true;
                break;
        }
    }

    private void ExecuteReject()
    {
        IsComparisonView = false;
        OptimizedText = string.Empty;
        ErrorMessage = null;
        OptimizeCommand.NotifyCanExecuteChanged();
    }

    private void ExecuteClearInput()
    {
        InputText = string.Empty;
        IsComparisonView = false;
        OptimizedText = string.Empty;
        ErrorMessage = null;
        ShouldFocusInput = true;
    }

    private void ExecuteSendToMode(string? modeString)
    {
        if (string.IsNullOrWhiteSpace(OptimizedText) || string.IsNullOrWhiteSpace(modeString))
            return;

        if (!Enum.TryParse<WindowMode>(modeString, out var mode))
            return;

        _windowManagerService.ShowWindowWithText(mode, OptimizedText);

        InputText = string.Empty;
        IsComparisonView = false;
        OptimizedText = string.Empty;
        ErrorMessage = null;
        OptimizeCommand.NotifyCanExecuteChanged();
    }

    private async Task ExecuteCopyToClipboard()
    {
        try
        {
            await _outputService.CopyToClipboardAsync(OptimizedText);
            ErrorMessage = _localizationService["Msg_Optimize_CopiedToClipboard"];
        }
        catch (Exception ex)
        {
            ErrorMessage = _localizationService.Format("Msg_Optimize_CopyFailed", ex.Message);
        }
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        if (settings.DefaultTemplateId.HasValue &&
            settings.DefaultTemplateId != _lastKnownDefaultTemplateId)
        {
            var previousKnownDefault = _lastKnownDefaultTemplateId;
            _lastKnownDefaultTemplateId = settings.DefaultTemplateId;

            var shouldFollowDefault =
                SelectedTemplateId == Guid.Empty ||
                (previousKnownDefault.HasValue && SelectedTemplateId == previousKnownDefault.Value);

            if (shouldFollowDefault)
            {
                _syncContext.Post(_ =>
                {
                    SelectedTemplateId = settings.DefaultTemplateId.Value;
                    UpdateSelectedTemplateAsync().SafeFireAndForget(_logger);
                }, null);
            }
        }
    }

    private void OnTemplatesChanged(object? sender, EventArgs e)
    {
        _syncContext.Post(_ =>
        {
            ExecuteLoadTemplates().SafeFireAndForget(_logger);
        }, null);
    }

    private void DebounceSaveDraft()
    {
        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;
        DebounceAsync(500, SaveDraftAsync, token).SafeFireAndForget(_logger);
    }

    private static async Task DebounceAsync(int delayMs, Func<Task> action, CancellationToken ct)
    {
        await Task.Delay(delayMs, ct);
        await action();
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InputText) || e.PropertyName == nameof(SelectedTemplateId))
        {
            DebounceSaveDraft();
            OptimizeCommand.NotifyCanExecuteChanged();
        }

        if (e.PropertyName == nameof(IsOptimizing))
        {
            OptimizeCommand.NotifyCanExecuteChanged();
        }

        if (e.PropertyName == nameof(SelectedTemplateId))
        {
            UpdateSelectedTemplateAsync().SafeFireAndForget(_logger);
        }

        if (e.PropertyName == nameof(SelectedLanguage))
        {
            SaveLanguageAsync().SafeFireAndForget(_logger);
        }
    }
    private async Task UpdateSelectedTemplateAsync()
    {
        SelectedTemplate = await _templateService.GetTemplateAsync(SelectedTemplateId);
    }

    private async Task SaveDraftAsync()
    {
        try
        {
            var settings = await _settingsService.GetSettingsAsync();
            settings.DraftText = InputText;
            await _settingsService.SaveSettingsAsync(settings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save draft text");
        }
    }

    private async Task SaveLanguageAsync()
    {
        try
        {
            var settings = await _settingsService.GetSettingsAsync();
            settings.TargetLanguage = Enum.Parse<Pia.Models.TargetLanguage>(SelectedLanguage);
            await _settingsService.SaveSettingsAsync(settings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save target language");
        }
    }

    private void ExecuteAdvancedMode()
    {
        _navigationService.NavigateTo<SettingsViewModel>();
    }

    private async Task ExecuteLoadTemplates()
    {
        try
        {
            var templates = await _templateService.GetTemplatesAsync();
            var previousSelection = SelectedTemplateId;

            _templates.Clear();
            foreach (var template in templates)
            {
                _templates.Add(template);
            }

            if (previousSelection != Guid.Empty &&
                _templates.Any(t => t.Id == previousSelection))
            {
                if (SelectedTemplateId != previousSelection)
                {
                    SelectedTemplateId = previousSelection;
                }
            }
            else if (_templates.Count > 0)
            {
                var settings = await _settingsService.GetSettingsAsync();
                SelectedTemplateId = settings.DefaultTemplateId
                    ?? _templates.FirstOrDefault(t => t.Id == Shared.BuiltInTemplates.ClarityAndGrammarId)?.Id
                    ?? _templates[0].Id;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load templates");
            _snackbarService.Show(
                _localizationService["Msg_Error"],
                _localizationService["Msg_Optimize_LoadTemplatesFailed"],
                Wpf.Ui.Controls.ControlAppearance.Danger,
                null,
                TimeSpan.FromSeconds(4));
        }
    }

    public void OnNavigatedTo(object? parameter)
    {
        if (parameter is bool shouldFocus && shouldFocus)
        {
            ShouldFocusInput = true;
        }
        else if (parameter is CapturedSelectionPayload selection)
        {
            ApplyCapturedSelection(selection.Text);
        }
    }

    private void ApplyCapturedSelection(string text)
    {
        if (string.IsNullOrEmpty(InputText))
        {
            InputText = text;
            ShouldFocusInput = true;
            return;
        }

        SnackbarActionHelper.ShowWithAction(
            _snackbarService,
            _localizationService["Msg_Warning"],
            _localizationService["Msg_SelectionNotPastedInputNotEmpty"],
            _localizationService["Msg_SelectionNotPasted_InsertAnyway"],
            () =>
            {
                InputText = text;
                ShouldFocusInput = true;
                OptimizeCommand.NotifyCanExecuteChanged();
            },
            Wpf.Ui.Controls.ControlAppearance.Caution,
            TimeSpan.FromSeconds(8));
    }

    private async Task ExecuteHandleFilesDropped(IReadOnlyList<string>? paths)
    {
        if (paths is null || paths.Count == 0) return;

        // Don't disrupt the user while they're reviewing an optimization result.
        if (IsComparisonView) return;

        var text = await DroppedFileImporter.TryImportAsync(
            paths, _logger, _snackbarService, _localizationService);
        if (text is not null)
            ApplyCapturedSelection(text);
    }

    public async Task OnNavigatedToAsync(object? parameter)
    {
        // Only initialize once
        if (_isInitialized)
        {
            _readyTcs.TrySetResult(true);
            return;
        }

        await ExecuteLoadTemplates();
        if (_templates.Count > 0)
        {
            var settings = await _settingsService.GetSettingsAsync();
            _lastKnownDefaultTemplateId = settings.DefaultTemplateId;
            var templateId = settings.DefaultTemplateId
                ?? _templates.FirstOrDefault(t => t.Id == Shared.BuiltInTemplates.ClarityAndGrammarId)?.Id
                ?? _templates[0].Id;
            SelectedTemplateId = templateId;
            await UpdateSelectedTemplateAsync();

            if (string.IsNullOrEmpty(InputText))
            {
                InputText = settings.DraftText ?? string.Empty;
            }
            if (!string.IsNullOrWhiteSpace(InputText))
            {
                OptimizeCommand.NotifyCanExecuteChanged();
            }

            var savedLanguage = settings.TargetLanguage?.ToString();
            if (!string.IsNullOrEmpty(savedLanguage))
            {
                SelectedLanguage = savedLanguage;
            }
            else
            {
                var osLangCode = CultureInfo.CurrentCulture.TwoLetterISOLanguageName.ToLower();
                var supportedLanguages = new[] { "en", "de", "fr" };
                var detectedLanguage = supportedLanguages.Contains(osLangCode)
                    ? osLangCode.ToUpper()
                    : "EN";
                SelectedLanguage = detectedLanguage;
            }
        }

        _isInitialized = true;
        _readyTcs.TrySetResult(true);
    }

    public void OnNavigatedFrom()
    {
    }

    public void RequestFocus()
    {
        ShouldFocusInput = false;
        FocusInputRequested?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // Unsubscribe from cross-window events
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _templateService.TemplatesChanged -= OnTemplatesChanged;

        // Cancel debounce timer
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();

        // Unsubscribe from own PropertyChanged event
        PropertyChanged -= OnPropertyChanged;

        // Dispose cancellation tokens
        _optimizationCancellationToken?.Dispose();

        GC.SuppressFinalize(this);
    }
}
