using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Pia.Models;
using Pia.Navigation;
using Pia.Services.Interfaces;

namespace Pia.ViewModels;

public partial class ResearchHistoryViewModel : ObservableObject, IDisposable, INavigationAware
{
    private readonly ILogger<ResearchHistoryViewModel> _logger;
    private readonly IResearchHistoryService _researchHistoryService;
    private readonly IResearchExportService _exportService;
    private readonly IOutputService _outputService;
    private readonly IDialogService _dialogService;
    private readonly ILocalizationService _localizationService;
    private readonly Wpf.Ui.ISnackbarService _snackbarService;
    private readonly SynchronizationContext _syncContext;
    private CancellationTokenSource? _debounceCts;
    private int _currentOffset;
    private bool _disposed;

    [ObservableProperty]
    private ObservableCollection<ResearchHistoryEntry> _entries = new();

    [ObservableProperty]
    private DateTime? _filterStartDate;

    [ObservableProperty]
    private DateTime? _filterEndDate;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private ResearchHistoryEntry? _selectedEntry;

    [ObservableProperty]
    private bool _isDetailOpen;

    [ObservableProperty]
    private ResearchHistoryEntry? _detailEntry;

    [ObservableProperty]
    private ObservableCollection<ResearchStepDto> _detailSteps = new();

    public IAsyncRelayCommand ViewDetailCommand { get; }
    public IAsyncRelayCommand<string> CopyResultCommand { get; }
    public IAsyncRelayCommand<string> ExportEntryCommand { get; }
    public IAsyncRelayCommand DeleteEntryCommand { get; }
    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand ClearFilterCommand { get; }
    public IAsyncRelayCommand LoadMoreCommand { get; }
    public IRelayCommand CloseDetailCommand { get; }

    public ResearchHistoryViewModel(
        ILogger<ResearchHistoryViewModel> logger,
        IResearchHistoryService researchHistoryService,
        IResearchExportService exportService,
        IOutputService outputService,
        IDialogService dialogService,
        ILocalizationService localizationService,
        Wpf.Ui.ISnackbarService snackbarService)
    {
        _logger = logger;
        _researchHistoryService = researchHistoryService;
        _exportService = exportService;
        _outputService = outputService;
        _dialogService = dialogService;
        _localizationService = localizationService;
        _snackbarService = snackbarService;
        _syncContext = SynchronizationContext.Current ?? throw new InvalidOperationException("Must be created on UI thread");

        ViewDetailCommand = new AsyncRelayCommand(ExecuteViewDetailAsync, CanExecuteAction);
        CopyResultCommand = new AsyncRelayCommand<string>(ExecuteCopyResult, _ => CanExecuteAction());
        ExportEntryCommand = new AsyncRelayCommand<string>(ExecuteExportEntry, _ => CanExecuteAction());
        DeleteEntryCommand = new AsyncRelayCommand(ExecuteDeleteEntry, CanExecuteAction);
        RefreshCommand = new AsyncRelayCommand(ExecuteRefreshAsync);
        ClearFilterCommand = new AsyncRelayCommand(ExecuteClearFilterAsync);
        LoadMoreCommand = new AsyncRelayCommand(ExecuteLoadMore, CanLoadMore);
        CloseDetailCommand = new RelayCommand(ExecuteCloseDetail);

        PropertyChanged += OnPropertyChanged;
        _researchHistoryService.SessionsChanged += OnSessionsChanged;
    }

    public void OnNavigatedTo(object? parameter) { }

    public async Task OnNavigatedToAsync(object? parameter)
    {
        try
        {
            if (Entries.Count == 0)
            {
                FilterStartDate = DateTime.Today.AddDays(-30);
                FilterEndDate = DateTime.Today;

                await LoadEntriesAsync(0, 50);
            }

            if (parameter is Guid entryId && entryId != Guid.Empty)
            {
                await SelectEntryByIdAsync(entryId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize ResearchHistoryViewModel");
        }
    }

    private async Task SelectEntryByIdAsync(Guid entryId)
    {
        var existing = Entries.FirstOrDefault(e => e.Id == entryId);
        if (existing is not null)
        {
            SelectedEntry = existing;
            return;
        }

        // Entry isn't in the currently filtered/paged view (e.g. the toast was
        // clicked after the user narrowed filters). Fetch it directly and
        // surface it at the top of the list so it's visible and selectable.
        var fetched = await _researchHistoryService.GetEntryAsync(entryId);
        if (fetched is null)
        {
            _logger.LogWarning("Research history entry {Id} not found for selection", entryId);
            return;
        }

        Entries.Insert(0, fetched);
        SelectedEntry = fetched;
    }

    public void OnNavigatedFrom() { }

    private async Task LoadEntriesAsync(int offset, int take)
    {
        try
        {
            IsLoading = true;

            var entries = await _researchHistoryService.SearchEntriesAsync(
                searchText: SearchQuery,
                fromDate: FilterStartDate,
                toDate: FilterEndDate,
                offset: offset,
                limit: take);

            if (offset == 0)
            {
                Entries.Clear();
            }

            foreach (var entry in entries)
            {
                Entries.Add(entry);
            }

            _currentOffset = offset + entries.Count;

            TotalCount = await _researchHistoryService.GetEntryCountAsync(
                searchText: SearchQuery,
                fromDate: FilterStartDate,
                toDate: FilterEndDate);

            UpdateCommandStates();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load research entries (offset: {Offset}, take: {Take})", offset, take);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task ExecuteViewDetailAsync()
    {
        if (SelectedEntry is null)
            return;

        DetailEntry = SelectedEntry;
        DetailSteps.Clear();

        try
        {
            var steps = JsonSerializer.Deserialize<List<ResearchStepDto>>(SelectedEntry.StepsJson);
            if (steps is not null)
            {
                foreach (var step in steps)
                {
                    DetailSteps.Add(step);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse steps JSON");
        }

        IsDetailOpen = true;
    }

    private void ExecuteCloseDetail()
    {
        IsDetailOpen = false;
        DetailEntry = null;
        DetailSteps.Clear();
    }

    private async Task ExecuteCopyResult(string? scope)
    {
        if (SelectedEntry is null)
            return;

        var isFull = string.Equals(scope, "full", StringComparison.OrdinalIgnoreCase);
        var content = isFull
            ? _exportService.BuildMarkdown(ReconstructSession(SelectedEntry))
            : SelectedEntry.SynthesizedResult;

        if (string.IsNullOrEmpty(content))
            return;

        try
        {
            await _outputService.CopyToClipboardAsync(content);
            _snackbarService.Show(
                _localizationService["Msg_Research_Copied"],
                _localizationService["Msg_Research_ResultCopied"],
                Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to copy result to clipboard");
        }
    }

    private async Task ExecuteExportEntry(string? scope)
    {
        if (SelectedEntry is null)
            return;

        var isFull = string.Equals(scope, "full", StringComparison.OrdinalIgnoreCase);

        try
        {
            var session = ReconstructSession(SelectedEntry);
            var sessionToExport = isFull ? session : BuildSummaryOnlySession(session);

            var dialog = new SaveFileDialog
            {
                Title = _localizationService["ResearchHistory_Export"],
                FileName = $"Research_{SelectedEntry.CreatedAt:yyyyMMdd_HHmmss}{(isFull ? "" : "_summary")}",
                Filter = "Markdown (*.md)|*.md|HTML (*.html)|*.html",
                FilterIndex = 1,
                DefaultExt = ".md"
            };

            if (dialog.ShowDialog() != true)
                return;

            var filePath = dialog.FileName;

            switch (dialog.FilterIndex)
            {
                case 1:
                    await _exportService.ExportAsMarkdownAsync(sessionToExport, filePath);
                    break;
                case 2:
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
            _logger.LogError(ex, "Failed to export research entry");
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

    private async Task ExecuteDeleteEntry()
    {
        if (SelectedEntry is null)
            return;

        var confirmed = await _dialogService.ShowConfirmationDialogAsync(
            _localizationService["Msg_ResearchHistory_ConfirmDeleteTitle"],
            _localizationService["Msg_ResearchHistory_ConfirmDeleteMessage"]);

        if (!confirmed)
            return;

        var entry = SelectedEntry;

        try
        {
            await _researchHistoryService.DeleteEntryAsync(entry.Id);
            Entries.Remove(entry);
            SelectedEntry = null;

            if (IsDetailOpen && DetailEntry?.Id == entry.Id)
            {
                ExecuteCloseDetail();
            }

            TotalCount = await _researchHistoryService.GetEntryCountAsync(
                searchText: SearchQuery,
                fromDate: FilterStartDate,
                toDate: FilterEndDate);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete research entry {EntryId}", entry.Id);
            await _dialogService.ShowMessageDialogAsync(
                _localizationService["Msg_Error"],
                _localizationService.Format("Msg_ResearchHistory_DeleteFailed", ex.Message));
        }
    }

    private async Task ExecuteRefreshAsync()
    {
        await LoadEntriesAsync(0, 50);
    }

    private async Task ExecuteClearFilterAsync()
    {
        FilterStartDate = null;
        FilterEndDate = null;
        SearchQuery = string.Empty;
        await LoadEntriesAsync(0, 50);
    }

    private async Task ExecuteLoadMore()
    {
        await LoadEntriesAsync(_currentOffset, 50);
    }

    private bool CanExecuteAction() => SelectedEntry is not null && !IsLoading;

    private bool CanLoadMore() => !IsLoading && Entries.Count < TotalCount;

    private void UpdateCommandStates()
    {
        ViewDetailCommand.NotifyCanExecuteChanged();
        CopyResultCommand.NotifyCanExecuteChanged();
        ExportEntryCommand.NotifyCanExecuteChanged();
        DeleteEntryCommand.NotifyCanExecuteChanged();
        LoadMoreCommand.NotifyCanExecuteChanged();
    }

    private void DebounceSearch()
    {
        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;
        _ = RunSafelyAsync(DebounceAsync(500, () => LoadEntriesAsync(0, 50), token));
    }

    private static async Task DebounceAsync(int delayMs, Func<Task> action, CancellationToken ct)
    {
        await Task.Delay(delayMs, ct);
        await action();
    }

    private async Task RunSafelyAsync(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogError(ex, "Background operation failed"); }
    }

    private void OnSessionsChanged(object? sender, EventArgs e)
    {
        _syncContext.Post(_ => _ = RunSafelyAsync(LoadEntriesAsync(0, 50)), null);
    }

    private void OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SelectedEntry))
        {
            UpdateCommandStates();
        }

        if (e.PropertyName == nameof(SearchQuery))
        {
            DebounceSearch();
        }

        if (e.PropertyName is nameof(FilterStartDate) or nameof(FilterEndDate))
        {
            DebounceSearch();
        }

        if (e.PropertyName == nameof(IsLoading))
        {
            UpdateCommandStates();
        }
    }

    private static ResearchSession ReconstructSession(ResearchHistoryEntry entry)
    {
        var session = new ResearchSession(entry.Query);

        try
        {
            var steps = JsonSerializer.Deserialize<List<ResearchStepDto>>(entry.StepsJson);
            if (steps is not null)
            {
                foreach (var stepDto in steps)
                {
                    var step = new ResearchStep(stepDto.StepNumber, stepDto.Title)
                    {
                        Content = stepDto.Content,
                        Status = Enum.TryParse<ResearchStatus>(stepDto.Status, out var status)
                            ? status
                            : ResearchStatus.Completed
                    };
                    session.Steps.Add(step);
                }
            }
        }
        catch
        {
            // If deserialization fails, return session with no steps
        }

        session.SynthesizedResult = entry.SynthesizedResult;
        session.Status = Enum.TryParse<ResearchStatus>(entry.Status, out var s)
            ? s
            : ResearchStatus.Completed;

        return session;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _debounceCts?.Cancel();
        _debounceCts?.Dispose();

        _researchHistoryService.SessionsChanged -= OnSessionsChanged;
        PropertyChanged -= OnPropertyChanged;

        GC.SuppressFinalize(this);
    }
}
