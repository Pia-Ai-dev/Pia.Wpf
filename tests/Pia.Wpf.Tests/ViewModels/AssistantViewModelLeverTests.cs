using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Navigation;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.MeetingAttendee;
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
/// <see cref="System.Threading.SynchronizationContext"/> (the lever paths never touch the WPF dispatcher).
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
            NullLogger<MeetingAttendeeViewModel>.Instance);

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
            _manager,
            _workingDir,
            Substitute.For<IFilesToolHandler>(),
            Substitute.For<IMarkdownExportService>(),
            Substitute.For<IDialogService>());
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
}
