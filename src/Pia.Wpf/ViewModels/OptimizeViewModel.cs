using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Navigation;

namespace Pia.ViewModels;

public partial class OptimizeViewModel : ObservableObject, INavigationAware, IDisposable
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
    private readonly Wpf.Ui.ISnackbarService _snackbarService;
    private readonly SynchronizationContext _syncContext;
    private CancellationTokenSource? _debounceCts;
    private CancellationTokenSource? _optimizationCancellationToken;
    private bool _isInitialized;
    private Guid? _lastKnownDefaultTemplateId;

    private static readonly Dictionary<string, string[]> OptimizingMessages = new()
    {
        ["Business Email"] =
        [
            "Applying business tone...",
            "Converting to office speech...",
            "Adding corporate polish...",
            "Inserting professional jargon...",
            "Calibrating formality levels...",
            "Removing casual vibes...",
            "Translating to manager-speak...",
            "Adding appropriate regards...",
            "Ensuring inbox-worthiness...",
            "Polishing executive summary...",
            "Checking synergy levels...",
            "Optimizing action items...",
            "Aligning with best practices...",
            "Adding circle-back potential...",
            "Maximizing meeting avoidance...",
            "Ensuring email trail compliance...",
            "Buffing professional sheen...",
            "Removing accidental personality...",
            "Adding strategic ambiguity...",
            "Preparing for reply-all survival..."
        ],
        ["Community Article"] =
        [
            "Loading customer vibes...",
            "Adding meaningful characters...",
            "Sprinkling engagement magic...",
            "Optimizing scroll-worthiness...",
            "Charging community batteries...",
            "Infusing relatability...",
            "Calibrating authenticity meter...",
            "Adding human touch...",
            "Boosting shareability factor...",
            "Generating warm fuzzies...",
            "Ensuring comment-bait quality...",
            "Polishing storytelling hooks...",
            "Maximizing emoji potential...",
            "Adding conversation starters...",
            "Checking inclusivity levels...",
            "Tuning friendly frequency...",
            "Brewing connection juice...",
            "Amplifying community spirit...",
            "Loading appreciation tokens...",
            "Preparing for viral potential..."
        ],
        ["Message to Friend"] =
        [
            "Activating bestie mode...",
            "Translating to friend language...",
            "Adding inside joke placeholders...",
            "Removing unnecessary formality...",
            "Injecting chill vibes...",
            "Calibrating casualness...",
            "Loading emoji suggestions...",
            "Checking banter levels...",
            "Adding friendly chaos...",
            "Optimizing chat energy...",
            "Ensuring laugh potential...",
            "Sprinkling friendship dust...",
            "Removing awkward politeness...",
            "Adding genuine feels...",
            "Tuning support frequencies...",
            "Charging hangout potential...",
            "Maximizing reply speed...",
            "Adding random tangent support...",
            "Ensuring meme compatibility...",
            "Preparing for instant response..."
        ],
        ["Default"] =
        [
            "Processing your text...",
            "Working the magic...",
            "Analyzing content...",
            "Applying optimizations...",
            "Crunching the words...",
            "Enhancing your message...",
            "Polishing the prose...",
            "Refining the content...",
            "Adding finishing touches...",
            "Almost there...",
            "Making it shine...",
            "Perfecting the output...",
            "Fine-tuning results...",
            "Generating goodness...",
            "Crafting excellence...",
            "Brewing perfection...",
            "Loading awesomeness...",
            "Summoning creativity...",
            "Channeling inspiration...",
            "Preparing masterpiece..."
        ]
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
                    _snackbarService.Show("Cancelled", "Optimization was cancelled", Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(4));
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
                cancellationToken);

            OptimizedText = session.OptimizedText;
            IsComparisonView = true;
            dialogCancellation.Cancel();
        }
        catch (OperationCanceledException)
        {
            // Already handled by dialog cancellation
        }
        catch (Exception ex)
        {
            dialogCancellation.Cancel();
            _snackbarService.Show("Error", $"Optimization failed: {ex.Message}", Wpf.Ui.Controls.ControlAppearance.Danger, null, TimeSpan.FromSeconds(4));
        }
    }

    private string[] GetOptimizingMessages()
    {
        var templateName = SelectedTemplate?.Name ?? "Default";
        if (!OptimizingMessages.TryGetValue(templateName, out var messages))
        {
            messages = OptimizingMessages["Default"];
        }
        return messages;
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
        try
        {
            var settings = await _settingsService.GetSettingsAsync();

            switch (settings.DefaultOutputAction)
            {
                case OutputAction.CopyToClipboard:
                    await _outputService.CopyToClipboardAsync(OptimizedText);
                    break;
                case OutputAction.AutoType:
                    await _outputService.AutoTypeAsync(OptimizedText);
                    break;
                case OutputAction.PasteToPreviousWindow:
                    await _outputService.PasteToPreviousWindowAsync(OptimizedText);
                    break;
            }

            InputText = string.Empty;
            IsComparisonView = false;
            OptimizedText = string.Empty;
            ErrorMessage = null;
            OptimizeCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Output failed: {ex.Message}";
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
            ErrorMessage = "Copied to clipboard";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Copy failed: {ex.Message}";
        }
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        if (settings.DefaultTemplateId.HasValue &&
            settings.DefaultTemplateId != _lastKnownDefaultTemplateId)
        {
            _lastKnownDefaultTemplateId = settings.DefaultTemplateId;
            _syncContext.Post(_ =>
            {
                SelectedTemplateId = settings.DefaultTemplateId.Value;
                SafeFireAndForget(UpdateSelectedTemplateAsync());
            }, null);
        }
    }

    private void OnTemplatesChanged(object? sender, EventArgs e)
    {
        _syncContext.Post(_ =>
        {
            SafeFireAndForget(ExecuteLoadTemplates());
        }, null);
    }

    private void DebounceSaveDraft()
    {
        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;
        SafeFireAndForget(DebounceAsync(500, SaveDraftAsync, token));
    }

    private static async Task DebounceAsync(int delayMs, Func<Task> action, CancellationToken ct)
    {
        await Task.Delay(delayMs, ct);
        await action();
    }

    private async void SafeFireAndForget(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogError(ex, "Background operation failed"); }
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
            SafeFireAndForget(UpdateSelectedTemplateAsync());
        }

        if (e.PropertyName == nameof(SelectedLanguage))
        {
            SafeFireAndForget(SaveLanguageAsync());
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
            _templates.Clear();
            foreach (var template in templates)
            {
                _templates.Add(template);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load templates");
            _snackbarService.Show(
                "Error",
                "Failed to load templates. Please check your configuration.",
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
    }

    public async Task OnNavigatedToAsync(object? parameter)
    {
        // Only initialize once
        if (_isInitialized)
            return;

        await ExecuteLoadTemplates();
        if (_templates.Count > 0)
        {
            var settings = await _settingsService.GetSettingsAsync();
            _lastKnownDefaultTemplateId = settings.DefaultTemplateId;
            var templateId = settings.DefaultTemplateId ?? _templates[0].Id;
            SelectedTemplateId = templateId;
            await UpdateSelectedTemplateAsync();

            InputText = settings.DraftText ?? string.Empty;
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
