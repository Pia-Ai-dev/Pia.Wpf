using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Navigation;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.MeetingAttendee;
using Pia.Tests.Services;
using Pia.ViewModels;
using Pia.ViewModels.Models;
using System.Threading;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>Deleting the open chat from the title-chip flyout must move to a fresh chat.</summary>
public class AssistantViewModelChipDeleteTests
{
    private readonly IChatSessionManager _manager = Substitute.For<IChatSessionManager>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IAssistantChatService _chatService = Substitute.For<IAssistantChatService>();
    private readonly IDialogService _dialog = Substitute.For<IDialogService>();
    private readonly ChatSession _freshSession = NewSession();

    private AssistantViewModel CreateSut()
    {
        // ChatTitleChipViewModel (built in the ctor) requires a captured SynchronizationContext.
        if (SynchronizationContext.Current is null)
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());

        _settings.GetSettingsAsync().Returns(new AppSettings());
        _manager.GetOrCreateActiveForNewChat().Returns(_freshSession);
        _dialog.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        var meeting = new MeetingAttendeeViewModel(
            Substitute.For<IMeetingAttendeeService>(),
            _settings,
            Substitute.For<ILocalizationService>(),
            Substitute.For<IFileDialogService>(),
            Substitute.For<IDialogService>(),
            Substitute.For<IMemoryService>(),
            Substitute.For<IIngestScheduler>(),
            Substitute.For<Wpf.Ui.ISnackbarService>(),
            NullLogger<MeetingAttendeeViewModel>.Instance,
            new InlineUiDispatcher());

        var directTranscription = new DirectTranscriptionViewModel(
            Substitute.For<IDirectTranscriptionService>(),
            _settings,
            Substitute.For<ILocalizationService>(),
            Substitute.For<IFileDialogService>(),
            Substitute.For<IDialogService>(),
            Substitute.For<IMemoryService>(),
            Substitute.For<IIngestScheduler>(),
            Substitute.For<Wpf.Ui.ISnackbarService>(),
            NullLogger<DirectTranscriptionViewModel>.Instance,
            new InlineUiDispatcher());

        var vm = new AssistantViewModel(
            NullLogger<AssistantViewModel>.Instance,
            Substitute.For<IAiClientService>(),
            Substitute.For<IProviderService>(),
            Substitute.For<IPersonaService>(),
            _settings,
            Substitute.For<IOutputService>(),
            Substitute.For<IPluginService>(),
            Substitute.For<IVoiceInputService>(),
            Substitute.For<ITtsService>(),
            Substitute.For<IAudioRecordingService>(),
            Substitute.For<ITranscriptionService>(),
            NullLoggerFactory.Instance,
            Substitute.For<global::Wpf.Ui.ISnackbarService>(),
            Substitute.For<ILocalizationService>(),
            Substitute.For<ITokenMapService>(),
            Substitute.For<IAutocompleteService>(),
            Substitute.For<INavigationService>(),
            Substitute.For<ISuggestionService>(),
            _chatService,
            meeting,
            directTranscription,
            Substitute.For<IAssistantPromptComposer>(),
            Substitute.For<IProviderCapabilityService>(),
            Substitute.For<IAgentRunService>(),
            Substitute.For<IAgentRunResumeService>(),
            _manager,
            Substitute.For<IWorkingDirectoryService>(),
            Substitute.For<IFilesToolHandler>(),
            Substitute.For<IMarkdownExportService>(),
            _dialog,
            new InlineUiDispatcher(),
            Substitute.For<IToolPermissionService>());

        // Drop ctor calls (initial GetOrCreateActiveForNewChat) before counting.
        _manager.ClearReceivedCalls();
        return vm;
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

    private static ChatChipItemViewModel ChipItem(Guid id) =>
        new(id, "Chat", DateTime.UtcNow, ChatState.Idle);

    private static Task DeleteAsync(AssistantViewModel vm, Guid id) =>
        vm.ChatTitleChip.DeleteChatCommand.ExecuteAsync(ChipItem(id));

    [Fact]
    public async Task DeletingTheOpenChat_StartsAFreshChat()
    {
        var chatId = Guid.NewGuid();
        var openSession = NewSession();
        openSession.SetIdentity(chatId, DateTime.UtcNow, null, null, false);
        openSession.SetWorkingDirectory("notes");
        _manager.ActiveSession.Returns(openSession);
        var vm = CreateSut();

        await DeleteAsync(vm, chatId);

        await _chatService.Received(1).DeleteAsync(chatId, Arg.Any<CancellationToken>());
        _manager.Received(1).GetOrCreateActiveForNewChat();
        // The fresh chat inherits the deleted chat's folder, like Clear conversation.
        Assert.Equal("notes", _freshSession.WorkingDirectory);
    }

    [Fact]
    public async Task DeletingFromTheComposer_DeletesTheOpenChatAndStartsAFresh()
    {
        var chatId = Guid.NewGuid();
        var openSession = NewSession();
        openSession.SetIdentity(chatId, DateTime.UtcNow, null, null, false);
        openSession.SetWorkingDirectory("notes");
        _manager.ActiveSession.Returns(openSession);
        var vm = CreateSut();

        await vm.DeleteCurrentChatCommand.ExecuteAsync(null);

        await _chatService.Received(1).DeleteAsync(chatId, Arg.Any<CancellationToken>());
        _manager.Received(1).GetOrCreateActiveForNewChat();
        Assert.Equal("notes", _freshSession.WorkingDirectory);
    }

    [Fact]
    public void TheComposerDeleteButton_TracksWhetherTheChatHasAnythingToDelete()
    {
        var openSession = NewSession();
        openSession.SetIdentity(Guid.NewGuid(), DateTime.UtcNow, null, null, false);
        _manager.ActiveSession.Returns(openSession);
        var vm = CreateSut();
        var refreshes = 0;
        vm.DeleteCurrentChatCommand.CanExecuteChanged += (_, _) => refreshes++;

        Assert.False(vm.DeleteCurrentChatCommand.CanExecute(null));

        vm.HasMessages = true;

        Assert.True(vm.DeleteCurrentChatCommand.CanExecute(null));
        Assert.True(refreshes > 0, "the button must re-enable itself when the chat gains its first message");
    }

    [Fact]
    public async Task DeletingABackgroundChat_KeepsTheOpenChat()
    {
        var chatId = Guid.NewGuid();
        var openSession = NewSession();
        openSession.SetIdentity(Guid.NewGuid(), DateTime.UtcNow, null, null, false);
        _manager.ActiveSession.Returns(openSession);
        var vm = CreateSut();

        await DeleteAsync(vm, chatId);

        await _chatService.Received(1).DeleteAsync(chatId, Arg.Any<CancellationToken>());
        _manager.DidNotReceive().GetOrCreateActiveForNewChat();
    }

    [Fact]
    public async Task DecliningTheConfirmation_DeletesNothing()
    {
        var chatId = Guid.NewGuid();
        var openSession = NewSession();
        openSession.SetIdentity(chatId, DateTime.UtcNow, null, null, false);
        _manager.ActiveSession.Returns(openSession);
        var vm = CreateSut();
        _dialog.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(false);

        await DeleteAsync(vm, chatId);

        await _chatService.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        _manager.DidNotReceive().GetOrCreateActiveForNewChat();
    }
}
