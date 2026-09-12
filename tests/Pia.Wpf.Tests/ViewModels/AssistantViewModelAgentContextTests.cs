using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Navigation;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.MeetingAttendee;
using Pia.Tests.Services;
using Pia.Tests.TestInfrastructure;
using Pia.ViewModels;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// The banner's trigger matrix and the composer gate it holds. The gate is the point: <c>Off</c> has to be a
/// click, so an unanswered banner must refuse both Send and Run-in-background.
/// </summary>
public class AssistantViewModelAgentContextTests
{
    private readonly IChatSessionManager _manager = Substitute.For<IChatSessionManager>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();

    private AssistantViewModel CreateSut()
    {
        if (SynchronizationContext.Current is null)
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());

        _settings.GetSettingsAsync().Returns(new AppSettings());

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

        return new AssistantViewModel(
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
            Substitute.For<IAssistantChatService>(),
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
            Substitute.For<IDialogService>(),
            new InlineUiDispatcher(),
            Substitute.For<IToolPermissionService>());
    }

    private static ChatSession NewSession(bool withTranscript, AgentContextMode? mode = null)
    {
        var session = new ChatSession(
            Substitute.For<ITokenMapService>(),
            Substitute.For<IAiClientService>(),
            Substitute.For<IPluginService>(),
            Substitute.For<IActionCardBuilder>(),
            Substitute.For<IToolPermissionService>(),
            Substitute.For<ILocalizationService>(),
            NullLogger.Instance,
            _ => true)
        {
            AgentContextMode = mode,
        };
        if (withTranscript)
        {
            session.Messages.Add(new AssistantMessage(ChatRole.User, "summarize the repo"));
            session.Messages.Add(new AssistantMessage(ChatRole.Assistant, "here you go"));
        }
        return session;
    }

    private ChatSession Activate(bool withTranscript, AgentContextMode? mode = null)
    {
        var session = NewSession(withTranscript, mode);
        _manager.ActiveSession.Returns(session);
        _manager.ActiveChanged += Raise.Event<EventHandler<ChatSession?>>(_manager, session);
        return session;
    }

    // ---- trigger matrix --------------------------------------------------------------------------

    [Fact]
    public void TogglingToAgent_InANonEmptyChatWithNoChoice_RaisesTheBanner()
    {
        var vm = CreateSut();
        Activate(withTranscript: true);

        vm.AgentModeEnabled = true;

        Assert.True(vm.AgentContextChoicePending);
    }

    /// <summary>The case a toggle-only trigger would miss: the lever was already on when the chat loaded.</summary>
    [Fact]
    public void LoadingAChatWithAgentModeAlreadyOn_RaisesTheBanner()
    {
        var vm = CreateSut();
        vm.AgentModeEnabled = true;

        Activate(withTranscript: true);

        Assert.True(vm.AgentContextChoicePending);
    }

    [Fact]
    public void AnEmptyChat_NeverRaisesTheBanner()
    {
        var vm = CreateSut();
        Activate(withTranscript: false);

        vm.AgentModeEnabled = true;

        Assert.False(vm.AgentContextChoicePending);
    }

    [Fact]
    public void ChatMode_NeverRaisesTheBanner()
    {
        var vm = CreateSut();

        Activate(withTranscript: true);

        Assert.False(vm.AgentContextChoicePending);
    }

    [Fact]
    public void AChatThatAlreadyRecordedAChoice_ShowsTheSettledLineInstead()
    {
        var vm = CreateSut();
        vm.AgentModeEnabled = true;

        Activate(withTranscript: true, AgentContextMode.Off);

        Assert.False(vm.AgentContextChoicePending);
        Assert.True(vm.AgentContextSettledVisible);
    }

    // ---- the gate --------------------------------------------------------------------------------

    [Fact]
    public void APendingChoice_BlocksSendAndRunInBackground()
    {
        var vm = CreateSut();
        Activate(withTranscript: true);
        vm.AgentModeEnabled = true;
        vm.InputText = "the file is not in the working folder";

        Assert.True(vm.AgentContextChoicePending);
        Assert.False(vm.SendMessageCommand.CanExecute(null));
        Assert.False(vm.RunInBackgroundCommand.CanExecute(null));
    }

    [Fact]
    public void AnsweringTheBanner_FreesSendingImmediately()
    {
        var vm = CreateSut();
        Activate(withTranscript: true);
        vm.AgentModeEnabled = true;
        vm.InputText = "the file is not in the working folder";

        vm.SetAgentContextCommand.Execute("Verbatim");

        Assert.False(vm.AgentContextChoicePending);
        Assert.True(vm.SendMessageCommand.CanExecute(null));
        Assert.True(vm.RunInBackgroundCommand.CanExecute(null));
    }

    /// <summary>Why the banner needs no "stay in chat" button: the lever is one.</summary>
    [Fact]
    public void SwitchingBackToChat_FreesSendingWithoutAnswering()
    {
        var vm = CreateSut();
        Activate(withTranscript: true);
        vm.AgentModeEnabled = true;
        vm.InputText = "the file is not in the working folder";
        Assert.False(vm.SendMessageCommand.CanExecute(null));

        vm.AgentModeEnabled = false;

        Assert.False(vm.AgentContextChoicePending);
        Assert.True(vm.SendMessageCommand.CanExecute(null));
    }

    // ---- recording the choice --------------------------------------------------------------------

    [Fact]
    public void AChoice_IsRecordedOnTheChatAndPersisted()
    {
        var vm = CreateSut();
        var session = Activate(withTranscript: true);
        vm.AgentModeEnabled = true;

        vm.SetAgentContextCommand.Execute("Summary");

        Assert.Equal(AgentContextMode.Summary, session.AgentContextMode);
        _manager.Received(1).PersistAsync(session);
    }

    [Fact]
    public void AnUnrecognizedParameter_RecordsNothing()
    {
        var vm = CreateSut();
        var session = Activate(withTranscript: true);
        vm.AgentModeEnabled = true;

        vm.SetAgentContextCommand.Execute("Telepathy");

        Assert.Null(session.AgentContextMode);
        Assert.True(vm.AgentContextChoicePending);
    }

    [Fact]
    public void Change_ReopensTheOfferAndBlocksAgain()
    {
        var vm = CreateSut();
        var session = Activate(withTranscript: true, AgentContextMode.Off);
        vm.AgentModeEnabled = true;
        vm.InputText = "go";
        Assert.True(vm.SendMessageCommand.CanExecute(null));

        vm.ChangeAgentContextCommand.Execute(null);

        Assert.Null(session.AgentContextMode);
        Assert.True(vm.AgentContextChoicePending);
        Assert.False(vm.SendMessageCommand.CanExecute(null));
    }

    // ---- the model-offered chip ------------------------------------------------------------------

    /// <summary>The chip is offered BECAUSE of the conversation, so it never stops to ask about it.</summary>
    [Fact]
    public async Task SwitchToAgent_RecordsSummaryAndDoesNotRaiseTheBanner()
    {
        var vm = CreateSut();
        var session = Activate(withTranscript: true);

        await vm.SwitchToAgentCommand.ExecuteAsync(
            new AgentModeSuggestion("summarise and file these", "multi-step task"));

        Assert.Equal(AgentContextMode.Summary, session.AgentContextMode);
        Assert.False(vm.AgentContextChoicePending);
        await _manager.Received(1).StartTurnAsync(
            session, "summarise and file these", null, Arg.Any<string?>(), planned: true);
    }
}
