using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Navigation;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Pia.ViewModels;
using Pia.ViewModels.Models;
using System.Threading;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>Deleting the open chat from the history view must move to a fresh chat.</summary>
public class AssistantHistoryViewModelDeleteTests
{
    private readonly IAssistantChatService _chatService = Substitute.For<IAssistantChatService>();
    private readonly IDialogService _dialog = Substitute.For<IDialogService>();
    private readonly IChatSessionManager _sessions = Substitute.For<IChatSessionManager>();
    private readonly ChatSession _freshSession = NewSession();

    private AssistantHistoryViewModel CreateSut()
    {
        if (SynchronizationContext.Current is null)
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());

        _dialog.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        _sessions.GetOrCreateActiveForNewChat().Returns(_freshSession);
        _chatService.SearchAsync().ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<SyncAssistantChat>>([]));

        return new AssistantHistoryViewModel(
            NullLogger<AssistantHistoryViewModel>.Instance,
            _chatService,
            Substitute.For<IProviderService>(),
            _dialog,
            Substitute.For<ILocalizationService>(),
            Substitute.For<INavigationService>(),
            Substitute.For<global::Wpf.Ui.ISnackbarService>(),
            _sessions,
            Substitute.For<IMarkdownExportService>());
    }

    private static ChatSession NewSession() => new(
        Substitute.For<ITokenMapService>(),
        Substitute.For<IAiClientService>(),
        Substitute.For<IPluginService>(),
        Substitute.For<IActionCardBuilder>(),
        Substitute.For<IToolPermissionService>(),
        Substitute.For<ILocalizationService>(),
        NullLogger.Instance,
        _ => true);

    private static AssistantChatRowViewModel Row(Guid id) =>
        new(new SyncAssistantChat { Id = id, Title = "Chat", UpdatedAt = DateTime.UtcNow }, ChatState.Idle);

    private ChatSession OpenSession(Guid chatId)
    {
        var session = NewSession();
        session.SetIdentity(chatId, DateTime.UtcNow, null, null, false);
        _sessions.ActiveSession.Returns(session);
        return session;
    }

    [Fact]
    public async Task DeletingTheOpenChat_StartsAFreshChat()
    {
        var chatId = Guid.NewGuid();
        OpenSession(chatId).SetWorkingDirectory("notes");
        var sut = CreateSut();

        sut.SelectedChat = Row(chatId);
        await sut.DeleteChatCommand.ExecuteAsync(null);

        _sessions.Received(1).GetOrCreateActiveForNewChat();
        // The fresh chat inherits the deleted chat's folder, like the title-chip path.
        Assert.Equal("notes", _freshSession.WorkingDirectory);
    }

    [Fact]
    public async Task DeletingABackgroundChat_KeepsTheOpenChat()
    {
        OpenSession(Guid.NewGuid());
        var sut = CreateSut();

        sut.SelectedChat = Row(Guid.NewGuid());
        await sut.DeleteChatCommand.ExecuteAsync(null);

        _sessions.DidNotReceive().GetOrCreateActiveForNewChat();
    }

    [Fact]
    public async Task QuickDeletingTheOpenChat_StartsAFreshChat()
    {
        var chatId = Guid.NewGuid();
        OpenSession(chatId);
        var sut = CreateSut();

        await sut.QuickDeleteChatCommand.ExecuteAsync(Row(chatId));

        _sessions.Received(1).GetOrCreateActiveForNewChat();
    }

    [Fact]
    public async Task DeleteAll_AbandonsTheOpenChat()
    {
        var chatId = Guid.NewGuid();
        OpenSession(chatId);
        _chatService.DeleteAllAsync(Arg.Any<CancellationToken>()).Returns([chatId]);
        var sut = CreateSut();

        await sut.DeleteAllChatsCommand.ExecuteAsync(null);

        _sessions.Received(1).GetOrCreateActiveForNewChat();
    }
}
