using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
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
    private readonly IResearchExportService _exportService;
    private readonly IResearchHistoryService _researchHistoryService;
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
        IResearchExportService exportService,
        IResearchHistoryService researchHistoryService,
        Wpf.Ui.ISnackbarService snackbarService,
        ILocalizationService localizationService,
        ILogger<ResearchViewModel> logger)
    {
        _researchService = researchService;
        _providerService = providerService;
        _outputService = outputService;
        _voiceInputService = voiceInputService;
        _exportService = exportService;
        _researchHistoryService = researchHistoryService;
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

            // Save completed session to history
            await SaveSessionToHistoryAsync(session, provider);
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
            var dialog = new SaveFileDialog
            {
                Title = _localizationService["Research_ExportAll"],
                FileName = $"Research_{CurrentSession.CreatedAt:yyyyMMdd_HHmmss}",
                Filter = "Markdown (*.md)|*.md|HTML (*.html)|*.html|PDF (*.xps)|*.xps",
                FilterIndex = 1,
                DefaultExt = ".md"
            };

            if (dialog.ShowDialog() != true)
                return;

            var filePath = dialog.FileName;
            var filterIndex = dialog.FilterIndex;

            switch (filterIndex)
            {
                case 1: // Markdown
                    await _exportService.ExportAsMarkdownAsync(CurrentSession, filePath);
                    break;
                case 2: // HTML
                    await _exportService.ExportAsHtmlAsync(CurrentSession, filePath);
                    break;
                case 3: // PDF/XPS
                    await _exportService.ExportAsPdfAsync(CurrentSession, filePath);
                    break;
            }

            _snackbarService.Show(
                _localizationService["Msg_Research_Exported"],
                _localizationService.Format("Msg_Research_ExportedToFile", filePath),
                Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
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

    private async Task SaveSessionToHistoryAsync(ResearchSession session, AiProvider provider)
    {
        try
        {
            var historyEntry = new ResearchHistoryEntry
            {
                Id = session.Id,
                Query = session.Query,
                SynthesizedResult = session.SynthesizedResult,
                StepsJson = JsonSerializer.Serialize(
                    session.Steps.Select(s => new ResearchStepDto
                    {
                        StepNumber = s.StepNumber,
                        Title = s.Title,
                        Content = s.Content,
                        Status = s.Status.ToString()
                    }).ToList()),
                ProviderId = provider.Id,
                ProviderName = provider.Name,
                Status = session.Status.ToString(),
                StepCount = session.Steps.Count,
                CreatedAt = session.CreatedAt,
                CompletedAt = session.CompletedAt ?? DateTime.Now
            };
            await _researchHistoryService.AddEntryAsync(historyEntry);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save research session to history");
        }
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
