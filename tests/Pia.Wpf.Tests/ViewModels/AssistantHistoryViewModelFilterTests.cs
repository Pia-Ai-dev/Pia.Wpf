using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Navigation;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Pia.ViewModels;
using Pia.ViewModels.Models;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.ViewModels;

public class AssistantHistoryViewModelFilterTests
{
    private readonly IAssistantChatService _chatService = Substitute.For<IAssistantChatService>();
    private readonly IProviderService _providers = Substitute.For<IProviderService>();
    private readonly IDialogService _dialog = Substitute.For<IDialogService>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly INavigationService _nav = Substitute.For<INavigationService>();
    private readonly global::Wpf.Ui.ISnackbarService _snackbar = Substitute.For<global::Wpf.Ui.ISnackbarService>();
    private readonly IChatSessionManager _sessions = Substitute.For<IChatSessionManager>();
    private readonly IMarkdownExportService _markdownExport = Substitute.For<IMarkdownExportService>();
    private readonly Dictionary<Guid, ChatState> _states = new();

    public AssistantHistoryViewModelFilterTests()
    {
        // DisplayName == resource key so option labels stay key-based.
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        // Live state per chat (Idle when not registered), mirroring the real manager.
        _sessions.GetState(Arg.Any<Guid>())
            .Returns(ci => _states.TryGetValue((Guid)ci[0], out var s) ? s : ChatState.Idle);
    }

    /// <summary>Runs <see cref="SynchronizationContext.Post"/> callbacks inline so the VM's
    /// SessionStateChanged marshalling is deterministic in-test.</summary>
    private AssistantHistoryViewModel CreateSut(IReadOnlyList<SyncAssistantChat> chats)
    {
        // Captured by the VM ctor; inline posting makes the SessionStateChanged path run
        // synchronously on the test thread.
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());

        // A synchronously-completed Task makes RefreshCommand → LoadChatsAsync run inline.
        _chatService.SearchAsync().ReturnsForAnyArgs(
            Task.FromResult<IReadOnlyList<SyncAssistantChat>>(chats));
        // OnNavigatedToAsync swallows exceptions into a log, so an auto-stubbed null provider list
        // would skip the load entirely and let a test pass without ever reaching the query.
        _chatService.CountAsync().ReturnsForAnyArgs(Task.FromResult(chats.Count));
        _providers.GetProvidersAsync().Returns(
            Task.FromResult<IReadOnlyList<AiProvider>>(Array.Empty<AiProvider>()));

        return new AssistantHistoryViewModel(
            NullLogger<AssistantHistoryViewModel>.Instance,
            _chatService, _providers, _dialog, _loc, _nav, _snackbar, _sessions, _markdownExport,
            Substitute.For<IChatArchiveService>());
    }

    private SyncAssistantChat Chat(string title, ChatState state)
    {
        var id = Guid.NewGuid();
        _states[id] = state;
        return new SyncAssistantChat { Id = id, Title = title, UpdatedAt = DateTime.UtcNow };
    }

    [Fact]
    public async Task OnNavigatedToAsync_SeedsNoEndDate_SoLaterChatsAreNotFilteredOut()
    {
        // The end date used to be seeded from DateTime.Today once per app run. Because this VM is
        // cached for the process lifetime, that bound went stale at the next midnight and the SQL
        // filter dropped every newer chat — refresh could not help, only a restart.
        var sut = CreateSut(new List<SyncAssistantChat>());

        await sut.OnNavigatedToAsync(null);

        // The argument is what reaches SQL; asserting the property alone would not prove the query.
        await _chatService.Received().SearchAsync(
            searchText: Arg.Any<string?>(),
            fromDate: Arg.Any<DateTime?>(),
            toDate: null,
            providerId: Arg.Any<Guid?>(),
            offset: Arg.Any<int>(),
            limit: Arg.Any<int>(),
            ct: Arg.Any<CancellationToken>());
        Assert.Null(sut.FilterEndDate);
    }

    [Fact]
    public void StateFilterOptions_StartWithAll_ThenActionNeededOrder()
    {
        var sut = CreateSut(new List<SyncAssistantChat>());

        Assert.Null(sut.StateFilterOptions[0].State); // "All states"
        var states = sut.StateFilterOptions.Skip(1).Select(o => o.State).ToArray();
        Assert.Equal(
            new ChatState?[]
            {
                ChatState.WaitingForTool,
                ChatState.Running,
                ChatState.Error,
                ChatState.Completed,
                ChatState.Idle,
            },
            states);
    }

    [Fact]
    public async Task StateFilter_All_ShowsEveryChat()
    {
        var sut = CreateSut(new List<SyncAssistantChat>
        {
            Chat("running", ChatState.Running),
            Chat("idle", ChatState.Idle),
        });

        await sut.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(2, sut.VisibleCount);
        Assert.Equal(2, sut.ChatGroups.Sum(g => g.ItemCount));
    }

    [Fact]
    public async Task StateFilter_Running_ShowsOnlyRunningChats()
    {
        var running = Chat("running", ChatState.Running);
        var sut = CreateSut(new List<SyncAssistantChat>
        {
            running,
            Chat("idle", ChatState.Idle),
            Chat("done", ChatState.Completed),
        });
        await sut.RefreshCommand.ExecuteAsync(null);

        sut.SelectedStateOption = sut.StateFilterOptions.First(o => o.State == ChatState.Running);

        Assert.Equal(1, sut.VisibleCount);
        var item = Assert.Single(sut.ChatGroups.SelectMany(g => g.Items));
        Assert.Equal(running.Id, item.Id);
    }

    [Fact]
    public async Task StateFilter_BackToAll_RestoresAllChats()
    {
        var sut = CreateSut(new List<SyncAssistantChat>
        {
            Chat("running", ChatState.Running),
            Chat("idle", ChatState.Idle),
        });
        await sut.RefreshCommand.ExecuteAsync(null);

        sut.SelectedStateOption = sut.StateFilterOptions.First(o => o.State == ChatState.Running);
        Assert.Equal(1, sut.VisibleCount);

        sut.SelectedStateOption = sut.StateFilterOptions[0]; // back to "All states"
        Assert.Equal(2, sut.VisibleCount);
    }

    [Fact]
    public async Task SessionStateChange_WhileFilterActive_ReappliesFilter()
    {
        var chat = Chat("chat", ChatState.Idle); // starts Idle
        var sut = CreateSut(new List<SyncAssistantChat> { chat });
        await sut.RefreshCommand.ExecuteAsync(null);

        // Filter to Running — the still-Idle chat is hidden.
        sut.SelectedStateOption = sut.StateFilterOptions.First(o => o.State == ChatState.Running);
        Assert.Equal(0, sut.VisibleCount);

        // The chat goes live (Running); a live transition must re-apply the active filter
        // so the row enters the filtered view.
        _sessions.SessionStateChanged += Raise.EventWith(new SessionStateChangedEventArgs
        {
            ChatId = chat.Id,
            OldState = ChatState.Idle,
            NewState = ChatState.Running,
            IsActive = false,
        });

        Assert.Equal(1, sut.VisibleCount);
    }
}
