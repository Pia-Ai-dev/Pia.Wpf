using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

public class ChatTitleChipFlyoutGroupingTests
{
    private readonly IAssistantChatService _chatService = Substitute.For<IAssistantChatService>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly Dictionary<Guid, ChatState> _states = new();

    public ChatTitleChipFlyoutGroupingTests()
    {
        // DisplayName == resource key so assertions stay key-based.
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
    }

    private ChatTitleChipViewModel CreateSut(IReadOnlyList<SyncAssistantChat> chats)
    {
        if (SynchronizationContext.Current is null)
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());

        // ReturnsForAnyArgs so the 8 optional params don't require positional matching;
        // a synchronously-completed Task makes the fire-and-forget reload run inline.
        _chatService.SearchAsync().ReturnsForAnyArgs(
            Task.FromResult<IReadOnlyList<SyncAssistantChat>>(chats));

        return new ChatTitleChipViewModel(
            _chatService,
            _loc,
            NullLogger<ChatTitleChipViewModel>.Instance,
            _ => Task.CompletedTask,
            () => { },
            () => { },
            id => _states.TryGetValue(id, out var s) ? s : ChatState.Idle);
    }

    private SyncAssistantChat Chat(string title, DateTime updatedAt, ChatState? state = null)
    {
        var id = Guid.NewGuid();
        if (state is { } s)
            _states[id] = s;
        return new SyncAssistantChat { Id = id, Title = title, UpdatedAt = updatedAt };
    }

    [Fact]
    public void Flyout_GroupsByDate_TodayThenOlder()
    {
        var chats = new List<SyncAssistantChat>
        {
            Chat("today", DateTime.UtcNow),
            Chat("older", DateTime.UtcNow.AddDays(-10)),
        };
        var sut = CreateSut(chats);

        sut.IsFlyoutOpen = true;

        var keys = sut.Groups.Select(g => g.DisplayName).ToArray();
        Assert.Equal(new[] { "History_Group_Today", "History_Group_Older" }, keys);
    }

    [Fact]
    public void Flyout_WithinDateBucket_SortsByUpdatedAtDesc()
    {
        var older = Chat("older", DateTime.UtcNow.AddHours(-2));
        var newer = Chat("newer", DateTime.UtcNow);
        var sut = CreateSut(new List<SyncAssistantChat> { older, newer });

        sut.IsFlyoutOpen = true;

        var group = Assert.Single(sut.Groups);
        Assert.Equal(newer.Id, group.Items[0].Id);
        Assert.Equal(older.Id, group.Items[1].Id);
    }

    [Fact]
    public void Flyout_Item_SeedsSnapshotStateFromResolver()
    {
        // The inline flyout-row badge reads ChatChipItemViewModel.State, seeded once at
        // build time via the resolveState delegate.
        var chat = Chat("running", DateTime.UtcNow, ChatState.Running);
        var sut = CreateSut(new List<SyncAssistantChat> { chat });

        sut.IsFlyoutOpen = true;

        var item = Assert.Single(Assert.Single(sut.Groups).Items);
        Assert.Equal(ChatState.Running, item.State);
    }

    [Fact]
    public void Flyout_Item_State_DefaultsToIdle_WhenNotLive()
    {
        // No live state registered -> the resolver returns Idle (badge stays hidden).
        var chat = Chat("persisted", DateTime.UtcNow);
        var sut = CreateSut(new List<SyncAssistantChat> { chat });

        sut.IsFlyoutOpen = true;

        var item = Assert.Single(Assert.Single(sut.Groups).Items);
        Assert.Equal(ChatState.Idle, item.State);
    }
}
