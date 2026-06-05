using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Pia.Helpers;
using Pia.Logging;
using Pia.Models;
using Pia.Navigation;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Wpf.Ui.Controls;

namespace Pia.ViewModels;

public partial class AssistantHistoryViewModel : ObservableObject, IDisposable, INavigationAware
{
    private const int PageSize = 50;
    private const int DebounceMs = 300;

    private readonly ILogger<AssistantHistoryViewModel> _logger;
    private readonly IAssistantChatService _chatService;
    private readonly IProviderService _providerService;
    private readonly IDialogService _dialogService;
    private readonly ILocalizationService _localizationService;
    private readonly INavigationService _navigationService;
    private readonly Wpf.Ui.ISnackbarService _snackbarService;
    private readonly SynchronizationContext _syncContext;
    private CancellationTokenSource? _debounceCts;
    private bool _disposed;
    private bool _initialized;
    private bool _suppressReload;

    [ObservableProperty]
    private ObservableCollection<SyncAssistantChat> _chats = new();

    [ObservableProperty]
    private ObservableCollection<AssistantChatGroupViewModel> _chatGroups = new();

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private DateTime? _filterStartDate;

    [ObservableProperty]
    private DateTime? _filterEndDate;

    [ObservableProperty]
    private Guid? _selectedProviderId;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private SyncAssistantChat? _selectedChat;

    [ObservableProperty]
    private SyncAssistantChat? _selectedChatDetail;

    public ObservableCollection<AssistantMessage> SelectedChatMessages { get; } = new();

    public ObservableCollection<AiProvider> Providers { get; } = new();

    public IAsyncRelayCommand DeleteChatCommand { get; }
    public IAsyncRelayCommand DeleteAllChatsCommand { get; }
    public IAsyncRelayCommand ClearFilterCommand { get; }
    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand ResumeChatCommand { get; }
    public IAsyncRelayCommand ExportChatCommand { get; }

    public AssistantHistoryViewModel(
        ILogger<AssistantHistoryViewModel> logger,
        IAssistantChatService chatService,
        IProviderService providerService,
        IDialogService dialogService,
        ILocalizationService localizationService,
        INavigationService navigationService,
        Wpf.Ui.ISnackbarService snackbarService)
    {
        _logger = logger;
        _chatService = chatService;
        _providerService = providerService;
        _dialogService = dialogService;
        _localizationService = localizationService;
        _navigationService = navigationService;
        _snackbarService = snackbarService;
        _syncContext = SynchronizationContext.Current ?? throw new InvalidOperationException("Must be created on UI thread");

        DeleteChatCommand = new AsyncRelayCommand(ExecuteDeleteChatAsync, CanExecuteWithSelection);
        DeleteAllChatsCommand = new AsyncRelayCommand(ExecuteDeleteAllChatsAsync);
        ClearFilterCommand = new AsyncRelayCommand(ExecuteClearFilterAsync);
        RefreshCommand = new AsyncRelayCommand(ExecuteRefreshAsync);
        ResumeChatCommand = new AsyncRelayCommand(ExecuteResumeChatAsync, CanExecuteWithSelection);
        ExportChatCommand = new AsyncRelayCommand(ExecuteExportChatAsync, CanExecuteExport);

        PropertyChanged += OnPropertyChanged;
        _chatService.ChatsChanged += OnChatsChanged;
    }

    public void OnNavigatedTo(object? parameter) { }

    public async Task OnNavigatedToAsync(object? parameter)
    {
        try
        {
            if (Providers.Count == 0)
            {
                var providers = await _providerService.GetProvidersAsync();
                foreach (var p in providers)
                    Providers.Add(p);
            }

            if (!_initialized)
            {
                _suppressReload = true;
                FilterStartDate = DateTime.Today.AddDays(-30);
                FilterEndDate = DateTime.Today;
                _suppressReload = false;
                _initialized = true;
            }

            await LoadChatsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize AssistantHistoryViewModel");
        }
    }

    public void OnNavigatedFrom() { }

    private async Task LoadChatsAsync()
    {
        try
        {
            IsLoading = true;

            var chats = await _chatService.SearchAsync(
                searchText: string.IsNullOrWhiteSpace(SearchQuery) ? null : SearchQuery,
                fromDate: FilterStartDate,
                toDate: FilterEndDate,
                providerId: SelectedProviderId,
                offset: 0,
                limit: PageSize);

            Chats.Clear();
            foreach (var chat in chats)
                Chats.Add(chat);

            _logger.LogInformation(
                "AssistantHistory loaded {Count} chats (hasQuery={HasQuery}, providerFilter={HasProvider})",
                chats.Count,
                !string.IsNullOrWhiteSpace(SearchQuery),
                SelectedProviderId.HasValue);
            _logger.SensitiveDebug("AssistantHistory query: {Query}", SearchQuery);

            RebuildGroups();

            if (SelectedChat is not null && !Chats.Contains(SelectedChat))
                SelectedChat = null;

            UpdateCommandStates();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load assistant chats");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void RebuildGroups()
    {
        var existingState = ChatGroups.ToDictionary(g => g.Bucket, g => g.IsExpanded);

        var today = DateTime.Today;
        var startOfWeek = today.AddDays(-(int)today.DayOfWeek);
        var startOfMonth = new DateTime(today.Year, today.Month, 1);

        var buckets = Chats
            .GroupBy(c => Classify(c.UpdatedAt.ToLocalTime(), today, startOfWeek, startOfMonth))
            .OrderBy(g => (int)g.Key)
            .Select(g =>
            {
                var items = g.OrderByDescending(c => c.UpdatedAt).ToList();
                var isExpanded = existingState.TryGetValue(g.Key, out var prev)
                    ? prev
                    : (g.Key == HistoryDateBucket.Today || g.Key == HistoryDateBucket.Yesterday);
                return new AssistantChatGroupViewModel
                {
                    Bucket = g.Key,
                    DisplayName = _localizationService[BucketResourceKey(g.Key)],
                    Items = new ObservableCollection<SyncAssistantChat>(items),
                    ItemCount = items.Count,
                    IsExpanded = isExpanded,
                };
            })
            .ToList();

        ChatGroups.Clear();
        foreach (var group in buckets)
            ChatGroups.Add(group);
    }

    private static HistoryDateBucket Classify(DateTime updatedLocal, DateTime today, DateTime startOfWeek, DateTime startOfMonth)
    {
        var date = updatedLocal.Date;
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
        SelectedProviderId = null;
        SearchQuery = string.Empty;
        await LoadChatsAsync();
    }

    private Task ExecuteRefreshAsync() => LoadChatsAsync();

    private void DebounceReload()
    {
        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;
        Pia.Helpers.TaskExtensions.DebounceAsync(DebounceMs, LoadChatsAsync, token)
            .SafeFireAndForget(_logger);
    }

    private async Task ExecuteDeleteChatAsync()
    {
        if (SelectedChat is null) return;

        var confirmed = await _dialogService.ShowConfirmationDialogAsync(
            _localizationService["Msg_History_ConfirmDeleteTitle"],
            _localizationService["Msg_History_ConfirmDeleteMessage"]);
        if (!confirmed) return;

        var chat = SelectedChat;
        try
        {
            await _chatService.DeleteAsync(chat.Id);
            Chats.Remove(chat);
            SelectedChat = null;
            RebuildGroups();
            _logger.LogInformation("Deleted assistant chat {ChatId}", chat.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete chat {ChatId}", chat.Id);
            await _dialogService.ShowMessageDialogAsync(
                _localizationService["Msg_Error"],
                _localizationService.Format("Msg_History_DeleteSessionFailed", ex.Message));
        }
    }

    private async Task ExecuteDeleteAllChatsAsync()
    {
        var confirmed = await _dialogService.ShowConfirmationDialogAsync(
            _localizationService["AssistantHistory_DeleteAllConfirmTitle"],
            _localizationService["AssistantHistory_DeleteAllConfirmBody"]);
        if (!confirmed) return;

        try
        {
            var deleted = await _chatService.DeleteAllAsync();
            _logger.LogInformation("Deleted all assistant chats ({Count})", deleted.Count);
            SelectedChat = null;
            Chats.Clear();
            RebuildGroups();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete all assistant chats");
            await _dialogService.ShowMessageDialogAsync(
                _localizationService["Msg_Error"],
                _localizationService.Format("Msg_AssistantHistory_DeleteAllFailed", ex.Message));
        }
    }

    private bool CanExecuteExport() => SelectedChatDetail is not null && !IsLoading;

    private async Task ExecuteExportChatAsync()
    {
        var chat = SelectedChatDetail;
        if (chat is null) return;

        var defaultName = SanitizeFileName(chat.Title) is { Length: > 0 } title
            ? $"{title}_{chat.UpdatedAt.ToLocalTime():yyyyMMdd_HHmmss}"
            : $"Chat_{chat.UpdatedAt.ToLocalTime():yyyyMMdd_HHmmss}";

        var dialog = new SaveFileDialog
        {
            Title = _localizationService["AssistantHistory_ExportMarkdown"],
            FileName = defaultName,
            Filter = "Markdown (*.md)|*.md",
            DefaultExt = ".md",
        };

        if (dialog.ShowDialog() != true)
            return;

        var filePath = dialog.FileName;
        try
        {
            var markdown = BuildMarkdown(chat);
            await File.WriteAllTextAsync(filePath, markdown, Encoding.UTF8);

            _logger.LogInformation("Exported assistant chat {ChatId} as markdown", chat.Id);
            _snackbarService.Show(
                _localizationService["Msg_AssistantHistory_Exported"],
                _localizationService.Format("Msg_AssistantHistory_ExportedToFile", filePath),
                ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export chat {ChatId}", chat.Id);
            await _dialogService.ShowMessageDialogAsync(
                _localizationService["Msg_Error"],
                _localizationService.Format("Msg_AssistantHistory_ExportFailed", ex.Message));
        }
    }

    private static string BuildMarkdown(SyncAssistantChat chat)
    {
        var sb = new StringBuilder();
        var title = string.IsNullOrWhiteSpace(chat.Title) ? "Assistant chat" : chat.Title;
        sb.Append("# ").AppendLine(title);
        sb.AppendLine();
        sb.Append("*").Append(chat.UpdatedAt.ToLocalTime().ToString("f")).AppendLine("*");
        sb.AppendLine();

        foreach (var msg in chat.Messages)
        {
            var role = string.Equals(msg.Role, "user", StringComparison.OrdinalIgnoreCase) ? "User" : "Assistant";
            sb.Append("## ").Append(role).Append(" — ")
              .AppendLine(msg.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine();
            sb.AppendLine(msg.Content);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        return sb.ToString().Trim();
    }

    private async Task ExecuteResumeChatAsync()
    {
        if (SelectedChat is null) return;

        var id = SelectedChat.Id;
        try
        {
            await _chatService.TouchLastAccessedAsync(id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to touch LastAccessedAt for {ChatId}", id);
        }

        _logger.LogInformation("Resuming chat {ChatId} from AssistantHistory", id);
        _navigationService.NavigateTo<AssistantViewModel, Guid>(id);
    }

    private bool CanExecuteWithSelection() => SelectedChat is not null && !IsLoading;

    private void UpdateCommandStates()
    {
        DeleteChatCommand.NotifyCanExecuteChanged();
        ResumeChatCommand.NotifyCanExecuteChanged();
    }

    private void OnChatsChanged(object? sender, AssistantChatChangedEventArgs e)
    {
        _syncContext.Post(_ => LoadChatsAsync().SafeFireAndForget(_logger), null);
    }

    private void OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SelectedChat))
        {
            UpdateCommandStates();
            LoadSelectedChatDetailAsync().SafeFireAndForget(_logger);
        }

        if (e.PropertyName is nameof(SearchQuery)
            or nameof(FilterStartDate)
            or nameof(FilterEndDate)
            or nameof(SelectedProviderId))
        {
            if (!_suppressReload)
                DebounceReload();
        }

        if (e.PropertyName == nameof(SelectedChatDetail))
            ExportChatCommand.NotifyCanExecuteChanged();

        if (e.PropertyName == nameof(IsLoading))
            UpdateCommandStates();
    }

    private async Task LoadSelectedChatDetailAsync()
    {
        var current = SelectedChat;
        if (current is null)
        {
            SelectedChatDetail = null;
            SelectedChatMessages.Clear();
            return;
        }

        try
        {
            var detail = await _chatService.GetAsync(current.Id);
            if (!ReferenceEquals(SelectedChat, current)) return;

            SelectedChatDetail = detail;
            SelectedChatMessages.Clear();
            if (detail is not null)
            {
                foreach (var msg in detail.Messages)
                    SelectedChatMessages.Add(AssistantMessageMapper.FromDto(msg));
            }

            _logger.LogInformation(
                "Loaded chat detail {ChatId} ({MessageCount} messages)",
                current.Id, detail?.Messages.Count ?? 0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load chat detail {ChatId}", current.Id);
            SelectedChatDetail = null;
            SelectedChatMessages.Clear();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _chatService.ChatsChanged -= OnChatsChanged;
        PropertyChanged -= OnPropertyChanged;

        GC.SuppressFinalize(this);
    }
}

public partial class AssistantChatGroupViewModel : ObservableObject
{
    [ObservableProperty]
    private HistoryDateBucket _bucket;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private ObservableCollection<SyncAssistantChat> _items = new();

    [ObservableProperty]
    private int _itemCount;

    [ObservableProperty]
    private bool _isExpanded = true;
}
