using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Navigation;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.MeetingAttendee;
using Pia.Shared.Models;
using Pia.Tests.Services;
using Pia.ViewModels;
using Pia.ViewModels.Models;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.ViewModels;

public class AssistantViewModelJobComposerTests
{
    private readonly IChatSessionManager _manager = Substitute.For<IChatSessionManager>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IProviderService _providers = Substitute.For<IProviderService>();
    private readonly IProviderCapabilityService _capability = Substitute.For<IProviderCapabilityService>();
    private readonly IPersonaService _personas = Substitute.For<IPersonaService>();
    private readonly IWorkingDirectoryService _workingDir = Substitute.For<IWorkingDirectoryService>();

    private readonly IAgentRunResumeService _resume = Substitute.For<IAgentRunResumeService>();
    private readonly IAssistantChatService _chats = Substitute.For<IAssistantChatService>();
    private readonly IRunMessageRecordStore _records = Substitute.For<IRunMessageRecordStore>();
    private readonly global::Wpf.Ui.ISnackbarService _snackbar = Substitute.For<global::Wpf.Ui.ISnackbarService>();

    private AssistantViewModel CreateSut(IAgentRunService? runs = null, AppSettings? settings = null)
    {
        // ChatTitleChipViewModel (built in the ctor) requires a captured SynchronizationContext.
        if (System.Threading.SynchronizationContext.Current is null)
            System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Threading.SynchronizationContext());

        _settings.GetSettingsAsync().Returns(settings ?? new AppSettings());
        // The real manager never hands back null here, and a command that writes to the session it gets would
        // null-ref against a bare substitute.
        _manager.GetOrCreateActiveForNewChat().Returns(SessionWithTranscript());

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
            _providers,
            _personas,
            _settings,
            Substitute.For<IOutputService>(),
            Substitute.For<IPluginService>(),
            Substitute.For<IVoiceInputService>(),
            Substitute.For<ITtsService>(),
            Substitute.For<IAudioRecordingService>(),
            Substitute.For<ITranscriptionService>(),
            NullLoggerFactory.Instance,
            _snackbar,
            Substitute.For<ILocalizationService>(),
            Substitute.For<ITokenMapService>(),
            Substitute.For<IAutocompleteService>(),
            Substitute.For<INavigationService>(),
            Substitute.For<ISuggestionService>(),
            _chats,
            meeting,
            directTranscription,
            Substitute.For<IAssistantPromptComposer>(),
            _capability,
            runs ?? Substitute.For<IAgentRunService>(),
            _resume,
            _manager,
            _workingDir,
            Substitute.For<IFilesToolHandler>(),
            Substitute.For<IMarkdownExportService>(),
            Substitute.For<IDialogService>(),
            new InlineUiDispatcher(),
            Substitute.For<IToolPermissionService>(),
            runMessageRecords: _records);
    }

    private static ChatSession SessionWithTranscript(bool foreignRunActive = false, bool planApprovalParkActive = false)
    {
        var session = new ChatSession(
            Substitute.For<ITokenMapService>(),
            Substitute.For<IAiClientService>(),
            Substitute.For<IPluginService>(),
            Substitute.For<IActionCardBuilder>(),
            Substitute.For<IToolPermissionService>(),
            Substitute.For<ILocalizationService>(),
            NullLogger.Instance,
            _ => true);
        session.Messages.Add(new AssistantMessage(Microsoft.Extensions.AI.ChatRole.User, "summarize the repo"));
        session.Messages.Add(new AssistantMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "here you go"));
        if (foreignRunActive)
            session.SetForeignRunActive(true);
        if (planApprovalParkActive)
            session.SetPlanApprovalParkActive(true);
        return session;
    }

    private void Activate(ChatSession session) =>
        _manager.ActiveChanged += Raise.Event<EventHandler<ChatSession?>>(_manager, session);

    private async Task<AssistantViewModel> AttachRunAsync(bool settingOn, bool chatJob, AgentRunState state)
    {
        System.Threading.SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());
        var runId = Guid.NewGuid();
        var runs = Substitute.For<IAgentRunService>();
        runs.GetAsync(runId, Arg.Any<CancellationToken>()).Returns(new AgentRun
        {
            Id = runId,
            State = state,
            PolicyJson = chatJob ? HeadlessRunLauncher.ChatJobEmptyEnvelopeJson : null,
        });
        var vm = CreateSut(runs, new AppSettings { UnifiedAssistantFlowEnabled = settingOn });
        vm.InputText = "how is it going?";
        var session = SessionWithTranscript();
        session.SetActiveRun(runId);
        session.SetForeignRunActive(true);
        Activate(session);
        await vm.RefreshChatJobComposerAsync();
        return vm;
    }

    [Theory]
    [InlineData(true, true, AgentRunState.Running, true)]
    [InlineData(true, true, AgentRunState.Verifying, true)]
    [InlineData(true, false, AgentRunState.Running, false)]
    [InlineData(false, true, AgentRunState.Running, false)]
    [InlineData(false, false, AgentRunState.Running, false)]
    [InlineData(true, true, AgentRunState.WaitingForInput, false)]
    public async Task Send_IsOpenBesideARunningChatJob_OnlyWithTheSettingOn(
        bool settingOn, bool chatJob, AgentRunState state, bool open)
    {
        var vm = await AttachRunAsync(settingOn, chatJob, state);

        Assert.True(vm.ForeignRunActive);
        Assert.Equal(open, vm.ChatJobComposerOpen);
        Assert.Equal(open, vm.SendMessageCommand.CanExecute(null));
        Assert.Equal(!open, vm.ShowRunActiveHint);
    }

    [Fact]
    public async Task RunInBackground_StaysLocked_BesideARunningChatJob()
    {
        var vm = await AttachRunAsync(true, true, AgentRunState.Running);
        Assert.False(vm.RunInBackgroundCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheComposerLocksAgain_WhenTheJobStopsExecuting()
    {
        var vm = await AttachRunAsync(true, true, AgentRunState.Running);
        vm.ForeignRunActive = false;
        await vm.RefreshChatJobComposerAsync();

        Assert.False(vm.ChatJobComposerOpen);
    }

    [Fact]
    public async Task SwitchingToAnotherChatWithARunningRoutine_LocksTheComposer()
    {
        var vm = await AttachRunAsync(true, true, AgentRunState.Running);
        Assert.True(vm.ChatJobComposerOpen);

        var other = SessionWithTranscript();
        other.SetActiveRun(Guid.NewGuid());
        other.SetForeignRunActive(true);
        Activate(other);

        Assert.False(vm.ChatJobComposerOpen);
        Assert.False(vm.SendMessageCommand.CanExecute(null));
    }
}
