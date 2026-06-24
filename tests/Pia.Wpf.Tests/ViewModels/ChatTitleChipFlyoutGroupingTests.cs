using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
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

    // Captured chip callbacks: the dir the next "+ New Chat" was pinned to, the dir offered to
    // the active chat re-point, and the (test-controlled) active chat's working dir.
    private string? _capturedNewChatDir = "<unset>";
    private string? _capturedSetActiveDir = "<unset>";
    private string? _activeWorkingDir;

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
            dir => _capturedNewChatDir = dir,
            () => { },
            id => _states.TryGetValue(id, out var s) ? s : ChatState.Idle,
            Substitute.For<IWorkingDirectoryService>(),
            dir => _capturedSetActiveDir = dir,
            () => _activeWorkingDir);
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
    public void Picking_Folder_UpdatesPill_AndOffersRepointToActiveChat()
    {
        var sut = CreateSut([]);
        sut.IsFlyoutOpen = true;   // seeds the pending folder from the active chat (root here)
        sut.IsPickerOpen = true;   // initializes the picker at the pending folder

        sut.WorkingDirectoryPicker.EnterCommand.Execute("projects");

        // The chip offers the re-point to its owner (which applies it only to an un-started
        // chat) and reflects the pick on the pill.
        Assert.Equal("projects", _capturedSetActiveDir);
        Assert.Equal("\\projects", sut.WorkingDirectoryDisplay);
        Assert.False(sut.IsWorkingDirectoryRoot);
    }

    [Fact]
    public void NewChat_PinsToPickedFolder_NotActiveChatDir()
    {
        // Active chat is at the root; the user picks a different folder for the new chat.
        _activeWorkingDir = null;
        var sut = CreateSut([]);
        sut.IsFlyoutOpen = true;
        sut.IsPickerOpen = true;
        sut.WorkingDirectoryPicker.EnterCommand.Execute("projects");

        sut.NewChatCommand.Execute(null);

        Assert.Equal("projects", _capturedNewChatDir);
    }

    [Fact]
    public void FlyoutOpen_SeedsPillFromActiveChatDir()
    {
        _activeWorkingDir = "src/app";
        var sut = CreateSut([]);

        sut.IsFlyoutOpen = true;

        Assert.Equal("\\src\\app", sut.WorkingDirectoryDisplay);
        Assert.False(sut.IsWorkingDirectoryRoot);
    }

    [Fact]
    public void FlyoutOpen_AtRoot_ResetsPillToHome()
    {
        // Put the pill in a non-root state first, so the root assertion can't pass on the
        // field-initializer defaults — it must come from the flyout-open reseed.
        _activeWorkingDir = "projects";
        var sut = CreateSut([]);
        sut.IsFlyoutOpen = true;
        Assert.Equal("\\projects", sut.WorkingDirectoryDisplay);

        // Re-opening with a root active chat must reset the pill back to home.
        sut.IsFlyoutOpen = false;
        _activeWorkingDir = null;
        sut.IsFlyoutOpen = true;

        Assert.Equal("\\", sut.WorkingDirectoryDisplay);
        Assert.True(sut.IsWorkingDirectoryRoot);
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
