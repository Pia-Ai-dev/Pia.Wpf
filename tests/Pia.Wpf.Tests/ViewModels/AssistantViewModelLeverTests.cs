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
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>The lever paths stay off the WPF dispatcher because an <c>InlineUiDispatcher</c> is injected, not
/// because <c>Application.Current</c> happens to be null — another test in this assembly creates a real one.</summary>
public class AssistantViewModelLeverTests
{
    private readonly IChatSessionManager _manager = Substitute.For<IChatSessionManager>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IProviderService _providers = Substitute.For<IProviderService>();
    private readonly IProviderCapabilityService _capability = Substitute.For<IProviderCapabilityService>();
    private readonly IPersonaService _personas = Substitute.For<IPersonaService>();
    private readonly IWorkingDirectoryService _workingDir = Substitute.For<IWorkingDirectoryService>();

    private AssistantViewModel CreateSut(IAgentRunService? runs = null)
    {
        // ChatTitleChipViewModel (built in the ctor) requires a captured SynchronizationContext.
        if (System.Threading.SynchronizationContext.Current is null)
            System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Threading.SynchronizationContext());

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
            _capability,
            runs ?? Substitute.For<IAgentRunService>(),
            Substitute.For<IAgentRunResumeService>(),
            _manager,
            _workingDir,
            Substitute.For<IFilesToolHandler>(),
            Substitute.For<IMarkdownExportService>(),
            Substitute.For<IDialogService>(),
            new InlineUiDispatcher(),
            Substitute.For<IToolPermissionService>());
    }

    private static Persona PersonaWith(PersonaToolScope scope) =>
        new() { Name = "Tester", SystemPrompt = "be helpful", ToolScope = scope };

    // ---- Default + toggle -> planned wiring -------------------------------------------------------

    [Fact]
    public void AgentModeEnabled_DefaultsToChat()
    {
        var vm = CreateSut();
        Assert.False(vm.AgentModeEnabled);
    }

    [Fact]
    public async Task Send_WithAgentModeEnabled_DispatchesPlannedTurn()
    {
        var vm = CreateSut();
        vm.ActivePersona = PersonaWith(PersonaToolScope.Full);
        vm.AgentModeEnabled = true;
        vm.InputText = "plan my week";

        await vm.SendMessageCommand.ExecuteAsync(null);

        await _manager.Received(1).StartTurnAsync(
            Arg.Any<ChatSession>(), "plan my week", Arg.Any<ImageAttachment?>(), Arg.Any<string?>(), planned: true);
    }

    [Fact]
    public async Task Send_WithAgentModeDisabled_DispatchesChatTurn()
    {
        var vm = CreateSut();
        vm.ActivePersona = PersonaWith(PersonaToolScope.Full);
        vm.AgentModeEnabled = false;
        vm.InputText = "just chat";

        await vm.SendMessageCommand.ExecuteAsync(null);

        await _manager.Received(1).StartTurnAsync(
            Arg.Any<ChatSession>(), "just chat", Arg.Any<ImageAttachment?>(), Arg.Any<string?>(), planned: false);
    }

    [Fact]
    public async Task Send_ToolScopeNone_ForcesChatEvenWhenLeverOn()
    {
        // Defence in depth: the lever UI already disables for a no-tools persona, but a stale value must never
        // plan on one.
        var vm = CreateSut();
        vm.ActivePersona = PersonaWith(PersonaToolScope.None);
        vm.AgentModeEnabled = true;
        vm.InputText = "hello";

        await vm.SendMessageCommand.ExecuteAsync(null);

        await _manager.Received(1).StartTurnAsync(
            Arg.Any<ChatSession>(), "hello", Arg.Any<ImageAttachment?>(), Arg.Any<string?>(), planned: false);
    }

    // ---- Persistence: persist-on-change, seed guard, reopen restore ------------------------------

    [Fact]
    public async Task ToggleOn_PersistsGlobalDefault()
    {
        var vm = CreateSut();
        _settings.ClearReceivedCalls();

        vm.AgentModeEnabled = true;
        await Task.Yield();

        await _settings.Received().SaveSettingsAsync(Arg.Is<AppSettings>(s => s.AssistantAgentModeDefault));
    }

    [Fact]
    public void Seed_FromSettings_DoesNotRePersist()
    {
        var vm = CreateSut();
        _settings.ClearReceivedCalls();

        // Mirrors the reopen-restore seed path via the internal seam.
        vm.SeedAgentModeFromSettings(new AppSettings { AssistantAgentModeDefault = true });

        Assert.True(vm.AgentModeEnabled);
        _settings.DidNotReceive().SaveSettingsAsync(Arg.Any<AppSettings>());
    }

    // ---- SwitchToAgent chip ----------------------------------------------------------------------

    [Fact]
    public async Task SwitchToAgent_FlipsLever_AndRedispatchesGoalAsPlanned()
    {
        var vm = CreateSut();
        vm.InputText = "unrelated draft"; // must be left untouched

        await vm.SwitchToAgentCommand.ExecuteAsync(new AgentModeSuggestion("summarise and file these", "multi-step task"));

        Assert.True(vm.AgentModeEnabled);
        Assert.Equal("unrelated draft", vm.InputText);
        await _manager.Received(1).StartTurnAsync(
            Arg.Any<ChatSession>(), "summarise and file these", null, Arg.Any<string?>(), planned: true);
    }

    [Fact]
    public async Task SwitchToAgent_NullOrEmptyGoal_NoOp()
    {
        var vm = CreateSut();

        await vm.SwitchToAgentCommand.ExecuteAsync(null);
        await vm.SwitchToAgentCommand.ExecuteAsync(new AgentModeSuggestion("   ", "reason"));

        await _manager.DidNotReceive().StartTurnAsync(
            Arg.Any<ChatSession>(), Arg.Any<string>(), Arg.Any<ImageAttachment?>(), Arg.Any<string?>(), Arg.Any<bool>());
    }

    // ---- Weak-provider banner surfaces but never blocks ------------------------------------------

    [Theory]
    [InlineData(PlanningCapability.Weak)]
    [InlineData(PlanningCapability.Unknown)]
    public async Task EvaluateProviderWarning_NonCapable_ShowsBanner(PlanningCapability cap)
    {
        var vm = CreateSut();
        var provider = new AiProvider { Name = "Local", Endpoint = "https://example.test" };
        _providers.GetDefaultProviderForModeAsync(WindowMode.Assistant).Returns(provider);
        _capability.GetPlanningCapabilityAsync(provider, Arg.Any<CancellationToken>()).Returns(cap);

        await vm.EvaluateProviderWarningAsync();

        Assert.True(vm.WeakProviderWarningVisible);
    }

    [Fact]
    public async Task EvaluateProviderWarning_Capable_ClearsBanner()
    {
        var vm = CreateSut();
        var provider = new AiProvider { Name = "Cloud", Endpoint = "https://example.test", SupportsToolCalling = true };
        _providers.GetDefaultProviderForModeAsync(WindowMode.Assistant).Returns(provider);
        _capability.GetPlanningCapabilityAsync(provider, Arg.Any<CancellationToken>()).Returns(PlanningCapability.Capable);

        await vm.EvaluateProviderWarningAsync();

        Assert.False(vm.WeakProviderWarningVisible);
    }

    // ---- Lever falls back to Chat when the active run settles ------------------------------------

    [Fact]
    public void ARunSettling_FlipsTheLeverBackToChat()
    {
        // A follow-up typed after a finished run must land in the conversation rather than mint a fresh run
        // that replaces the settled header. The inline context makes the transition observable synchronously.
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());
        var runId = Guid.NewGuid();
        var run = new AgentRun { Id = runId, State = AgentRunState.Running, Plan = [] };
        var runs = Substitute.For<IAgentRunService>();
        runs.GetAsync(runId, Arg.Any<CancellationToken>()).Returns(run);

        var vm = CreateSut(runs);
        vm.SyncRunProgress(runId);
        vm.AgentModeEnabled = true;

        run.State = AgentRunState.Completed;
        runs.RunChanged += Raise.EventWith(new AgentRunChangedEventArgs(runId, AgentRunState.Completed, null));

        Assert.False(vm.AgentModeEnabled);
    }

    [Fact]
    public void AttachingToAnAlreadySettledRun_KeepsTheLever()
    {
        // The fallback is a transition, not a state: opening a chat whose run finished long ago must not touch
        // the lever the user set.
        SynchronizationContext.SetSynchronizationContext(new InlineSyncContext());
        var runId = Guid.NewGuid();
        var run = new AgentRun { Id = runId, State = AgentRunState.Completed, Plan = [] };
        var runs = Substitute.For<IAgentRunService>();
        runs.GetAsync(runId, Arg.Any<CancellationToken>()).Returns(run);

        var vm = CreateSut(runs);
        vm.SyncRunProgress(runId);
        vm.AgentModeEnabled = true;

        runs.RunChanged += Raise.EventWith(new AgentRunChangedEventArgs(runId, AgentRunState.Completed, null));

        Assert.True(vm.AgentModeEnabled);
    }

    // ---- Send is blocked while a foreign (headless) run is executing in this chat ----

    [Fact]
    public void CanSend_IsFalse_WhileAForeignRunIsExecuting()
    {
        // A live turn here would be a second full-chat writer against a mid-run headless executor, and its
        // full replace deletes the run's step rows.
        var vm = CreateSut();
        vm.InputText = "hello";
        Assert.True(vm.SendMessageCommand.CanExecute(null));

        vm.ForeignRunActive = true;

        Assert.False(vm.SendMessageCommand.CanExecute(null));
    }

    [Fact]
    public void CanSend_ReEnables_WhenTheForeignRunStops()
    {
        var vm = CreateSut();
        vm.InputText = "hello";
        vm.ForeignRunActive = true;
        Assert.False(vm.SendMessageCommand.CanExecute(null));

        vm.ForeignRunActive = false;

        Assert.True(vm.SendMessageCommand.CanExecute(null));
    }

    [Fact]
    public void RunInBackground_Disabled_WhileAForeignRunIsExecuting()
    {
        // The button never offers more than Send.
        var vm = CreateSut();
        vm.InputText = "a different goal";
        vm.ForeignRunActive = true;

        Assert.False(vm.RunInBackgroundCommand.CanExecute(null));
    }

    [Fact]
    public void ForeignRunActive_DefaultsToFalse_SoAnOrdinaryComposerIsUnaffected()
    {
        var vm = CreateSut();
        vm.InputText = "hello";

        Assert.False(vm.ForeignRunActive);
        Assert.True(vm.SendMessageCommand.CanExecute(null));
    }

    // ---- Send is blocked while the run is parked on a plan the user has not decided on yet ----

    [Fact]
    public void CanSend_IsFalse_WhilePlanApprovalParkIsActive()
    {
        // Typing here would be neither an approval nor a rejection, so the only answers stay Approve/Reject.
        var vm = CreateSut();
        vm.InputText = "hello";
        Assert.True(vm.SendMessageCommand.CanExecute(null));

        vm.PlanApprovalParkActive = true;

        Assert.False(vm.SendMessageCommand.CanExecute(null));
    }

    [Fact]
    public void CanSend_ReEnables_WhenThePlanApprovalParkResolves()
    {
        var vm = CreateSut();
        vm.InputText = "hello";
        vm.PlanApprovalParkActive = true;
        Assert.False(vm.SendMessageCommand.CanExecute(null));

        vm.PlanApprovalParkActive = false;

        Assert.True(vm.SendMessageCommand.CanExecute(null));
    }

    [Fact]
    public void RunInBackground_Disabled_WhilePlanApprovalParkIsActive()
    {
        var vm = CreateSut();
        vm.InputText = "a different goal";
        vm.PlanApprovalParkActive = true;

        Assert.False(vm.RunInBackgroundCommand.CanExecute(null));
    }

    /// <summary>The park can land between IsStreaming clearing and the flag arriving, so Send stays enabled for
    /// a moment; a refusal in that window must not swallow what was typed.</summary>
    [Fact]
    public async Task Send_RefusedByTheManager_PutsTheTypedTextBack()
    {
        var vm = CreateSut();
        vm.InputText = "meanwhile, what is the weather";
        _manager.StartTurnAsync(Arg.Any<ChatSession>(), Arg.Any<string>(), Arg.Any<ImageAttachment?>(),
            Arg.Any<string?>(), Arg.Any<bool>()).Returns(false);

        await vm.SendMessageCommand.ExecuteAsync(null);

        Assert.Equal("meanwhile, what is the weather", vm.InputText);
    }

    [Fact]
    public async Task Send_AcceptedByTheManager_LeavesTheComposerCleared()
    {
        var vm = CreateSut();
        vm.InputText = "hello";
        _manager.StartTurnAsync(Arg.Any<ChatSession>(), Arg.Any<string>(), Arg.Any<ImageAttachment?>(),
            Arg.Any<string?>(), Arg.Any<bool>()).Returns(true);

        await vm.SendMessageCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, vm.InputText);
    }

    // ---- the ChatSession -> ViewModel wiring, and the two commands that also start live turns ----

    /// <summary>The state a restore leaves behind for a chat whose headless run is mid-flight.</summary>
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

    [Fact]
    public void AttachingASessionWithAForeignRun_SeedsTheFlagOntoTheViewModel()
    {
        // The wiring the sibling facts cannot see because they poke the property directly: without the seed on
        // attach, the composer stays enabled for the whole duration of a foreign run.
        var vm = CreateSut();
        vm.InputText = "hello";
        Assert.True(vm.SendMessageCommand.CanExecute(null));

        Activate(SessionWithTranscript(foreignRunActive: true));

        Assert.True(vm.ForeignRunActive);
        Assert.False(vm.SendMessageCommand.CanExecute(null));
    }

    [Fact]
    public void AttachingAnOrdinarySession_LeavesTheComposerEnabled()
    {
        var vm = CreateSut();
        vm.InputText = "hello";

        Activate(SessionWithTranscript(foreignRunActive: false));

        Assert.False(vm.ForeignRunActive);
        Assert.True(vm.SendMessageCommand.CanExecute(null));
    }

    [Fact]
    public void AttachingASessionParkedForPlanApproval_SeedsTheFlagOntoTheViewModel()
    {
        // The wiring the sibling facts cannot see: without the seed on attach, the composer stays live for a
        // chat re-opened onto a run that is already waiting on its plan.
        var vm = CreateSut();
        vm.InputText = "hello";
        Assert.True(vm.SendMessageCommand.CanExecute(null));

        Activate(SessionWithTranscript(planApprovalParkActive: true));

        Assert.True(vm.PlanApprovalParkActive);
        Assert.False(vm.SendMessageCommand.CanExecute(null));
    }

    [Fact]
    public async Task Regenerate_IsBlocked_WhileAPlanApprovalParkIsActive()
    {
        var vm = CreateSut();
        var session = SessionWithTranscript(planApprovalParkActive: true);
        _manager.ActiveSession.Returns(session);
        Activate(session);

        await vm.RegenerateMessageCommand.ExecuteAsync(vm.Messages[1]);

        await _manager.DidNotReceive().StartTurnAsync(
            Arg.Any<ChatSession>(), Arg.Any<string>(), Arg.Any<ImageAttachment?>(), Arg.Any<string?>(), Arg.Any<bool>());
        Assert.Equal(2, vm.Messages.Count);   // not truncated either
    }

    [Fact]
    public async Task SwitchToAgent_IsBlocked_WhileAPlanApprovalParkIsActive()
    {
        var vm = CreateSut();
        var session = SessionWithTranscript(planApprovalParkActive: true);
        _manager.ActiveSession.Returns(session);
        Activate(session);

        await vm.SwitchToAgentCommand.ExecuteAsync(new AgentModeSuggestion("plan my week", "multi-step task"));

        await _manager.DidNotReceive().StartTurnAsync(
            Arg.Any<ChatSession>(), Arg.Any<string>(), Arg.Any<ImageAttachment?>(), Arg.Any<string?>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task Regenerate_IsBlocked_WhileAForeignRunIsExecuting()
    {
        // Worse than Send: Regenerate truncates the transcript the headless run is still extending, then starts
        // a live turn whose persist full-replaces the chat.
        var vm = CreateSut();
        var session = SessionWithTranscript(foreignRunActive: true);
        _manager.ActiveSession.Returns(session);
        Activate(session);

        await vm.RegenerateMessageCommand.ExecuteAsync(vm.Messages[1]);

        await _manager.DidNotReceive().StartTurnAsync(
            Arg.Any<ChatSession>(), Arg.Any<string>(), Arg.Any<ImageAttachment?>(), Arg.Any<string?>(), Arg.Any<bool>());
        Assert.Equal(2, vm.Messages.Count);   // not truncated either
    }

    [Fact]
    public async Task Regenerate_StillWorks_WithoutAForeignRun()
    {
        // Non-vacuity: the same call goes through on an ordinary chat.
        var vm = CreateSut();
        var session = SessionWithTranscript(foreignRunActive: false);
        _manager.ActiveSession.Returns(session);
        Activate(session);

        await vm.RegenerateMessageCommand.ExecuteAsync(vm.Messages[1]);

        await _manager.Received(1).StartTurnAsync(
            session, "summarize the repo", Arg.Any<ImageAttachment?>(), Arg.Any<string?>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task SwitchToAgent_IsBlocked_WhileAForeignRunIsExecuting()
    {
        // It starts a live turn against the active chat, and would create a second Planned run in a chat that
        // already has one.
        var vm = CreateSut();
        var session = SessionWithTranscript(foreignRunActive: true);
        _manager.ActiveSession.Returns(session);
        Activate(session);

        await vm.SwitchToAgentCommand.ExecuteAsync(new AgentModeSuggestion("plan my week", "multi-step task"));

        await _manager.DidNotReceive().StartTurnAsync(
            Arg.Any<ChatSession>(), Arg.Any<string>(), Arg.Any<ImageAttachment?>(), Arg.Any<string?>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task SwitchToAgent_StillWorks_WithoutAForeignRun()
    {
        var vm = CreateSut();
        var session = SessionWithTranscript(foreignRunActive: false);
        _manager.ActiveSession.Returns(session);
        Activate(session);

        await vm.SwitchToAgentCommand.ExecuteAsync(new AgentModeSuggestion("plan my week", "multi-step task"));

        await _manager.Received(1).StartTurnAsync(
            session, "plan my week", Arg.Any<ImageAttachment?>(), Arg.Any<string?>(), planned: true);
    }

    // ---- Dispose is unsubscribe-only, so it has to unsubscribe every session event ----

    [Fact]
    public void Dispose_StopsReactingToForeignRunActiveChanged()
    {
        // The manager owns session lifetime, so the session outlives the ViewModel: any event Dispose forgets
        // leaves the dead ViewModel in the live session's invocation list for good.
        var vm = CreateSut();
        var session = SessionWithTranscript(foreignRunActive: false);
        Activate(session);
        Assert.False(vm.ForeignRunActive);

        vm.Dispose();
        session.SetForeignRunActive(true);   // false -> true, so this really does raise

        Assert.False(vm.ForeignRunActive);
    }

    [Fact]
    public void Dispose_StopsReactingToPlanApprovalParkActiveChanged()
    {
        var vm = CreateSut();
        var session = SessionWithTranscript();
        Activate(session);
        Assert.False(vm.PlanApprovalParkActive);

        vm.Dispose();
        session.SetPlanApprovalParkActive(true);   // false -> true, so this really does raise

        Assert.False(vm.PlanApprovalParkActive);
    }

    [Fact]
    public void Dispose_StopsReactingToActiveRunChanged()
    {
        var vm = CreateSut();
        var session = SessionWithTranscript(foreignRunActive: false);
        Activate(session);
        Assert.Null(vm.ActiveRunProgress);

        vm.Dispose();
        session.SetActiveRun(Guid.NewGuid());   // null -> an id, so this really does raise

        Assert.Null(vm.ActiveRunProgress);
    }
}
