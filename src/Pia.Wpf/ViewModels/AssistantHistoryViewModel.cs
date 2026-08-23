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
using Pia.ViewModels.Models;
using Wpf.Ui.Controls;

namespace Pia.ViewModels;

public partial class AssistantHistoryViewModel : UiThreadViewModel, IDisposable, INavigationAware
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
    private readonly IChatSessionManager _chatSessionManager;
    private readonly IMarkdownExportService _markdownExportService;
    private readonly IChatArchiveService _chatArchiveService;
    private CancellationTokenSource? _debounceCts;
    private bool _disposed;
    private bool _initialized;
    private bool _suppressReload;

    [ObservableProperty]
    private ObservableCollection<AssistantChatRowViewModel> _chats = new();

    [ObservableProperty]
    private ObservableCollection<AssistantChatGroupViewModel> _chatGroups = new();

    /// <summary>Selected option in the live-state filter (null State = "All states").</summary>
    [ObservableProperty]
    private ChatStateFilterOption? _selectedStateOption;

    /// <summary>Count of chats currently visible after the state filter (drives the status bar).</summary>
    [ObservableProperty]
    private int _visibleCount;

    /// <summary>How many chats the current filter matches in the store, loaded or not.</summary>
    [ObservableProperty]
    private int _totalCount;

    /// <summary>True while the store holds chats past the last loaded page — drives the Load more row.</summary>
    [ObservableProperty]
    private bool _hasMoreChats;

    /// <summary>"All states" + one option per live <see cref="ChatState"/> (action-needed first).</summary>
    public IReadOnlyList<ChatStateFilterOption> StateFilterOptions { get; }

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

    /// <summary>Drives the import progress row; a large export takes minutes.</summary>
    [ObservableProperty]
    private bool _isImporting;

    [ObservableProperty]
    private string _importStatus = string.Empty;

    /// <summary>Percent stored, meaningful only while <see cref="ImportProgressIsIndeterminate"/> is false.</summary>
    [ObservableProperty]
    private double _importProgress;

    /// <summary>True until the file is parsed — the chat count is unknown before that.</summary>
    [ObservableProperty]
    private bool _importProgressIsIndeterminate = true;

    [ObservableProperty]
    private AssistantChatRowViewModel? _selectedChat;

    [ObservableProperty]
    private SyncAssistantChat? _selectedChatDetail;

    public ObservableCollection<AssistantMessage> SelectedChatMessages { get; } = new();

    public ObservableCollection<AiProvider> Providers { get; } = new();

    public IAsyncRelayCommand DeleteChatCommand { get; }
    public IAsyncRelayCommand<AssistantChatRowViewModel> QuickDeleteChatCommand { get; }
    public IAsyncRelayCommand DeleteAllChatsCommand { get; }
    public IAsyncRelayCommand ClearFilterCommand { get; }
    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand ResumeChatCommand { get; }

    /// <summary>Opens the row it is given rather than the selected one, so a caller (or a script) can name
    /// which past chat to resume.</summary>
    public IAsyncRelayCommand<AssistantChatRowViewModel> OpenChatCommand { get; }
    public IAsyncRelayCommand ExportChatCommand { get; }
    public IAsyncRelayCommand<AssistantMessage> ExportMessageHtmlCommand { get; }
    public IAsyncRelayCommand ExportChatArchiveCommand { get; }
    public IAsyncRelayCommand ExportAllChatsCommand { get; }
    public IAsyncRelayCommand ImportChatsCommand { get; }
    public IAsyncRelayCommand LoadMoreChatsCommand { get; }

    public AssistantHistoryViewModel(
        ILogger<AssistantHistoryViewModel> logger,
        IAssistantChatService chatService,
        IProviderService providerService,
        IDialogService dialogService,
        ILocalizationService localizationService,
        INavigationService navigationService,
        Wpf.Ui.ISnackbarService snackbarService,
        IChatSessionManager chatSessionManager,
        IMarkdownExportService markdownExportService,
        IChatArchiveService chatArchiveService)
        : base(requireUiThread: true)
    {
        _logger = logger;
        _chatService = chatService;
        _providerService = providerService;
        _dialogService = dialogService;
        _localizationService = localizationService;
        _navigationService = navigationService;
        _snackbarService = snackbarService;
        _chatSessionManager = chatSessionManager;
        _markdownExportService = markdownExportService;
        _chatArchiveService = chatArchiveService;

        StateFilterOptions = BuildStateFilterOptions(localizationService);
        _selectedStateOption = StateFilterOptions[0];

        DeleteChatCommand = new AsyncRelayCommand(ExecuteDeleteChatAsync, CanExecuteWithSelection);
        QuickDeleteChatCommand = new AsyncRelayCommand<AssistantChatRowViewModel>(ExecuteQuickDeleteChatAsync);
        DeleteAllChatsCommand = new AsyncRelayCommand(ExecuteDeleteAllChatsAsync);
        ClearFilterCommand = new AsyncRelayCommand(ExecuteClearFilterAsync);
        RefreshCommand = new AsyncRelayCommand(ExecuteRefreshAsync);
        ResumeChatCommand = new AsyncRelayCommand(ExecuteResumeChatAsync, CanExecuteWithSelection);
        OpenChatCommand = new AsyncRelayCommand<AssistantChatRowViewModel>(ExecuteOpenChatAsync);
        ExportChatCommand = new AsyncRelayCommand(ExecuteExportChatAsync, CanExecuteExport);
        ExportMessageHtmlCommand = new AsyncRelayCommand<AssistantMessage>(ExecuteExportMessageHtml);
        ExportChatArchiveCommand = new AsyncRelayCommand(ExecuteExportChatArchiveAsync, CanExecuteExport);
        ExportAllChatsCommand = new AsyncRelayCommand(ExecuteExportAllChatsAsync);
        ImportChatsCommand = new AsyncRelayCommand(ExecuteImportChatsAsync);
        LoadMoreChatsCommand = new AsyncRelayCommand(ExecuteLoadMoreChatsAsync, () => HasMoreChats && !IsLoading);

        PropertyChanged += OnPropertyChanged;
        _chatService.ChatsChanged += OnChatsChanged;
        _chatSessionManager.SessionStateChanged += OnSessionStateChanged;
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

    private Task LoadChatsAsync() => LoadChatsAsync(append: false);

    private async Task LoadChatsAsync(bool append)
    {
        try
        {
            IsLoading = true;
            var offset = append ? Chats.Count : 0;

            var chats = await _chatService.SearchAsync(
                searchText: string.IsNullOrWhiteSpace(SearchQuery) ? null : SearchQuery,
                fromDate: FilterStartDate,
                toDate: FilterEndDate,
                providerId: SelectedProviderId,
                offset: offset,
                limit: PageSize);

            TotalCount = await _chatService.CountAsync(
                searchText: string.IsNullOrWhiteSpace(SearchQuery) ? null : SearchQuery,
                fromDate: FilterStartDate,
                toDate: FilterEndDate,
                providerId: SelectedProviderId);

            var previousSelectedId = SelectedChat?.Id;

            if (!append)
                Chats.Clear();
            foreach (var chat in chats)
                Chats.Add(new AssistantChatRowViewModel(chat, _chatSessionManager.GetState(chat.Id)));

            HasMoreChats = Chats.Count < TotalCount;

            _logger.LogInformation(
                "AssistantHistory showing {Loaded} of {Total} chats (hasQuery={HasQuery}, providerFilter={HasProvider})",
                Chats.Count,
                TotalCount,
                !string.IsNullOrWhiteSpace(SearchQuery),
                SelectedProviderId.HasValue);
            _logger.SensitiveDebug("AssistantHistory query: {Query}", SearchQuery);

            RebuildGroups();

            // Re-resolve the selection against the freshly-wrapped rows (the row VM
            // instances are new each load, so reference equality won't survive).
            SelectedChat = previousSelectedId is { } id
                ? Chats.FirstOrDefault(r => r.Id == id)
                : null;

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
        var existingState = ChatGroups
            .Where(g => g.GroupKey is not null)
            .ToDictionary(g => g.GroupKey!, g => g.IsExpanded);

        // Live-status filter (client-side): GetState returns Idle for any persisted-but-
        // not-live chat, so this meaningfully isolates the live set (Running/Waiting/Error/
        // Completed); a null State means "All states".
        var stateFilter = SelectedStateOption?.State;
        var filtered = stateFilter is { } s
            ? Chats.Where(c => c.State == s).ToList()
            : Chats.ToList();
        VisibleCount = filtered.Count;

        ChatGroups.Clear();
        foreach (var group in BuildDateGroups(filtered, existingState))
            ChatGroups.Add(group);
    }

    private List<AssistantChatGroupViewModel> BuildDateGroups(
        IReadOnlyList<AssistantChatRowViewModel> chats,
        IReadOnlyDictionary<string, bool> existingState)
    {
        var today = DateTime.Today;
        var startOfWeek = today.AddDays(-(int)today.DayOfWeek);
        var startOfMonth = new DateTime(today.Year, today.Month, 1);

        return chats
            .GroupBy(c => Classify(c.UpdatedAt.ToLocalTime(), today, startOfWeek, startOfMonth))
            .OrderBy(g => (int)g.Key)
            .Select(g =>
            {
                var items = g.OrderByDescending(c => c.UpdatedAt).ToList();
                var key = $"date:{(int)g.Key}";
                var isExpanded = existingState.TryGetValue(key, out var prev)
                    ? prev
                    : (g.Key == HistoryDateBucket.Today || g.Key == HistoryDateBucket.Yesterday);
                return new AssistantChatGroupViewModel
                {
                    GroupKey = key,
                    Bucket = g.Key,
                    DisplayName = _localizationService[BucketResourceKey(g.Key)],
                    Items = new ObservableCollection<AssistantChatRowViewModel>(items),
                    ItemCount = items.Count,
                    IsExpanded = isExpanded,
                };
            })
            .ToList();
    }

    private static IReadOnlyList<ChatStateFilterOption> BuildStateFilterOptions(ILocalizationService loc)
    {
        var options = new List<ChatStateFilterOption>
        {
            new(null, loc["AssistantHistory_StateFilter_All"]),
        };
        // Action-needed first, mirroring the badge/grouping order via the shared comparer.
        options.AddRange(Enum.GetValues<ChatState>()
            .OrderBy(ChatStateGrouping.StateGroupOrder)
            .Select(state => new ChatStateFilterOption(state, loc[$"ChatState_{state}"])));
        return options;
    }

    partial void OnSelectedStateOptionChanged(ChatStateFilterOption? value) => RebuildGroups();

    private void OnSessionStateChanged(object? sender, SessionStateChangedEventArgs e)
    {
        if (e.ChatId is not { } chatId) return;
        Post(() =>
        {
            var row = Chats.FirstOrDefault(r => r.Id == chatId);
            if (row is null) return;
            row.State = e.NewState;
            // A live transition changes which rows pass an active state filter, so
            // re-apply it. Date grouping itself is unaffected by state.
            if (SelectedStateOption?.State is not null)
                RebuildGroups();
        });
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
        // Reset to "All states" (index 0). Assigning the same instance is a no-op; a real
        // change fires OnSelectedStateOptionChanged → RebuildGroups, which LoadChatsAsync
        // also runs, so the list is rebuilt exactly once either way.
        SelectedStateOption = StateFilterOptions[0];
        await LoadChatsAsync();
    }

    private Task ExecuteRefreshAsync() => LoadChatsAsync();

    /// <summary>Appends the next page. History is capped at one page, which a migration import blows past.</summary>
    private Task ExecuteLoadMoreChatsAsync() => LoadChatsAsync(append: true);

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

        var row = SelectedChat;
        try
        {
            await _chatService.DeleteAsync(row.Id);
            AbandonActiveSessionIfDeleted(row.Id);
            Chats.Remove(row);
            SelectedChat = null;
            RebuildGroups();
            _logger.LogInformation("Deleted assistant chat {ChatId}", row.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete chat {ChatId}", row.Id);
            await _dialogService.ShowMessageDialogAsync(
                _localizationService["Msg_Error"],
                _localizationService.Format("Msg_History_DeleteSessionFailed", ex.Message));
        }
    }

    /// <summary>Per-row quick delete (hover trash icon): deletes the given row rather than the selected one.</summary>
    private async Task ExecuteQuickDeleteChatAsync(AssistantChatRowViewModel? row)
    {
        if (row is null) return;

        var confirmed = await _dialogService.ShowConfirmationDialogAsync(
            _localizationService["Msg_History_ConfirmDeleteTitle"],
            _localizationService["Msg_History_ConfirmDeleteMessage"]);
        if (!confirmed) return;

        try
        {
            await _chatService.DeleteAsync(row.Id);
            AbandonActiveSessionIfDeleted(row.Id);
            Chats.Remove(row);
            if (ReferenceEquals(SelectedChat, row))
                SelectedChat = null;
            RebuildGroups();
            _logger.LogInformation("Quick-deleted assistant chat {ChatId}", row.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete chat {ChatId}", row.Id);
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
            AbandonActiveSessionIfDeleted(_chatSessionManager.ActiveSession?.Id);
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

    /// <summary>Deleting the open chat: swap the assistant view to a fresh session, cancelling
    /// first so the turn's terminal persist cannot resurrect the deleted row.</summary>
    private void AbandonActiveSessionIfDeleted(Guid? deletedChatId)
    {
        var active = _chatSessionManager.ActiveSession;
        if (deletedChatId is null || active?.Id != deletedChatId) return;

        var inheritedDir = active.WorkingDirectory;
        active.Cancel();
        _chatSessionManager.GetOrCreateActiveForNewChat().SetWorkingDirectory(inheritedDir);
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

    /// <summary>
    /// Per-message export to a static HTML file — the same action offered on live assistant messages,
    /// so past chats get parity. Adds an "open file" chip to the message and opens it in the browser.
    /// </summary>
    private async Task ExecuteExportMessageHtml(AssistantMessage? message)
    {
        if (message is null || string.IsNullOrEmpty(message.Content))
            return;

        try
        {
            var fallbackTitle = _localizationService["Msg_Assistant_ExportDefaultTitle"];
            var path = await _markdownExportService.ExportAsync(
                message.Content, title: null, fallbackTitle, workingSubpath: null);

            message.AddOrUpgradeFileRef(new FileRef(path, FileRefKind.Exported));
            ShellLauncher.OpenFile(path);

            _snackbarService.Show(
                _localizationService["Msg_Assistant_Exported"],
                _localizationService.Format("Msg_Assistant_ExportedTo", Path.GetFileName(path)),
                ControlAppearance.Success, null, TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export message to HTML");
            _snackbarService.Show(
                _localizationService["Msg_Error"],
                _localizationService["Msg_Assistant_ExportFailed"],
                ControlAppearance.Danger, null, TimeSpan.FromSeconds(3));
        }
    }

    /// <summary>
    /// Dev-only: a preset path that stands in for the file picker, so a UI script can drive the real
    /// Import/Export buttons without automating a native dialog. Always null in release.
    /// </summary>
    private string? DebugPresetPath(string environmentVariable)
    {
#if DEBUG
        if (Environment.GetEnvironmentVariable(environmentVariable) is not { Length: > 0 } path)
            return null;

        _logger.LogWarning("Chat archive path preset by {EnvVar}; the file picker is bypassed", environmentVariable);
        return path;
#else
        return null;
#endif
    }

    private async Task ExecuteExportChatArchiveAsync()
    {
        var chat = SelectedChatDetail;
        if (chat is null) return;

        var defaultName = SanitizeFileName(chat.Title) is { Length: > 0 } title
            ? $"{title}_{chat.UpdatedAt.ToLocalTime():yyyyMMdd_HHmmss}"
            : $"Chat_{chat.UpdatedAt.ToLocalTime():yyyyMMdd_HHmmss}";

        await ExportToArchiveFileAsync(defaultName, [chat.Id]);
    }

    private Task ExecuteExportAllChatsAsync() =>
        ExportToArchiveFileAsync($"PiaChats_{DateTime.Now:yyyyMMdd_HHmmss}", chatIds: null);

    /// <summary>Writes a re-importable archive; a null <paramref name="chatIds"/> means every stored chat.</summary>
    private async Task ExportToArchiveFileAsync(string defaultName, IReadOnlyList<Guid>? chatIds)
    {
        var filePath = DebugPresetPath(Bootstrapper.DebugChatExportFileEnvVar);
        if (filePath is null)
        {
            var dialog = new SaveFileDialog
            {
                Title = _localizationService["AssistantHistory_ExportArchive"],
                FileName = defaultName,
                Filter = "Pia chat archive (*.json)|*.json",
                DefaultExt = ".json",
            };

            if (dialog.ShowDialog() != true)
                return;

            filePath = dialog.FileName;
        }
        try
        {
            IsLoading = true;
            var count = chatIds is null
                ? await _chatArchiveService.ExportAllAsync(filePath)
                : await _chatArchiveService.ExportAsync(chatIds, filePath);

            _snackbarService.Show(
                _localizationService["Msg_AssistantHistory_Exported"],
                _localizationService.Format("Msg_AssistantHistory_ArchiveExported", count, filePath),
                ControlAppearance.Success, null, TimeSpan.FromSeconds(4));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export a chat archive");
            await _dialogService.ShowMessageDialogAsync(
                _localizationService["Msg_Error"],
                _localizationService.Format("Msg_AssistantHistory_ExportFailed", ex.Message));
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task ExecuteImportChatsAsync()
    {
        var filePath = DebugPresetPath(Bootstrapper.DebugChatImportFileEnvVar);
        if (filePath is null)
        {
            var dialog = new OpenFileDialog
            {
                Title = _localizationService["AssistantHistory_Import"],
                Filter = "Chat export (*.json)|*.json|All files (*.*)|*.*",
                DefaultExt = ".json",
                CheckFileExists = true,
            };

            if (dialog.ShowDialog() != true)
                return;

            filePath = dialog.FileName;
        }

        ChatImportResult result;
        Exception? failure = null;
        try
        {
            IsLoading = true;
            BeginImportProgress();
            // Constructed here, on the UI thread, so the service's background reports come back marshalled.
            var progress = new Progress<ChatImportProgress>(ReportImportProgress);
            result = await _chatArchiveService.ImportAsync(filePath, progress);
        }
        catch (Exception ex)
        {
            failure = ex;
            result = new ChatImportResult();
        }
        finally
        {
            IsImporting = false;
            IsLoading = false;
        }

        // Reported after the finally so the spinner is already off while the dialog is up.
        if (failure is not null)
        {
            _logger.LogError(failure, "Failed to import chats");
            await _dialogService.ShowMessageDialogAsync(
                _localizationService["Msg_Error"],
                _localizationService.Format("Msg_AssistantHistory_ImportFailed", failure.Message));
            return;
        }

        if (result.Format == ChatArchiveFormat.Unknown)
        {
            await _dialogService.ShowMessageDialogAsync(
                _localizationService["AssistantHistory_Import"],
                _localizationService["Msg_AssistantHistory_ImportUnrecognized"]);
            return;
        }

        await RevealImportedChatsAsync(result);

        await _dialogService.ShowMessageDialogAsync(
            _localizationService["AssistantHistory_Import"],
            BuildImportSummary(result));
    }

    internal void BeginImportProgress()
    {
        IsImporting = true;
        ImportProgress = 0;
        ImportProgressIsIndeterminate = true;
        ImportStatus = _localizationService["Msg_AssistantHistory_ImportReading"];
    }

    internal void ReportImportProgress(ChatImportProgress progress)
    {
        ImportProgressIsIndeterminate = progress.Phase != ChatImportPhase.Storing || progress.Total == 0;
        ImportProgress = ImportProgressIsIndeterminate
            ? 0
            : 100.0 * progress.Processed / progress.Total;
        ImportStatus = progress.Phase switch
        {
            ChatImportPhase.Reading => _localizationService["Msg_AssistantHistory_ImportReading"],
            ChatImportPhase.Converting => _localizationService["Msg_AssistantHistory_ImportConverting"],
            _ => _localizationService.Format(
                "Msg_AssistantHistory_ImportStoring", progress.Processed, progress.Total),
        };
    }

    /// <summary>
    /// Widens the filters far enough to show what just landed. Imported chats are typically older than
    /// the default 30-day window and carry no provider, so an unchanged filter would hide every one of
    /// them and the import would read as a silent failure.
    /// </summary>
    internal async Task RevealImportedChatsAsync(ChatImportResult result)
    {
        _suppressReload = true;
        try
        {
            SearchQuery = string.Empty;
            SelectedProviderId = null;
            SelectedStateOption = StateFilterOptions[0];

            if (result.OldestUpdatedAt is { } oldest)
            {
                var oldestLocalDate = oldest.ToLocalTime().Date;
                if (FilterStartDate is null || FilterStartDate > oldestLocalDate)
                    FilterStartDate = oldestLocalDate;
                if (FilterEndDate is not null && FilterEndDate < DateTime.Today)
                    FilterEndDate = DateTime.Today;
            }
        }
        finally
        {
            _suppressReload = false;
        }

        await LoadChatsAsync();
    }

    private string BuildImportSummary(ChatImportResult result)
    {
        var formatName = result.Format == ChatArchiveFormat.OpenWebUi
            ? _localizationService["AssistantHistory_ImportFormat_OpenWebUi"]
            : _localizationService["AssistantHistory_ImportFormat_Pia"];

        var summary = new StringBuilder(
            _localizationService.Format("Msg_AssistantHistory_ImportSummary", formatName, result.Imported, result.Total));

        if (result.SkippedUpToDate > 0)
            summary.AppendLine().Append(
                _localizationService.Format("Msg_AssistantHistory_ImportSkippedUpToDate", result.SkippedUpToDate));
        if (result.SkippedEmpty > 0)
            summary.AppendLine().Append(
                _localizationService.Format("Msg_AssistantHistory_ImportSkippedEmpty", result.SkippedEmpty));
        if (result.DroppedAttachments > 0)
            summary.AppendLine().Append(
                _localizationService.Format("Msg_AssistantHistory_ImportDroppedAttachments", result.DroppedAttachments));
        if (result.Failed > 0)
            summary.AppendLine().Append(
                _localizationService.Format("Msg_AssistantHistory_ImportFailedCount", result.Failed));

        return summary.ToString();
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

    private Task ExecuteResumeChatAsync() => ExecuteOpenChatAsync(SelectedChat);

    private async Task ExecuteOpenChatAsync(AssistantChatRowViewModel? row)
    {
        if (row is null) return;

        var id = row.Chat.Id;
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
        ExportChatArchiveCommand.NotifyCanExecuteChanged();
        LoadMoreChatsCommand.NotifyCanExecuteChanged();
    }

    private void OnChatsChanged(object? sender, AssistantChatChangedEventArgs e)
    {
        // An import saves each chat separately and reveals the whole result itself once done, so its
        // hundreds of events need no reload at all. Otherwise debounce: one search per save would
        // contend for the same write gate the next save needs.
        if (IsImporting) return;
        Post(DebounceReload);
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
        {
            ExportChatCommand.NotifyCanExecuteChanged();
            ExportChatArchiveCommand.NotifyCanExecuteChanged();
        }

        if (e.PropertyName is nameof(IsLoading) or nameof(HasMoreChats))
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
        _chatSessionManager.SessionStateChanged -= OnSessionStateChanged;
        PropertyChanged -= OnPropertyChanged;

        GC.SuppressFinalize(this);
    }
}

public partial class AssistantChatGroupViewModel : ObservableObject
{
    /// <summary>Stable key used to preserve expand/collapse state across rebuilds (date: or state: prefixed).</summary>
    public string? GroupKey { get; init; }

    [ObservableProperty]
    private HistoryDateBucket _bucket;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private ObservableCollection<AssistantChatRowViewModel> _items = new();

    [ObservableProperty]
    private int _itemCount;

    [ObservableProperty]
    private bool _isExpanded = true;
}
