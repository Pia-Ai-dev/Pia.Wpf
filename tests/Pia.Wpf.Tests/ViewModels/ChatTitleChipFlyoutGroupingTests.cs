using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Pia.ViewModels;
using Pia.ViewModels.Models;
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
    public void StateMode_OrdersBuckets_ActionNeededFirst()
    {
        var now = DateTime.UtcNow;
        var chats = new List<SyncAssistantChat>
        {
            Chat("idle", now, ChatState.Idle),
            Chat("completed", now, ChatState.Completed),
            Chat("error", now, ChatState.Error),
            Chat("running", now, ChatState.Running),
            Chat("waiting", now, ChatState.WaitingForTool),
        };
        var sut = CreateSut(chats);

        sut.IsFlyoutOpen = true;
        sut.GroupMode = ChatGroupMode.State;

        var keys = sut.Groups.Select(g => g.DisplayName).ToArray();
        Assert.Equal(
            new[]
            {
                "ChatState_Group_WaitingForTool",
                "ChatState_Group_Running",
                "ChatState_Group_Error",
                "ChatState_Group_Completed",
                "ChatState_Group_Idle",
            },
            keys);
    }

    [Fact]
    public void StateMode_PersistedNotLive_ResolvesToIdle()
    {
        var now = DateTime.UtcNow;
        // No state registered for this chat -> _resolveState returns Idle.
        var chats = new List<SyncAssistantChat> { Chat("persisted", now) };
        var sut = CreateSut(chats);

        sut.IsFlyoutOpen = true;
        sut.GroupMode = ChatGroupMode.State;

        var group = Assert.Single(sut.Groups);
        Assert.Equal("ChatState_Group_Idle", group.DisplayName);
    }

    [Fact]
    public void StateMode_WithinBucket_SortsByUpdatedAtDesc()
    {
        var older = Chat("older", DateTime.UtcNow.AddHours(-2), ChatState.Running);
        var newer = Chat("newer", DateTime.UtcNow, ChatState.Running);
        var sut = CreateSut(new List<SyncAssistantChat> { older, newer });

        sut.IsFlyoutOpen = true;
        sut.GroupMode = ChatGroupMode.State;

        var group = Assert.Single(sut.Groups);
        Assert.Equal(newer.Id, group.Items[0].Id);
        Assert.Equal(older.Id, group.Items[1].Id);
    }

    [Fact]
    public void DateMode_IsDefault_AndGroupsByDate()
    {
        var chats = new List<SyncAssistantChat>
        {
            Chat("today", DateTime.UtcNow),
            Chat("older", DateTime.UtcNow.AddDays(-10)),
        };
        var sut = CreateSut(chats);

        Assert.Equal(ChatGroupMode.Date, sut.GroupMode);

        sut.IsFlyoutOpen = true;

        var keys = sut.Groups.Select(g => g.DisplayName).ToArray();
        Assert.Equal(new[] { "History_Group_Today", "History_Group_Older" }, keys);
    }

    [Fact]
    public void ToggleBackToDate_RebuildsFromSnapshot_NoRefetch()
    {
        var chats = new List<SyncAssistantChat>
        {
            Chat("today", DateTime.UtcNow, ChatState.Running),
            Chat("older", DateTime.UtcNow.AddDays(-10), ChatState.Idle),
        };
        var sut = CreateSut(chats);

        sut.IsFlyoutOpen = true;
        sut.GroupMode = ChatGroupMode.State;
        sut.GroupMode = ChatGroupMode.Date;

        var keys = sut.Groups.Select(g => g.DisplayName).ToArray();
        Assert.Equal(new[] { "History_Group_Today", "History_Group_Older" }, keys);

        // Only LoadRecentChatsAsync hits SearchAsync; toggling re-groups from the snapshot.
        // ReceivedWithAnyArgs ignores the argument; the token is passed only to satisfy xUnit1051.
        _chatService.ReceivedWithAnyArgs(1).SearchAsync(ct: TestContext.Current.CancellationToken);
    }
}
