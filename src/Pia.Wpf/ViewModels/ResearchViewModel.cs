using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Navigation;
using Pia.Services.Interfaces;

namespace Pia.ViewModels;

public partial class ResearchViewModel : ObservableObject, INavigationAware, IDisposable
{
    private readonly IResearchService _researchService;
    private readonly IProviderService _providerService;
    private readonly IOutputService _outputService;
    private readonly IVoiceInputService _voiceInputService;
    private readonly Wpf.Ui.ISnackbarService _snackbarService;
    private readonly ILocalizationService _localizationService;
    private readonly ILogger<ResearchViewModel> _logger;
    private CancellationTokenSource? _researchCts;
    private bool _disposed;

    [ObservableProperty]
    private string _queryText = string.Empty;

    [ObservableProperty]
    private ResearchSession? _currentSession;

    [ObservableProperty]
    private bool _isResearching;

    [ObservableProperty]
    private string? _errorMessage;

    public IAsyncRelayCommand StartResearchCommand { get; }
    public IAsyncRelayCommand ToggleRecordingCommand { get; }
    public IRelayCommand CancelResearchCommand { get; }
    public IAsyncRelayCommand CopyResultCommand { get; }
    public IAsyncRelayCommand ExportResultCommand { get; }
    public IRelayCommand NewResearchCommand { get; }

    public ResearchViewModel(
        IResearchService researchService,
        IProviderService providerService,
        IOutputService outputService,
        IVoiceInputService voiceInputService,
        Wpf.Ui.ISnackbarService snackbarService,
        ILocalizationService localizationService,
        ILogger<ResearchViewModel> logger)
    {
        _researchService = researchService;
        _providerService = providerService;
        _outputService = outputService;
        _voiceInputService = voiceInputService;
        _snackbarService = snackbarService;
        _localizationService = localizationService;
        _logger = logger;

        StartResearchCommand = new AsyncRelayCommand(ExecuteStartResearch, CanExecuteStartResearch);
        ToggleRecordingCommand = new AsyncRelayCommand(ExecuteToggleRecording);
        CancelResearchCommand = new RelayCommand(ExecuteCancelResearch);
        CopyResultCommand = new AsyncRelayCommand(ExecuteCopyResult);
        ExportResultCommand = new AsyncRelayCommand(ExecuteExportResult);
        NewResearchCommand = new RelayCommand(ExecuteNewResearch);

        PropertyChanged += OnPropertyChanged;
    }

    private void OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(QueryText) or nameof(IsResearching))
        {
            StartResearchCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanExecuteStartResearch() =>
        !IsResearching && !string.IsNullOrWhiteSpace(QueryText);

    private async Task ExecuteStartResearch()
    {
        var query = QueryText.Trim();
        ErrorMessage = null;

        var provider = await _providerService.GetDefaultProviderForModeAsync(WindowMode.Research);
        if (provider is null)
        {
            _snackbarService.Show(_localizationService["Msg_Error"], _localizationService["Msg_Research_NoProviderConfigured"], Wpf.Ui.Controls.ControlAppearance.Danger, null, TimeSpan.FromSeconds(4));
            return;
        }

        var session = new ResearchSession(query);
        CurrentSession = session;

        _researchCts = new CancellationTokenSource();
        IsResearching = true;

        try
        {
            await _researchService.ExecuteResearchAsync(session, provider, _researchCts.Token);
        }
        catch (OperationCanceledException)
        {
            _snackbarService.Show(_localizationService["Msg_Cancelled"], _localizationService["Msg_Research_Cancelled"], Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(4));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Research failed");
            ErrorMessage = ex.Message;
            _snackbarService.Show(_localizationService["Msg_Error"], _localizationService.Format("Msg_Research_Failed", ex.Message), Wpf.Ui.Controls.ControlAppearance.Danger, null, TimeSpan.FromSeconds(4));
        }
        finally
        {
            IsResearching = false;
            _researchCts?.Dispose();
            _researchCts = null;
        }
    }

    private async Task ExecuteToggleRecording()
    {
        var transcription = await _voiceInputService.CaptureVoiceInputAsync();
        if (!string.IsNullOrWhiteSpace(transcription))
        {
            QueryText = string.IsNullOrWhiteSpace(QueryText)
                ? transcription
                : $"{QueryText.TrimEnd()} {transcription}";
            StartResearchCommand.NotifyCanExecuteChanged();
        }
    }

    private void ExecuteCancelResearch()
    {
        _researchCts?.Cancel();
    }

    private async Task ExecuteCopyResult()
    {
        if (CurrentSession is null || string.IsNullOrEmpty(CurrentSession.SynthesizedResult))
            return;

        try
        {
            await _outputService.CopyToClipboardAsync(CurrentSession.SynthesizedResult);
            _snackbarService.Show(_localizationService["Msg_Research_Copied"], _localizationService["Msg_Research_ResultCopied"], Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to copy result");
        }
    }

    private async Task ExecuteExportResult()
    {
        if (CurrentSession is null)
            return;

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# Research: {CurrentSession.Query}");
            sb.AppendLine();

            foreach (var step in CurrentSession.Steps)
            {
                sb.AppendLine($"## Step {step.StepNumber}: {step.Title}");
                sb.AppendLine();
                sb.AppendLine(step.Content);
                sb.AppendLine();
            }

            await _outputService.CopyToClipboardAsync(sb.ToString());
            _snackbarService.Show(_localizationService["Msg_Research_Exported"], _localizationService["Msg_Research_FullResearchExported"], Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export result");
        }
    }

    private void ExecuteNewResearch()
    {
        _researchCts?.Cancel();
        CurrentSession = null;
        QueryText = string.Empty;
        ErrorMessage = null;
        IsResearching = false;
    }

    public void OnNavigatedTo(object? parameter)
    {
        if (parameter is string text && !string.IsNullOrWhiteSpace(text))
        {
            QueryText = text;
        }
    }

    public void OnNavigatedFrom() { }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        PropertyChanged -= OnPropertyChanged;
        _researchCts?.Cancel();
        _researchCts?.Dispose();

        GC.SuppressFinalize(this);
    }
}
