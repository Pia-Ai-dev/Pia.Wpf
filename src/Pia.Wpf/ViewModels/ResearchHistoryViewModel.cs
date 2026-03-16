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
    public IAsyncRelayCommand CopyResultCommand { get; }
    public IAsyncRelayCommand ExportEntryCommand { get; }
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
        ILocalizationService localizationService)
    {
        _logger = logger;
        _researchHistoryService = researchHistoryService;
        _exportService = exportService;
        _outputService = outputService;
        _dialogService = dialogService;
        _localizationService = localizationService;

        ViewDetailCommand = new AsyncRelayCommand(ExecuteViewDetailAsync, CanExecuteAction);
        CopyResultCommand = new AsyncRelayCommand(ExecuteCopyResult, CanExecuteAction);
        ExportEntryCommand = new AsyncRelayCommand(ExecuteExportEntry, CanExecuteAction);
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
            if (Entries.Count > 0)
                return;

            FilterStartDate = DateTime.Today.AddDays(-30);
            FilterEndDate = DateTime.Today;

            await LoadEntriesAsync(0, 50);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize ResearchHistoryViewModel");
        }
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

    private async Task ExecuteCopyResult()
    {
        if (SelectedEntry is null)
            return;

        try
        {
            await _outputService.CopyToClipboardAsync(SelectedEntry.SynthesizedResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to copy result to clipboard");
        }
    }

    private async Task ExecuteExportEntry()
    {
        if (SelectedEntry is null)
            return;

        try
        {
            // Reconstruct a ResearchSession from the history entry for export
            var session = ReconstructSession(SelectedEntry);

            var dialog = new SaveFileDialog
            {
                Title = _localizationService["ResearchHistory_Export"],
                FileName = $"Research_{SelectedEntry.CreatedAt:yyyyMMdd_HHmmss}",
                Filter = "Markdown (*.md)|*.md|HTML (*.html)|*.html|PDF (*.xps)|*.xps",
                FilterIndex = 1,
                DefaultExt = ".md"
            };

            if (dialog.ShowDialog() != true)
                return;

            switch (dialog.FilterIndex)
            {
                case 1:
                    await _exportService.ExportAsMarkdownAsync(session, dialog.FileName);
                    break;
                case 2:
                    await _exportService.ExportAsHtmlAsync(session, dialog.FileName);
                    break;
                case 3:
                    await _exportService.ExportAsPdfAsync(session, dialog.FileName);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export research entry");
        }
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
        SafeFireAndForget(DebounceAsync(500, () => LoadEntriesAsync(0, 50), token));
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

    private void OnSessionsChanged(object? sender, EventArgs e)
    {
        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            SafeFireAndForget(LoadEntriesAsync(0, 50)));
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
