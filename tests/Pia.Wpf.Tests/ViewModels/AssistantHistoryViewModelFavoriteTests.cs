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

public class AssistantHistoryViewModelFavoriteTests
{
    private readonly IAssistantChatService _chatService = Substitute.For<IAssistantChatService>();
    private readonly IProviderService _providers = Substitute.For<IProviderService>();
    private readonly IDialogService _dialog = Substitute.For<IDialogService>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly INavigationService _nav = Substitute.For<INavigationService>();
    private readonly global::Wpf.Ui.ISnackbarService _snackbar = Substitute.For<global::Wpf.Ui.ISnackbarService>();
    private readonly IChatSessionManager _sessions = Substitute.For<IChatSessionManager>();
    private readonly IMarkdownExportService _markdownExport = Substitute.For<IMarkdownExportService>();

    public AssistantHistoryViewModelFavoriteTests()
    {
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        _sessions.GetState(Arg.Any<Guid>()).Returns(ChatState.Idle);
    }

    private AssistantHistoryViewModel CreateSut(
        IReadOnlyList<SyncAssistantChat> page,
        IReadOnlyList<SyncAssistantChat> favorites,
        int? totalCount = null)
    {
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());

        _chatService.SearchAsync().ReturnsForAnyArgs(
            Task.FromResult<IReadOnlyList<SyncAssistantChat>>(page));
        _chatService.GetFavoritesAsync().ReturnsForAnyArgs(
            Task.FromResult<IReadOnlyList<SyncAssistantChat>>(favorites));
        _chatService.CountAsync().ReturnsForAnyArgs(Task.FromResult(totalCount ?? page.Count));
        _chatService.SetFavoriteAsync(Arg.Any<Guid>(), Arg.Any<bool>()).ReturnsForAnyArgs(Task.FromResult(true));
        _providers.GetProvidersAsync().Returns(
            Task.FromResult<IReadOnlyList<AiProvider>>(Array.Empty<AiProvider>()));

        return new AssistantHistoryViewModel(
            NullLogger<AssistantHistoryViewModel>.Instance,
            _chatService, _providers, _dialog, _loc, _nav, _snackbar, _sessions, _markdownExport,
            Substitute.For<IChatArchiveService>());
    }

    private static SyncAssistantChat Chat(string title, bool isFavorite = false, int daysOld = 0) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        UpdatedAt = DateTime.UtcNow.AddDays(-daysOld),
        IsFavorite = isFavorite,
    };

    [Fact]
    public async Task Favorites_GroupFirst_AndLeaveTheDateBuckets()
    {
        var starred = Chat("starred", isFavorite: true);
        var sut = CreateSut([Chat("ordinary"), starred], [starred]);

        await sut.OnNavigatedToAsync(null);

        Assert.Equal("History_Group_Favorites", sut.ChatGroups[0].DisplayName);
        Assert.Equal(starred.Id, Assert.Single(sut.ChatGroups[0].Items).Id);
        Assert.DoesNotContain(sut.ChatGroups.Skip(1).SelectMany(g => g.Items), r => r.Id == starred.Id);
    }

    /// <summary>The whole point of the separate query: history is paged, so a chat starred months ago is
    /// not in the page the view groups over.</summary>
    [Fact]
    public async Task AFavoriteOutsideTheLoadedPage_StillReachesTheFavoritesGroup()
    {
        var old = Chat("starred long ago", isFavorite: true, daysOld: 200);
        var sut = CreateSut([Chat("ordinary")], [old]);

        await sut.OnNavigatedToAsync(null);

        Assert.Equal(old.Id, Assert.Single(sut.ChatGroups[0].Items).Id);
    }

    /// <summary>Favourites pulled in unpaged inflate <c>Chats</c>, so counting it against the store total
    /// would hide the Load-more row while whole pages are still unread.</summary>
    [Fact]
    public async Task HasMoreChats_CountsThePageOnly_NotThePulledInFavorites()
    {
        var old = Chat("starred long ago", isFavorite: true, daysOld: 200);
        var sut = CreateSut([Chat("a"), Chat("b")], [old], totalCount: 3);

        await sut.OnNavigatedToAsync(null);

        Assert.Equal(3, sut.Chats.Count);
        Assert.True(sut.HasMoreChats);
    }

    [Fact]
    public async Task ToggleFavorite_PersistsAndMovesTheRowIntoTheFavoritesGroup()
    {
        var chat = Chat("ordinary");
        var sut = CreateSut([chat], []);
        await sut.OnNavigatedToAsync(null);

        var row = sut.Chats.Single();
        Assert.DoesNotContain(sut.ChatGroups, g => g.DisplayName == "History_Group_Favorites");

        await sut.ToggleFavoriteChatCommand.ExecuteAsync(row);

        await _chatService.Received(1).SetFavoriteAsync(chat.Id, true, Arg.Any<CancellationToken>());
        Assert.True(row.IsFavorite);
        Assert.Equal("History_Group_Favorites", sut.ChatGroups[0].DisplayName);
        Assert.Same(row, Assert.Single(sut.ChatGroups[0].Items));
    }

    [Fact]
    public async Task ToggleFavorite_OnAStarredRow_UnstarsIt()
    {
        var starred = Chat("starred", isFavorite: true);
        var sut = CreateSut([starred], [starred]);
        await sut.OnNavigatedToAsync(null);

        await sut.ToggleFavoriteChatCommand.ExecuteAsync(sut.Chats.Single());

        await _chatService.Received(1).SetFavoriteAsync(starred.Id, false, Arg.Any<CancellationToken>());
        Assert.DoesNotContain(sut.ChatGroups, g => g.DisplayName == "History_Group_Favorites");
    }
}
