using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Models;
using Pia.Navigation;
using Pia.Services.Interfaces;

namespace Pia.ViewModels;


public partial class HistoryViewModel : UiThreadViewModel, IDisposable, INavigationAware
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
    private ObservableCollection<SessionGroupViewModel> _sessionGroups = new();

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

    public IAsyncRelayCommand CopyOriginalCommand { get; }
    public IAsyncRelayCommand CopyOptimizedCommand { get; }
    public IAsyncRelayCommand DeleteSessionCommand { get; }
    public IAsyncRelayCommand DeleteAllCommand { get; }
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
        : base(requireUiThread: true)
    {
        _logger = logger;
        _historyService = historyService;
        _templateService = templateService;
        _providerService = providerService;
        _outputService = outputService;
        _dialogService = dialogService;
        _localizationService = localizationService;

        CopyOriginalCommand = new AsyncRelayCommand(ExecuteCopyOriginal, CanExecuteAction);
        CopyOptimizedCommand = new AsyncRelayCommand(ExecuteCopyOptimized, CanExecuteAction);
        DeleteSessionCommand = new AsyncRelayCommand(ExecuteDeleteSession, CanExecuteAction);
        DeleteAllCommand = new AsyncRelayCommand(ExecuteDeleteAllAsync, CanExecuteDeleteAll);
        ClearFilterCommand = new AsyncRelayCommand(ExecuteClearFilterAsync);
        RefreshCommand = new AsyncRelayCommand(ExecuteRefreshAsync);
        LoadMoreCommand = new AsyncRelayCommand(ExecuteLoadMore, CanLoadMore);

        PropertyChanged += OnPropertyChanged;
        _historyService.SessionsChanged += OnSessionsChanged;
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

            RebuildGroups();

            if (SelectedSession is not null && !Sessions.Contains(SelectedSession))
            {
                SelectedSession = null;
            }

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

    private void RebuildGroups()
    {
        var existingState = SessionGroups.ToDictionary(g => g.Bucket, g => g.IsExpanded);

        var today = DateTime.Today;
        var startOfWeek = today.AddDays(-(int)today.DayOfWeek);
        var startOfMonth = new DateTime(today.Year, today.Month, 1);

        var buckets = Sessions
            .GroupBy(s => Classify(s.CreatedAt.ToLocalTime(), today, startOfWeek, startOfMonth))
            .OrderBy(g => (int)g.Key)
            .Select(g =>
            {
                var items = g.OrderByDescending(s => s.CreatedAt).ToList();
                var isExpanded = existingState.TryGetValue(g.Key, out var prev)
                    ? prev
                    : (g.Key == HistoryDateBucket.Today || g.Key == HistoryDateBucket.Yesterday);
                return new SessionGroupViewModel
                {
                    Bucket = g.Key,
                    DisplayName = _localizationService[BucketResourceKey(g.Key)],
                    Items = new ObservableCollection<OptimizationSession>(items),
                    ItemCount = items.Count,
                    IsExpanded = isExpanded,
                };
            })
            .ToList();

        SessionGroups.Clear();
        foreach (var group in buckets)
        {
            SessionGroups.Add(group);
        }
    }

    private static HistoryDateBucket Classify(DateTime createdLocal, DateTime today, DateTime startOfWeek, DateTime startOfMonth)
    {
        var date = createdLocal.Date;
        if (date == today) return HistoryDateBucket.Today;
        if (date == today.AddDays(-1)) return HistoryDateBucket.Yesterday;
        if (date >= startOfWeek) return HistoryDateBucket.ThisWeek;
        if (date >= startOfMonth) return HistoryDateBucket.EarlierThisMonth;
        return HistoryDateBucket.Older;
    }

    private static string BucketResourceKey(HistoryDateBucket bucket) => bucket switch
    {
        HistoryDateBucket.Today => "History_Group_Today",
        HistoryDateBucket.Yesterday => "History_Group_Yesterday",
        HistoryDateBucket.ThisWeek => "History_Group_ThisWeek",
        HistoryDateBucket.EarlierThisMonth => "History_Group_EarlierThisMonth",
        HistoryDateBucket.Older => "History_Group_Older",
        _ => "History_Group_Older",
    };

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
        DebounceAsync(500, () => LoadSessionsAsync(0, 50), token).SafeFireAndForget(_logger);
    }

    private static async Task DebounceAsync(int delayMs, Func<Task> action, CancellationToken ct)
    {
        await Task.Delay(delayMs, ct);
        await action();
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
            RebuildGroups();
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

    private async Task ExecuteDeleteAllAsync()
    {
        if (TotalCount == 0)
            return;

        var confirmed = await _dialogService.ShowConfirmationDialogAsync(
            _localizationService["History_DeleteAllConfirmTitle"],
            _localizationService["History_DeleteAllConfirmBody"]);

        if (!confirmed)
            return;

        try
        {
            IsLoading = true;
            await _historyService.DeleteAllSessionsAsync(
                searchText: SearchQuery,
                templateId: SelectedTemplateId,
                fromDate: FilterStartDate,
                toDate: FilterEndDate);
            SelectedSession = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete all sessions");
            await _dialogService.ShowMessageDialogAsync(
                _localizationService["Msg_Error"],
                _localizationService.Format("Msg_History_DeleteSessionFailed", ex.Message));
        }
        finally
        {
            await LoadSessionsAsync(0, 50);
        }
    }

    private bool CanExecuteAction()
    {
        return SelectedSession is not null && !IsLoading;
    }

    private bool CanExecuteDeleteAll()
    {
        return !IsLoading && TotalCount > 0;
    }

    private bool CanLoadMore()
    {
        return !IsLoading && Sessions.Count < TotalCount;
    }

    private void UpdateCommandStates()
    {
        CopyOriginalCommand.NotifyCanExecuteChanged();
        CopyOptimizedCommand.NotifyCanExecuteChanged();
        DeleteSessionCommand.NotifyCanExecuteChanged();
        DeleteAllCommand.NotifyCanExecuteChanged();
        LoadMoreCommand.NotifyCanExecuteChanged();
    }

    private void OnSessionsChanged(object? sender, EventArgs e)
    {
        Post(() => LoadSessionsAsync(0, 50).SafeFireAndForget(_logger));
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

        if (e.PropertyName == nameof(IsLoading) || e.PropertyName == nameof(TotalCount))
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

        // Unsubscribe from events
        _historyService.SessionsChanged -= OnSessionsChanged;
        PropertyChanged -= OnPropertyChanged;

        GC.SuppressFinalize(this);
    }
}

public partial class SessionGroupViewModel : ObservableObject
{
    [ObservableProperty]
    private HistoryDateBucket _bucket;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private ObservableCollection<OptimizationSession> _items = new();

    [ObservableProperty]
    private int _itemCount;

    [ObservableProperty]
    private bool _isExpanded = true;
}
