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

    private readonly IAssistantChatService _chatService;
    private readonly ILocalizationService _localizationService;
    private readonly ILogger<ChatTitleChipViewModel> _logger;
    private readonly Func<Guid, Task> _resumeChat;
    private readonly Action _newChat;
    private readonly Action _showAllChats;
    private CancellationTokenSource? _debounceCts;
    private bool _disposed;

    [ObservableProperty]
    private string _currentTitle = string.Empty;

    [ObservableProperty]
    private bool _isFlyoutOpen;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    public ObservableCollection<ChatChipGroupViewModel> Groups { get; } = [];

    public IAsyncRelayCommand<Guid?> ResumeChatCommand { get; }
    public IRelayCommand NewChatCommand { get; }
    public IRelayCommand ShowAllChatsCommand { get; }

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
    }

    private void OnChatsChanged(object? sender, EventArgs e)
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
                Items = g.Select(c => new ChatChipItemViewModel(
                    c.Id,
                    string.IsNullOrWhiteSpace(c.Title)
                        ? _localizationService["AssistantChat_TitlePlaceholder_NewChat"]
                        : c.Title!)).ToList(),
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
        try
        {
            await _resumeChat(id.Value);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resume chat {ChatId}", id);
        }
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
