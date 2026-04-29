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

    [ObservableProperty]
    private ResearchAnswerLength _selectedAnswerLength = ResearchAnswerLength.Balanced;

    public IReadOnlyList<ResearchAnswerLength> AnswerLengths { get; } =
        [ResearchAnswerLength.Concise, ResearchAnswerLength.Balanced, ResearchAnswerLength.Detailed];

    public IAsyncRelayCommand StartResearchCommand { get; }
    public IAsyncRelayCommand ToggleRecordingCommand { get; }
    public IRelayCommand CancelResearchCommand { get; }
    public IAsyncRelayCommand<string> CopyResultCommand { get; }
    public IAsyncRelayCommand<string> ExportResultCommand { get; }
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
        CopyResultCommand = new AsyncRelayCommand<string>(ExecuteCopyResult);
        ExportResultCommand = new AsyncRelayCommand<string>(ExecuteExportResult);
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
            await _researchService.ExecuteResearchAsync(session, provider, SelectedAnswerLength, _researchCts.Token);

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

    private async Task ExecuteCopyResult(string? scope)
    {
        if (CurrentSession is null)
            return;

        var isFull = string.Equals(scope, "full", StringComparison.OrdinalIgnoreCase);
        var content = isFull
            ? _exportService.BuildMarkdown(CurrentSession)
            : CurrentSession.SynthesizedResult;

        if (string.IsNullOrEmpty(content))
            return;

        try
        {
            await _outputService.CopyToClipboardAsync(content);
            _snackbarService.Show(_localizationService["Msg_Research_Copied"], _localizationService["Msg_Research_ResultCopied"], Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to copy result");
        }
    }

    private async Task ExecuteExportResult(string? scope)
    {
        if (CurrentSession is null)
            return;

        var isFull = string.Equals(scope, "full", StringComparison.OrdinalIgnoreCase);

        try
        {
            var dialog = new SaveFileDialog
            {
                Title = _localizationService["Research_ExportAll"],
                FileName = $"Research_{CurrentSession.CreatedAt:yyyyMMdd_HHmmss}{(isFull ? "" : "_summary")}",
                Filter = "Markdown (*.md)|*.md|HTML (*.html)|*.html",
                FilterIndex = 1,
                DefaultExt = ".md"
            };

            if (dialog.ShowDialog() != true)
                return;

            var filePath = dialog.FileName;
            var filterIndex = dialog.FilterIndex;

            var sessionToExport = isFull ? CurrentSession : BuildSummaryOnlySession(CurrentSession);

            switch (filterIndex)
            {
                case 1: // Markdown
                    await _exportService.ExportAsMarkdownAsync(sessionToExport, filePath);
                    break;
                case 2: // HTML
                    await _exportService.ExportAsHtmlAsync(sessionToExport, filePath);
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

    private static ResearchSession BuildSummaryOnlySession(ResearchSession source)
    {
        var summary = new ResearchSession(source.Query)
        {
            SynthesizedResult = source.SynthesizedResult,
            Status = source.Status,
            CompletedAt = source.CompletedAt
        };
        var lastStep = source.Steps.LastOrDefault();
        if (lastStep is not null && !string.IsNullOrWhiteSpace(lastStep.Content))
        {
            summary.Steps.Add(new ResearchStep(1, lastStep.Title)
            {
                Content = lastStep.Content,
                Status = lastStep.Status,
                StartedAt = lastStep.StartedAt,
                CompletedAt = lastStep.CompletedAt
            });
        }
        return summary;
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
