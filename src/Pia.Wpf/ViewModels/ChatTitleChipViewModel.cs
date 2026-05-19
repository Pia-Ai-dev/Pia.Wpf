using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Shared.Models;

namespace Pia.ViewModels;

public partial class ChatTitleChipViewModel : ObservableObject, IDisposable
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
    private readonly Action _newChat;
    private readonly Action _showAllChats;
    private CancellationTokenSource? _debounceCts;
    private CancellationTokenSource? _quickSwitcherCts;
    private List<SyncAssistantChat> _quickSwitcherCandidates = [];
    private bool _disposed;

    [ObservableProperty]
    private string _currentTitle = string.Empty;

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

    public ObservableCollection<ChatChipGroupViewModel> Groups { get; } = [];
    public ObservableCollection<QuickSwitcherMatchViewModel> Matches { get; } = [];

    public IAsyncRelayCommand<Guid?> ResumeChatCommand { get; }
    public IRelayCommand NewChatCommand { get; }
    public IRelayCommand ShowAllChatsCommand { get; }
    public IRelayCommand OpenQuickSwitcherCommand { get; }
    public IRelayCommand CloseQuickSwitcherCommand { get; }
    public IAsyncRelayCommand ConfirmSelectionCommand { get; }
    public IRelayCommand<int> MoveSelectionCommand { get; }

    public ChatTitleChipViewModel(
        IAssistantChatService chatService,
        ILocalizationService localizationService,
        ILogger<ChatTitleChipViewModel> logger,
        Func<Guid, Task> resumeChat,
        Action newChat,
        Action showAllChats)
    {
        _chatService = chatService;
        _localizationService = localizationService;
        _logger = logger;
        _resumeChat = resumeChat;
        _newChat = newChat;
        _showAllChats = showAllChats;

        CurrentTitle = _localizationService["AssistantChat_TitlePlaceholder_NewChat"];

        ResumeChatCommand = new AsyncRelayCommand<Guid?>(ExecuteResumeChat);
        NewChatCommand = new RelayCommand(ExecuteNewChat);
        ShowAllChatsCommand = new RelayCommand(ExecuteShowAllChats);
        OpenQuickSwitcherCommand = new RelayCommand(ExecuteOpenQuickSwitcher);
        CloseQuickSwitcherCommand = new RelayCommand(ExecuteCloseQuickSwitcher);
        ConfirmSelectionCommand = new AsyncRelayCommand(ExecuteConfirmSelection);
        MoveSelectionCommand = new RelayCommand<int>(ExecuteMoveSelection);

        PropertyChanged += OnPropertyChanged;
        _chatService.ChatsChanged += OnChatsChanged;
    }

    public void SetTitle(string? title) =>
        CurrentTitle = string.IsNullOrWhiteSpace(title)
            ? _localizationService["AssistantChat_TitlePlaceholder_NewChat"]
            : title;

    private void OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IsFlyoutOpen) && IsFlyoutOpen)
            LoadRecentChatsAsync().SafeFireAndForget(_logger);
        else if (e.PropertyName == nameof(SearchQuery))
            DebounceReload();
        else if (e.PropertyName == nameof(QuickSwitcherQuery))
            RefreshQuickSwitcherMatches();
    }

    private void OnChatsChanged(object? sender, AssistantChatChangedEventArgs e)
    {
        if (IsFlyoutOpen)
            LoadRecentChatsAsync().SafeFireAndForget(_logger);
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

            RebuildGroups(chats);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load recent chats for flyout");
        }
    }

    private void RebuildGroups(IReadOnlyList<SyncAssistantChat> chats)
    {
        var today = DateTime.Today;
        var yesterday = today.AddDays(-1);

        var groups = chats
            .GroupBy(c => ClassifyForFlyout(c.UpdatedAt.ToLocalTime().Date, today, yesterday))
            .OrderBy(g => (int)g.Key)
            .Select(g => new ChatChipGroupViewModel
            {
                DisplayName = _localizationService[BucketResourceKey(g.Key)],
                Items = g.Select(c => new ChatChipItemViewModel(c.Id, ResolveTitle(c))).ToList(),
            })
            .ToList();

        Groups.Clear();
        foreach (var group in groups)
            Groups.Add(group);
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
        _newChat();
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
        PropertyChanged -= OnPropertyChanged;
        GC.SuppressFinalize(this);
    }
}

public sealed class ChatChipGroupViewModel
{
    public required string DisplayName { get; init; }
    public required IReadOnlyList<ChatChipItemViewModel> Items { get; init; }
}

public sealed record ChatChipItemViewModel(Guid Id, string Title);

public sealed partial class QuickSwitcherMatchViewModel : ObservableObject
{
    public Guid Id { get; init; }
    public string Title { get; init; } = string.Empty;

    [ObservableProperty]
    private string _snippet = string.Empty;
}
