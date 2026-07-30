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
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// Covers the 1.3 Chat/Agent lever wiring (14.5 / R8 / R10 / R15) at the <see cref="AssistantViewModel"/>
/// level: the lever resolves the <c>planned</c> flag threaded into <see cref="IChatSessionManager.StartTurnAsync"/>,
/// the ToolScope==None defence-in-depth guard forces Chat, the global default persists on change but not on
/// seed, reopen restores it, <c>SwitchToAgent</c> re-dispatches a goal as Planned, and the Weak-provider
/// banner surfaces without ever blocking. The VM is constructed off any thread with a stub
/// <see cref="System.Threading.SynchronizationContext"/>, and the lever paths never touch the WPF
/// dispatcher — since Batch 12 that holds because an <c>InlineUiDispatcher</c> is injected, not because
/// <c>Application.Current</c> happens to be null (<c>AssistantViewParseTests</c> creates a real one).
/// </summary>
public class AssistantViewModelLeverTests
{
    private readonly IChatSessionManager _manager = Substitute.For<IChatSessionManager>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IProviderService _providers = Substitute.For<IProviderService>();
    private readonly IProviderCapabilityService _capability = Substitute.For<IProviderCapabilityService>();
    private readonly IPersonaService _personas = Substitute.For<IPersonaService>();
    private readonly IWorkingDirectoryService _workingDir = Substitute.For<IWorkingDirectoryService>();

    private AssistantViewModel CreateSut()
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
            NullLogger<MeetingAttendeeViewModel>.Instance,
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
            Substitute.For<IAssistantPromptComposer>(),
            _capability,
            Substitute.For<IAgentRunService>(),
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
        // Defence in depth (the lever UI already disables for a no-tools persona): a stale lever value
        // must never plan on a persona that can't use tools.
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

        // Mirrors the LoadPersonasAsync seed path (reopen restore) via the internal seam.
        vm.SeedAgentModeFromSettings(new AppSettings { AssistantAgentModeDefault = true });

        Assert.True(vm.AgentModeEnabled);                       // reopen restores the persisted value
        _settings.DidNotReceive().SaveSettingsAsync(Arg.Any<AppSettings>()); // guard: seed never re-persists
    }

    // ---- SwitchToAgent chip (R8) -----------------------------------------------------------------

    [Fact]
    public async Task SwitchToAgent_FlipsLever_AndRedispatchesGoalAsPlanned()
    {
        var vm = CreateSut();
        vm.InputText = "unrelated draft"; // must be left untouched (composer round-trip preserved)

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

    // ---- Weak-provider banner surfaces but never blocks (R10) ------------------------------------

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

    // ---- W2c: Send is blocked while a FOREIGN (headless) run is executing in this chat ----

    [Fact]
    public void CanSend_IsFalse_WhileAForeignRunIsExecuting()
    {
        // The data-loss guard: a live turn here would be a SECOND full-chat writer against a headless
        // executor that is mid-run, and its full replace deletes the run's step rows. The composer's
        // Assistant_BackgroundRunActive_Hint line explains the disabled Send in words; this fact pins the gate.
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
    public void RunInBackground_StaysEnabled_WhileAForeignRunIsExecuting()
    {
        // "Run in background" launches into a NEW chat id and never writes this chat, so it is not a second
        // writer and must not be gated.
        var vm = CreateSut();
        vm.InputText = "a different goal";
        vm.ForeignRunActive = true;

        Assert.True(vm.RunInBackgroundCommand.CanExecute(null));
    }

    [Fact]
    public void ForeignRunActive_DefaultsToFalse_SoAnOrdinaryComposerIsUnaffected()
    {
        var vm = CreateSut();
        vm.InputText = "hello";

        Assert.False(vm.ForeignRunActive);
        Assert.True(vm.SendMessageCommand.CanExecute(null));
    }

    // ---- W2c: the ChatSession -> ViewModel wiring, and the two commands that also start live turns ----

    /// <summary>
    /// A session with a two-message transcript, optionally already flagged as having a foreign run
    /// executing — the state <c>ChatSessionManager.RestoreActiveRunAsync</c> leaves behind for a chat whose
    /// headless run is mid-flight.
    /// </summary>
    private static ChatSession SessionWithTranscript(bool foreignRunActive)
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
        return session;
    }

    private void Activate(ChatSession session) =>
        _manager.ActiveChanged += Raise.Event<EventHandler<ChatSession?>>(_manager, session);

    [Fact]
    public void AttachingASessionWithAForeignRun_SeedsTheFlagOntoTheViewModel()
    {
        // The wiring the other W2c view-model tests cannot see because they poke the property directly:
        // delete `ForeignRunActive = session.ForeignRunActive` from AttachToActiveSession and the composer
        // stays enabled for the whole duration of a foreign run with no red test anywhere.
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
    public async Task Regenerate_IsBlocked_WhileAForeignRunIsExecuting()
    {
        // Regenerate had only an IsStreaming guard, so it was an open door through the W2c lever — and a
        // worse one than Send: it TRUNCATES the transcript the headless run is still extending, then starts a
        // live turn whose persist full-replaces the chat.
        var vm = CreateSut();
        var session = SessionWithTranscript(foreignRunActive: true);
        _manager.ActiveSession.Returns(session);
        Activate(session);

        await vm.RegenerateMessageCommand.ExecuteAsync(vm.Messages[1]);

        await _manager.DidNotReceive().StartTurnAsync(
            Arg.Any<ChatSession>(), Arg.Any<string>(), Arg.Any<ImageAttachment?>(), Arg.Any<string?>(), Arg.Any<bool>());
        Assert.Equal(2, vm.Messages.Count);   // the transcript was not truncated either
    }

    [Fact]
    public async Task Regenerate_StillWorks_WithoutAForeignRun()
    {
        // The guard must not be vacuous: the same call goes through on an ordinary chat.
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
        // Modeled on Send, so it needs Send's lever: it starts a live turn against the ACTIVE chat and would
        // additionally create a second Planned run in a chat that already has one.
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

    // ---- Dispose is unsubscribe-only, so it has to unsubscribe EVERY session event ----

    [Fact]
    public void Dispose_StopsReactingToForeignRunActiveChanged()
    {
        // The manager owns session lifetime, so the session outlives the ViewModel - that is exactly what
        // lets Assistant -> History -> Assistant not kill a running turn. Any event Dispose forgets
        // therefore keeps the dead ViewModel in the live session's invocation list for good (one leaked
        // ViewModel per round-trip) and its handler still runs on every foreign-run flip.
        var vm = CreateSut();
        var session = SessionWithTranscript(foreignRunActive: false);
        Activate(session);
        Assert.False(vm.ForeignRunActive);

        vm.Dispose();
        session.SetForeignRunActive(true);   // false -> true, so this really does raise (the setter no-ops on equal)

        Assert.False(vm.ForeignRunActive);
    }

    [Fact]
    public void Dispose_StopsReactingToActiveRunChanged()
    {
        // Regression guard for the sibling event, which Dispose already unsubscribes: this one is green
        // today, and goes red if that unsubscribe is ever removed.
        var vm = CreateSut();
        var session = SessionWithTranscript(foreignRunActive: false);
        Activate(session);
        Assert.Null(vm.ActiveRunProgress);   // attaching a run-less session leaves the panel unembedded

        vm.Dispose();
        session.SetActiveRun(Guid.NewGuid());   // null -> an id, so this really does raise

        Assert.Null(vm.ActiveRunProgress);
    }
}
