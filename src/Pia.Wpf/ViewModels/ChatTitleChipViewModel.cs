using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Pia.ViewModels.Models;

namespace Pia.ViewModels;

public partial class ChatTitleChipViewModel : UiThreadViewModel, IDisposable
{
    private const int RecentLimit = 10;
    private const int DebounceMs = 300;
    private const int QuickSwitcherCandidateLimit = 50;
    private const int QuickSwitcherSnippetTopN = 8;
    private const int QuickSwitcherSnippetMaxChars = 80;

    private readonly IAssistantChatService _chatService;
    private readonly ILocalizationService _localizationService;
    private readonly ILogger<ChatTitleChipViewModel> _logger;
    private readonly Func<Guid, Task> _resumeChat;
    private readonly Func<Guid, Task> _deleteChat;
    private readonly Action<string?> _newChat;
    private readonly Action _showAllChats;
    private readonly Func<Guid, ChatState> _resolveState;
    private readonly Action<string?> _setActiveWorkingDirectory;
    private readonly Func<string?> _getActiveWorkingDirectory;
    private readonly IWorkingDirectoryService _workingDirectoryService;
    private CancellationTokenSource? _debounceCts;
    private CancellationTokenSource? _quickSwitcherCts;
    private List<SyncAssistantChat> _quickSwitcherCandidates = [];
    private List<SyncAssistantChat> _lastFlyoutChats = [];
    /// <summary>Folder the next "+ New Chat" opens in (forward-slash relative; <c>""</c> = root).
    /// Tracks the picker; re-seeded from the active chat each time the flyout opens.</summary>
    private string _pendingNewChatDirectory = string.Empty;
    private bool _disposed;

    [ObservableProperty]
    private string _currentTitle = string.Empty;

    /// <summary>Live state of the active chat — drives the badge pill on the chip.</summary>
    [ObservableProperty]
    private ChatState _activeState = ChatState.Idle;

    [ObservableProperty]
    private bool _isFlyoutOpen;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private bool _isQuickSwitcherOpen;

    [ObservableProperty]
    private string _quickSwitcherQuery = string.Empty;

    [ObservableProperty]
    private int _selectedIndex;

    /// <summary>The folder shown on the pill — the one the next "+ New Chat" opens in: backslash
    /// form, <c>\</c> at root (e.g. <c>\</c> or <c>\src\app</c>). Seeded from the active chat on
    /// flyout open, then updated as the user drills the picker. Display-only; navigation uses the
    /// picker's forward-slash relative path.</summary>
    [ObservableProperty]
    private string _workingDirectoryDisplay = "\\";

    /// <summary>True when the pill folder is the sandbox root (drives the home glyph).</summary>
    [ObservableProperty]
    private bool _isWorkingDirectoryRoot = true;

    /// <summary>Drives the nested drill-down folder picker popup.</summary>
    [ObservableProperty]
    private bool _isPickerOpen;

    /// <summary>The embedded drill-down folder picker.</summary>
    public WorkingDirectoryPickerViewModel WorkingDirectoryPicker { get; }

    public ObservableCollection<ChatChipGroupViewModel> Groups { get; } = [];
    public ObservableCollection<QuickSwitcherMatchViewModel> Matches { get; } = [];

    public IAsyncRelayCommand<Guid?> ResumeChatCommand { get; }
    public IAsyncRelayCommand<ChatChipItemViewModel?> DeleteChatCommand { get; }
    public IRelayCommand NewChatCommand { get; }
    public IRelayCommand ShowAllChatsCommand { get; }
    public IRelayCommand OpenQuickSwitcherCommand { get; }
    public IRelayCommand CloseQuickSwitcherCommand { get; }
    public IAsyncRelayCommand ConfirmSelectionCommand { get; }
    public IRelayCommand<int> MoveSelectionCommand { get; }
    public IRelayCommand OpenWorkingDirectoryCommand { get; }

    public ChatTitleChipViewModel(
        IAssistantChatService chatService,
        ILocalizationService localizationService,
        ILogger<ChatTitleChipViewModel> logger,
        Func<Guid, Task> resumeChat,
        Func<Guid, Task> deleteChat,
        Action<string?> newChat,
        Action showAllChats,
        Func<Guid, ChatState> resolveState,
        IWorkingDirectoryService workingDirectoryService,
        Action<string?> setActiveWorkingDirectory,
        Func<string?> getActiveWorkingDirectory)
        : base(requireUiThread: true)
    {
        _chatService = chatService;
        _localizationService = localizationService;
        _logger = logger;
        _resumeChat = resumeChat;
        _deleteChat = deleteChat;
        _newChat = newChat;
        _showAllChats = showAllChats;
        _resolveState = resolveState;
        _setActiveWorkingDirectory = setActiveWorkingDirectory;
        _getActiveWorkingDirectory = getActiveWorkingDirectory;
        _workingDirectoryService = workingDirectoryService;

        WorkingDirectoryPicker = new WorkingDirectoryPickerViewModel(workingDirectoryService);
        WorkingDirectoryPicker.WorkingDirectoryChosen += OnWorkingDirectoryChosen;

        CurrentTitle = _localizationService["AssistantChat_TitlePlaceholder_NewChat"];

        ResumeChatCommand = new AsyncRelayCommand<Guid?>(ExecuteResumeChat);
        DeleteChatCommand = new AsyncRelayCommand<ChatChipItemViewModel?>(ExecuteDeleteChat);
        NewChatCommand = new RelayCommand(ExecuteNewChat);
        ShowAllChatsCommand = new RelayCommand(ExecuteShowAllChats);
        OpenQuickSwitcherCommand = new RelayCommand(ExecuteOpenQuickSwitcher);
        CloseQuickSwitcherCommand = new RelayCommand(ExecuteCloseQuickSwitcher);
        ConfirmSelectionCommand = new AsyncRelayCommand(ExecuteConfirmSelection);
        MoveSelectionCommand = new RelayCommand<int>(ExecuteMoveSelection);
        OpenWorkingDirectoryCommand = new RelayCommand(ExecuteOpenWorkingDirectory);

        PropertyChanged += OnPropertyChanged;
        _chatService.ChatsChanged += OnChatsChanged;
    }

    public void SetTitle(string? title) =>
        CurrentTitle = string.IsNullOrWhiteSpace(title)
            ? _localizationService["AssistantChat_TitlePlaceholder_NewChat"]
            : title;

    public void SetState(ChatState state) => ActiveState = state;

    /// <summary>Opens the pill folder in Explorer. Resolves the same relative path the pill displays, so the
    /// icon can never open a folder other than the one being read; a folder deleted since the chat was created
    /// resolves to null and the click does nothing.</summary>
    private void ExecuteOpenWorkingDirectory() =>
        ShellLauncher.RevealInExplorer(_workingDirectoryService.ResolveAbsolutePath(_pendingNewChatDirectory));

    /// <summary>Refresh a quick-switcher match's live state in place (called on SessionStateChanged).</summary>
    public void RefreshMatchState(Guid chatId, ChatState state)
    {
        var match = Matches.FirstOrDefault(m => m.Id == chatId);
        if (match is not null)
            match.State = state;
    }

    private void OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IsFlyoutOpen))
        {
            if (IsFlyoutOpen)
            {
                // Re-seed the "+ New Chat" folder target to the active chat's folder each time
                // the flyout opens, so an abandoned pick from a previous open doesn't linger.
                SetWorkingDirectory(_getActiveWorkingDirectory());
                LoadRecentChatsAsync().SafeFireAndForget(_logger);
            }
            else
                // Close the drill-down with the flyout so it doesn't auto-pop on the next
                // open. Covers every close path (resume/show-all and the outside-click
                // dismiss that flips IsFlyoutOpen via its binding), not just New Chat.
                IsPickerOpen = false;
        }
        else if (e.PropertyName == nameof(SearchQuery))
            DebounceReload();
        else if (e.PropertyName == nameof(QuickSwitcherQuery))
            RefreshQuickSwitcherMatches();
        else if (e.PropertyName == nameof(IsPickerOpen) && IsPickerOpen)
            // Open the drill-down at the current pending folder (seeded from the active chat
            // on flyout open, or wherever the user last drilled in this flyout session).
            WorkingDirectoryPicker.InitializeFrom(_pendingNewChatDirectory);
    }

    private void OnWorkingDirectoryChosen(object? sender, string relativePath)
    {
        // The user entered/jumped to a folder in the picker. Offer the re-point to the active
        // chat (the owner applies it ONLY while that chat is un-started — a chat with a turn in
        // progress or history keeps its folder), record it as the folder the next "+ New Chat"
        // opens in, and refresh the pill display.
        _setActiveWorkingDirectory(relativePath);
        SetWorkingDirectory(relativePath);
    }

    /// <summary>Reflect the chosen working dir on the pill (backslash display; <c>\</c> at root)
    /// and record it as the folder the next "+ New Chat" opens in.</summary>
    public void SetWorkingDirectory(string? relativePath)
    {
        var normalized = relativePath?.Trim().Replace('\\', '/').Trim('/');
        if (string.IsNullOrEmpty(normalized))
        {
            _pendingNewChatDirectory = string.Empty;
            IsWorkingDirectoryRoot = true;
            WorkingDirectoryDisplay = "\\";
        }
        else
        {
            _pendingNewChatDirectory = normalized;
            IsWorkingDirectoryRoot = false;
            WorkingDirectoryDisplay = "\\" + normalized.Replace('/', '\\');
        }
    }

    private void OnChatsChanged(object? sender, AssistantChatChangedEventArgs e)
    {
        // ChatsChanged can fire off the UI thread (retention BackgroundService). Marshal
        // before reloading — LoadRecentChatsAsync mutates the bound Groups collection.
        Post(() =>
        {
            if (IsFlyoutOpen)
                LoadRecentChatsAsync().SafeFireAndForget(_logger);
        });
    }

    private void DebounceReload()
    {
        if (!IsFlyoutOpen) return;
        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;
        Pia.Helpers.TaskExtensions.DebounceAsync(DebounceMs, LoadRecentChatsAsync, token)
            .SafeFireAndForget(_logger);
    }

    private async Task LoadRecentChatsAsync()
    {
        try
        {
            var chats = await _chatService.SearchAsync(
                searchText: string.IsNullOrWhiteSpace(SearchQuery) ? null : SearchQuery,
                limit: RecentLimit);
            _logger.LogInformation("Loaded {Count} recent chats for flyout (hasQuery={HasQuery})",
                chats.Count, !string.IsNullOrWhiteSpace(SearchQuery));

            _lastFlyoutChats = [.. chats];
            RebuildGroups();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load recent chats for flyout");
        }
    }

    private void RebuildGroups()
    {
        Groups.Clear();
        foreach (var group in BuildDateGroups(_lastFlyoutChats))
            Groups.Add(group);
    }

    private List<ChatChipGroupViewModel> BuildDateGroups(IReadOnlyList<SyncAssistantChat> chats)
    {
        var today = DateTime.Today;
        var yesterday = today.AddDays(-1);
        return chats
            .GroupBy(c => ClassifyForFlyout(c.UpdatedAt.ToLocalTime().Date, today, yesterday))
            .OrderBy(g => (int)g.Key)
            .Select(g => new ChatChipGroupViewModel
            {
                DisplayName = _localizationService[BucketResourceKey(g.Key)],
                // State is a SNAPSHOT read once here via _resolveState (Idle when not live);
                // the flyout row badge reflects it without per-item live notification.
                Items = g.OrderByDescending(c => c.UpdatedAt)
                         .Select(c => new ChatChipItemViewModel(c.Id, ResolveTitle(c), c.UpdatedAt, _resolveState(c.Id)))
                         .ToList(),
            })
            .ToList();
    }

    private static HistoryDateBucket ClassifyForFlyout(DateTime localDate, DateTime today, DateTime yesterday)
    {
        if (localDate == today) return HistoryDateBucket.Today;
        if (localDate == yesterday) return HistoryDateBucket.Yesterday;
        return HistoryDateBucket.Older;
    }

    private static string BucketResourceKey(HistoryDateBucket bucket) => bucket switch
    {
        HistoryDateBucket.Today => "History_Group_Today",
        HistoryDateBucket.Yesterday => "History_Group_Yesterday",
        _ => "History_Group_Older",
    };

    private async Task ExecuteResumeChat(Guid? id)
    {
        if (id is null) return;
        IsFlyoutOpen = false;
        IsQuickSwitcherOpen = false;
        try
        {
            await _resumeChat(id.Value);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resume chat {ChatId}", id);
        }
    }

    private async Task ExecuteDeleteChat(ChatChipItemViewModel? item)
    {
        if (item is null) return;
        // Collapse the flyout first so it doesn't overlap the host's confirmation dialog.
        IsFlyoutOpen = false;
        try
        {
            // The host owns confirmation + the actual delete.
            await _deleteChat(item.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete chat {ChatId} from flyout", item.Id);
        }
    }

    private void ExecuteOpenQuickSwitcher()
    {
        QuickSwitcherQuery = string.Empty;
        SelectedIndex = 0;
        Matches.Clear();
        IsQuickSwitcherOpen = true;
        LoadQuickSwitcherCandidatesAsync().SafeFireAndForget(_logger);
    }

    private void ExecuteCloseQuickSwitcher()
    {
        IsQuickSwitcherOpen = false;
        _quickSwitcherCts?.Cancel();
    }

    private async Task ExecuteConfirmSelection()
    {
        if (Matches.Count == 0) return;
        var index = Math.Clamp(SelectedIndex, 0, Matches.Count - 1);
        var match = Matches[index];
        await ExecuteResumeChat(match.Id);
    }

    private void ExecuteMoveSelection(int delta)
    {
        if (Matches.Count == 0) return;
        var next = SelectedIndex + delta;
        if (next < 0) next = Matches.Count - 1;
        else if (next >= Matches.Count) next = 0;
        SelectedIndex = next;
    }

    private async Task LoadQuickSwitcherCandidatesAsync()
    {
        _quickSwitcherCts?.Cancel();
        _quickSwitcherCts = new CancellationTokenSource();
        var token = _quickSwitcherCts.Token;
        try
        {
            var chats = await _chatService.SearchAsync(limit: QuickSwitcherCandidateLimit, ct: token);
            if (token.IsCancellationRequested) return;
            _quickSwitcherCandidates = [.. chats];
            _logger.LogInformation("Quick switcher loaded {Count} candidate chats", chats.Count);
            RefreshQuickSwitcherMatches();
            FillSnippetsAsync(token).SafeFireAndForget(_logger);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load quick switcher candidates");
        }
    }

    private void RefreshQuickSwitcherMatches()
    {
        if (!IsQuickSwitcherOpen) return;

        var query = QuickSwitcherQuery?.Trim() ?? string.Empty;
        var scored = new List<(SyncAssistantChat Chat, int Score)>();
        foreach (var chat in _quickSwitcherCandidates)
        {
            var title = ResolveTitle(chat);
            if (TryScore(title, query, out var score))
                scored.Add((chat, score));
        }

        scored.Sort((a, b) =>
        {
            var byScore = a.Score.CompareTo(b.Score);
            if (byScore != 0) return byScore;
            var lenA = ResolveTitle(a.Chat).Length;
            var lenB = ResolveTitle(b.Chat).Length;
            var byLen = lenA.CompareTo(lenB);
            if (byLen != 0) return byLen;
            return b.Chat.UpdatedAt.CompareTo(a.Chat.UpdatedAt);
        });

        var existingSnippets = Matches.ToDictionary(m => m.Id, m => m.Snippet);

        Matches.Clear();
        foreach (var (chat, _) in scored)
        {
            existingSnippets.TryGetValue(chat.Id, out var snippet);
            Matches.Add(new QuickSwitcherMatchViewModel
            {
                Id = chat.Id,
                Title = ResolveTitle(chat),
                Snippet = snippet ?? string.Empty,
                State = _resolveState(chat.Id),
            });
        }

        SelectedIndex = Matches.Count == 0 ? 0 : Math.Clamp(SelectedIndex, 0, Matches.Count - 1);
    }

    private string ResolveTitle(SyncAssistantChat chat) =>
        string.IsNullOrWhiteSpace(chat.Title)
            ? _localizationService["AssistantChat_TitlePlaceholder_NewChat"]
            : chat.Title!;

    private static bool TryScore(string title, string query, out int score)
    {
        score = 0;
        if (string.IsNullOrEmpty(query))
            return true;

        var qi = 0;
        for (var i = 0; i < title.Length && qi < query.Length; i++)
        {
            if (char.ToLowerInvariant(title[i]) == char.ToLowerInvariant(query[qi]))
            {
                score += i;
                qi++;
            }
        }
        return qi == query.Length;
    }

    private async Task FillSnippetsAsync(CancellationToken token)
    {
        var topMatches = Matches.Take(QuickSwitcherSnippetTopN)
            .Where(m => string.IsNullOrEmpty(m.Snippet))
            .Select(m => m.Id)
            .ToList();

        foreach (var id in topMatches)
        {
            if (token.IsCancellationRequested) return;
            try
            {
                var chat = await _chatService.GetAsync(id, token);
                if (chat is null) continue;

                var snippet = ExtractSnippet(chat);
                var existing = Matches.FirstOrDefault(m => m.Id == id);
                if (existing is not null)
                    existing.Snippet = snippet;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load snippet for chat {ChatId}", id);
            }
        }
    }

    private static string ExtractSnippet(SyncAssistantChat chat)
    {
        var msg = chat.Messages.LastOrDefault(m => m.Role == "assistant")
                  ?? chat.Messages.LastOrDefault(m => m.Role == "user");
        if (msg is null || string.IsNullOrWhiteSpace(msg.Content)) return string.Empty;

        var text = msg.Content.Trim().Replace('\n', ' ').Replace('\r', ' ');
        return text.Length <= QuickSwitcherSnippetMaxChars
            ? text
            : text[..QuickSwitcherSnippetMaxChars].TrimEnd() + "…";
    }

    private void ExecuteNewChat()
    {
        IsFlyoutOpen = false;
        IsPickerOpen = false;
        // Pin the new chat to the folder selected in the picker (shown on the pill). This is
        // independent of the active chat — picking a folder never re-points a started chat.
        _newChat(_pendingNewChatDirectory);
    }

    private void ExecuteShowAllChats()
    {
        IsFlyoutOpen = false;
        _showAllChats();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _quickSwitcherCts?.Cancel();
        _quickSwitcherCts?.Dispose();
        _chatService.ChatsChanged -= OnChatsChanged;
        WorkingDirectoryPicker.WorkingDirectoryChosen -= OnWorkingDirectoryChosen;
        PropertyChanged -= OnPropertyChanged;
        GC.SuppressFinalize(this);
    }
}

public sealed partial class QuickSwitcherMatchViewModel : ObservableObject
{
    public Guid Id { get; init; }
    public string Title { get; init; } = string.Empty;

    [ObservableProperty]
    private string _snippet = string.Empty;

    /// <summary>Live state of this chat (or Idle if not currently live).</summary>
    [ObservableProperty]
    private ChatState _state = ChatState.Idle;
}
