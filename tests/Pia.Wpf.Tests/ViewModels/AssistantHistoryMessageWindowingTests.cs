using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Converters;
using Pia.Models;
using Pia.Navigation;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Pia.Tests.TestInfrastructure;
using Pia.ViewModels;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>The inspector is bound to the newest slice of the selected chat, and a list reload re-wraps
/// every row, so the selection changes reference even when the chat did not.</summary>
public class AssistantHistoryMessageWindowingTests
{
    private const int Window = 50;

    private readonly IAssistantChatService _chatService = Substitute.For<IAssistantChatService>();
    private readonly IProviderService _providers = Substitute.For<IProviderService>();
    private readonly IDialogService _dialog = Substitute.For<IDialogService>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly INavigationService _nav = Substitute.For<INavigationService>();
    private readonly global::Wpf.Ui.ISnackbarService _snackbar = Substitute.For<global::Wpf.Ui.ISnackbarService>();
    private readonly IChatSessionManager _sessions = Substitute.For<IChatSessionManager>();
    private readonly IMarkdownExportService _markdownExport = Substitute.For<IMarkdownExportService>();

    public AssistantHistoryMessageWindowingTests()
    {
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        _sessions.GetState(Arg.Any<Guid>()).Returns(ChatState.Idle);
    }

    [Fact]
    public async Task OpeningALongChat_RendersOnlyTheNewestWindow()
    {
        var chat = Chat(120);
        var sut = CreateSut(chat);

        await sut.RefreshCommand.ExecuteAsync(null);
        sut.SelectedChat = sut.Chats[0];

        Assert.Equal(Window, sut.VisibleChatMessages.Count);
        Assert.Equal(70, sut.OlderChatMessageCount);
        Assert.True(sut.HasOlderChatMessages);
        Assert.Equal(70, sut.SelectedChatMessages.IndexOf(sut.VisibleChatMessages[0]));
        Assert.Same(sut.SelectedChatMessages[^1], sut.VisibleChatMessages[^1]);
    }

    [Fact]
    public async Task AChatShorterThanTheWindow_ShowsNoAffordanceAtAll()
    {
        var chat = Chat(12);
        var sut = CreateSut(chat);

        await sut.RefreshCommand.ExecuteAsync(null);
        sut.SelectedChat = sut.Chats[0];

        Assert.Equal(12, sut.VisibleChatMessages.Count);
        Assert.Equal(0, sut.OlderChatMessageCount);
        Assert.False(sut.HasOlderChatMessages);
    }

    [Fact]
    public async Task LoadingOlder_PrependsOneWindowAtATime()
    {
        var chat = Chat(120);
        var sut = CreateSut(chat);

        await sut.RefreshCommand.ExecuteAsync(null);
        sut.SelectedChat = sut.Chats[0];

        sut.LoadOlderChatMessagesCommand.Execute(null);
        Assert.Equal(100, sut.VisibleChatMessages.Count);
        Assert.Equal(20, sut.OlderChatMessageCount);
        Assert.True(sut.HasOlderChatMessages);
        Assert.Equal(20, sut.SelectedChatMessages.IndexOf(sut.VisibleChatMessages[0]));

        sut.LoadOlderChatMessagesCommand.Execute(null);
        Assert.Equal(120, sut.VisibleChatMessages.Count);
        Assert.Equal(0, sut.OlderChatMessageCount);
        Assert.False(sut.HasOlderChatMessages);
        Assert.Equal(0, sut.SelectedChatMessages.IndexOf(sut.VisibleChatMessages[0]));
    }

    /// <summary>Export and the markdown writer read the loaded detail, not the window.</summary>
    [Fact]
    public async Task TheWholeTranscriptStaysAvailableBehindTheWindow()
    {
        var chat = Chat(120);
        var sut = CreateSut(chat);

        await sut.RefreshCommand.ExecuteAsync(null);
        sut.SelectedChat = sut.Chats[0];

        Assert.Equal(Window, sut.VisibleChatMessages.Count);
        Assert.Equal(120, sut.SelectedChatMessages.Count);
        Assert.Equal(120, sut.SelectedChatDetail!.Messages.Count);
    }

    [Fact]
    public async Task ClearingTheSelection_EmptiesTheWindow()
    {
        var chat = Chat(120);
        var sut = CreateSut(chat);

        await sut.RefreshCommand.ExecuteAsync(null);
        sut.SelectedChat = sut.Chats[0];
        sut.SelectedChat = null;

        Assert.Empty(sut.VisibleChatMessages);
        Assert.Empty(sut.SelectedChatMessages);
        Assert.Equal(0, sut.OlderChatMessageCount);
        Assert.False(sut.HasOlderChatMessages);
        Assert.Null(sut.SelectedChatDetail);
    }

    [Fact]
    public async Task ReloadingTheList_LeavesAnUnchangedSelectionAlone()
    {
        var chat = Chat(120);
        var sut = CreateSut(chat);

        await sut.RefreshCommand.ExecuteAsync(null);
        sut.SelectedChat = sut.Chats[0];
        sut.LoadOlderChatMessagesCommand.Execute(null);

        await sut.RefreshCommand.ExecuteAsync(null);

        await _chatService.Received(1).GetAsync(chat.Id, Arg.Any<CancellationToken>());
        Assert.Same(sut.Chats[0], sut.SelectedChat);
        Assert.NotNull(sut.SelectedChatDetail);
        Assert.Equal(100, sut.VisibleChatMessages.Count);
    }

    [Fact]
    public async Task ReloadingTheList_RefetchesAChatThatChanged()
    {
        var chat = Chat(120);
        var sut = CreateSut(chat);

        await sut.RefreshCommand.ExecuteAsync(null);
        sut.SelectedChat = sut.Chats[0];

        // A rename, a headless turn and a sync pull all land as a new UpdatedAt on the stored row.
        chat.Messages.Add(Message(121));
        chat.UpdatedAt = chat.UpdatedAt.AddMinutes(1);
        await sut.RefreshCommand.ExecuteAsync(null);

        await _chatService.Received(2).GetAsync(chat.Id, Arg.Any<CancellationToken>());
        Assert.Equal(121, sut.SelectedChatMessages.Count);
        Assert.Equal(Window, sut.VisibleChatMessages.Count);
    }

    [Fact]
    public async Task SelectingADifferentChat_LoadsIt()
    {
        var first = Chat(120);
        var second = Chat(8);
        var sut = CreateSut(first, second);

        await sut.RefreshCommand.ExecuteAsync(null);
        sut.SelectedChat = sut.Chats[0];
        sut.SelectedChat = sut.Chats[1];

        await _chatService.Received(1).GetAsync(second.Id, Arg.Any<CancellationToken>());
        Assert.Equal(8, sut.VisibleChatMessages.Count);
        Assert.False(sut.HasOlderChatMessages);
    }

    /// <summary>A missing key renders as <c>[Key]</c> rather than throwing, so only a lookup catches a typo.</summary>
    [Fact]
    public void TheLoadOlderLabel_ResolvesTheInspectorsOwnKey()
    {
        var label = (string)new OlderMessagesLabelConverter().Convert(
            7, typeof(string), "AssistantHistory_LoadOlderMessages", CultureInfo.InvariantCulture);

        Assert.DoesNotContain("[", label);
        Assert.Contains("7", label);
    }

    private AssistantHistoryViewModel CreateSut(params SyncAssistantChat[] chats)
    {
        // Inline posting plus synchronously-completed tasks make the fire-and-forget detail load run on
        // the test thread.
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());

        _chatService.SearchAsync().ReturnsForAnyArgs(
            Task.FromResult<IReadOnlyList<SyncAssistantChat>>(chats));
        _chatService.CountAsync().ReturnsForAnyArgs(Task.FromResult(chats.Length));
        _chatService.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(
            ci => Task.FromResult(chats.FirstOrDefault(c => c.Id == (Guid)ci[0])));
        _providers.GetProvidersAsync().Returns(
            Task.FromResult<IReadOnlyList<AiProvider>>(Array.Empty<AiProvider>()));

        return new AssistantHistoryViewModel(
            NullLogger<AssistantHistoryViewModel>.Instance,
            _chatService, _providers, _dialog, _loc, _nav, _snackbar, _sessions, _markdownExport,
            Substitute.For<IChatArchiveService>());
    }

    private static SyncAssistantChat Chat(int messages) => new()
    {
        Id = Guid.NewGuid(),
        Title = "imported",
        UpdatedAt = new DateTime(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc),
        Messages = [.. Enumerable.Range(1, messages).Select(Message)],
    };

    private static SyncAssistantChatMessage Message(int index) => new()
    {
        Id = Guid.NewGuid(),
        Role = index % 2 == 0 ? "assistant" : "user",
        Content = $"turn {index}",
        Timestamp = new DateTime(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc).AddSeconds(index),
    };
}
