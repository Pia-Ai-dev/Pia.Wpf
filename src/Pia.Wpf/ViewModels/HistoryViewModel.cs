using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Navigation;
using Pia.Services.Interfaces;

namespace Pia.ViewModels;


public partial class HistoryViewModel : ObservableObject, IDisposable, INavigationAware
{
    private readonly ILogger<HistoryViewModel> _logger;
    private bool _disposed;
    private readonly IHistoryService _historyService;
    private readonly ITemplateService _templateService;
    private readonly IProviderService _providerService;
    private readonly IOutputService _outputService;
    private readonly IDialogService _dialogService;
    private readonly ILocalizationService _localizationService;
    private CancellationTokenSource? _debounceCts;
    private int _currentOffset;

    [ObservableProperty]
    private ObservableCollection<OptimizationSession> _sessions = new();

    [ObservableProperty]
    private DateTime? _filterStartDate;

    [ObservableProperty]
    private DateTime? _filterEndDate;

    [ObservableProperty]
    private Guid? _selectedTemplateId;

    [ObservableProperty]
    private Guid? _selectedProviderId;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private OptimizationSession? _selectedSession;

    private ObservableCollection<OptimizationTemplate> _templates = new();

    public ObservableCollection<OptimizationTemplate> Templates => _templates;

    private ObservableCollection<AiProvider> _providers = new();

    public ObservableCollection<AiProvider> Providers => _providers;

    public IAsyncRelayCommand ViewDetailCommand { get; }
    public IAsyncRelayCommand CopyOriginalCommand { get; }
    public IAsyncRelayCommand CopyOptimizedCommand { get; }
    public IAsyncRelayCommand DeleteSessionCommand { get; }
    public IAsyncRelayCommand ApplyFilterCommand { get; }
    public IAsyncRelayCommand ClearFilterCommand { get; }
    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand LoadMoreCommand { get; }

    public HistoryViewModel(
        ILogger<HistoryViewModel> logger,
        IHistoryService historyService,
        ITemplateService templateService,
        IProviderService providerService,
        IOutputService outputService,
        IDialogService dialogService,
        ILocalizationService localizationService)
    {
        _logger = logger;
        _historyService = historyService;
        _templateService = templateService;
        _providerService = providerService;
        _outputService = outputService;
        _dialogService = dialogService;
        _localizationService = localizationService;

        ViewDetailCommand = new AsyncRelayCommand(ExecuteViewDetailAsync, CanExecuteAction);
        CopyOriginalCommand = new AsyncRelayCommand(ExecuteCopyOriginal, CanExecuteAction);
        CopyOptimizedCommand = new AsyncRelayCommand(ExecuteCopyOptimized, CanExecuteAction);
        DeleteSessionCommand = new AsyncRelayCommand(ExecuteDeleteSession, CanExecuteAction);
        ApplyFilterCommand = new AsyncRelayCommand(ExecuteApplyFilterAsync);
        ClearFilterCommand = new AsyncRelayCommand(ExecuteClearFilterAsync);
        RefreshCommand = new AsyncRelayCommand(ExecuteRefreshAsync);
        LoadMoreCommand = new AsyncRelayCommand(ExecuteLoadMore, CanLoadMore);

        PropertyChanged += OnPropertyChanged;
    }

    public void OnNavigatedTo(object? parameter)
    {
    }

    public async Task OnNavigatedToAsync(object? parameter)
    {
        try
        {
            // Only load data if not already loaded
            if (_templates.Count > 0)
                return;

            var templates = await _templateService.GetTemplatesAsync();
            foreach (var template in templates)
                _templates.Add(template);

            var providers = await _providerService.GetProvidersAsync();
            foreach (var provider in providers)
                _providers.Add(provider);

            // Default to last 30 days
            FilterStartDate = DateTime.Today.AddDays(-30);
            FilterEndDate = DateTime.Today;

            await LoadSessionsAsync(0, 50);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize HistoryViewModel");
        }
    }

    public void OnNavigatedFrom()
    {
    }

    private async Task LoadSessionsAsync(int offset, int take)
    {
        try
        {
            IsLoading = true;

            var sessions = await _historyService.SearchSessionsAsync(
                searchText: SearchQuery,
                templateId: SelectedTemplateId,
                fromDate: FilterStartDate,
                toDate: FilterEndDate,
                offset: offset,
                limit: take);

            if (offset == 0)
            {
                Sessions.Clear();
                foreach (var session in sessions)
                {
                    Sessions.Add(session);
                }
            }
            else
            {
                foreach (var session in sessions)
                {
                    Sessions.Add(session);
                }
            }

            _currentOffset = offset + sessions.Count;

            TotalCount = await _historyService.GetSessionCountAsync(
                searchText: SearchQuery,
                templateId: SelectedTemplateId,
                fromDate: FilterStartDate,
                toDate: FilterEndDate);

            UpdateCommandStates();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load sessions (offset: {Offset}, take: {Take})", offset, take);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task ExecuteApplyFilterAsync()
    {
        await LoadSessionsAsync(0, 50);
    }

    private async Task ExecuteClearFilterAsync()
    {
        FilterStartDate = null;
        FilterEndDate = null;
        SelectedTemplateId = null;
        SelectedProviderId = null;
        SearchQuery = string.Empty;
        await LoadSessionsAsync(0, 50);
    }

    private async Task ExecuteRefreshAsync()
    {
        await LoadSessionsAsync(0, 50);
    }

    private void DebounceSearch()
    {
        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;
        SafeFireAndForget(DebounceAsync(500, () => LoadSessionsAsync(0, 50), token));
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

    private async Task ExecuteViewDetailAsync()
    {
        if (SelectedSession is null)
            return;

        await _dialogService.ShowSessionDetailDialogAsync(SelectedSession);
    }

    private async Task ExecuteDeleteSession()
    {
        if (SelectedSession is null)
            return;

        var confirmed = await _dialogService.ShowConfirmationDialogAsync(
            _localizationService["Msg_History_ConfirmDeleteTitle"],
            _localizationService["Msg_History_ConfirmDeleteMessage"]);

        if (!confirmed)
            return;

        var session = SelectedSession;

        try
        {
            await _historyService.DeleteSessionAsync(session.Id);
            Sessions.Remove(session);
            SelectedSession = null;
            TotalCount = await _historyService.GetSessionCountAsync(
                searchText: SearchQuery,
                templateId: SelectedTemplateId,
                fromDate: FilterStartDate,
                toDate: FilterEndDate);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete session {SessionId}", session.Id);
            await _dialogService.ShowMessageDialogAsync(_localizationService["Msg_Error"], _localizationService.Format("Msg_History_DeleteSessionFailed", ex.Message));
        }
    }

    private async Task ExecuteCopyOriginal()
    {
        if (SelectedSession is null)
            return;

        try
        {
            await _outputService.CopyToClipboardAsync(SelectedSession.OriginalText);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to copy original text to clipboard");
            await _dialogService.ShowMessageDialogAsync(_localizationService["Msg_Error"], _localizationService.Format("Msg_History_CopyFailed", ex.Message));
        }
    }

    private async Task ExecuteCopyOptimized()
    {
        if (SelectedSession is null)
            return;

        try
        {
            await _outputService.CopyToClipboardAsync(SelectedSession.OptimizedText);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to copy optimized text to clipboard");
            await _dialogService.ShowMessageDialogAsync(_localizationService["Msg_Error"], _localizationService.Format("Msg_History_CopyFailed", ex.Message));
        }
    }

    private async Task ExecuteLoadMore()
    {
        await LoadSessionsAsync(_currentOffset, 50);
    }

    private bool CanExecuteAction()
    {
        return SelectedSession is not null && !IsLoading;
    }

    private bool CanLoadMore()
    {
        return !IsLoading && Sessions.Count < TotalCount;
    }

    private void UpdateCommandStates()
    {
        ViewDetailCommand.NotifyCanExecuteChanged();
        CopyOriginalCommand.NotifyCanExecuteChanged();
        CopyOptimizedCommand.NotifyCanExecuteChanged();
        DeleteSessionCommand.NotifyCanExecuteChanged();
        LoadMoreCommand.NotifyCanExecuteChanged();
    }

    private void OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SelectedSession))
        {
            UpdateCommandStates();
        }

        if (e.PropertyName == nameof(SearchQuery))
        {
            DebounceSearch();
        }

        if (e.PropertyName is nameof(FilterStartDate) or nameof(FilterEndDate) or nameof(SelectedTemplateId))
        {
            DebounceSearch();
        }

        if (e.PropertyName == nameof(IsLoading))
        {
            UpdateCommandStates();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // Cancel debounce timer
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();

        // Unsubscribe from own PropertyChanged event
        PropertyChanged -= OnPropertyChanged;

        GC.SuppressFinalize(this);
    }
}
