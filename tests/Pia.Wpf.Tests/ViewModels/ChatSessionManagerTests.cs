using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Shared.Models;
using Pia.ViewModels.Models;
using Xunit;

namespace Pia.Tests.ViewModels;

public class ChatSessionManagerTests
{
    private readonly IAssistantChatService _chatService = Substitute.For<IAssistantChatService>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IPersonaService _personas = Substitute.For<IPersonaService>();
    private readonly IProviderService _providers = Substitute.For<IProviderService>();
    private readonly IAssistantPromptComposer _composer = Substitute.For<IAssistantPromptComposer>();
    private readonly IChatTitleService _titleService = Substitute.For<IChatTitleService>();
    private readonly IActionCardBuilder _cards = Substitute.For<IActionCardBuilder>();
    private readonly IPluginService _plugins = Substitute.For<IPluginService>();
    private readonly IAiClientService _ai = Substitute.For<IAiClientService>();
    private readonly ILocalizationService _loc = Substitute.For<ILocalizationService>();
    private readonly ITokenMapService _tokenMap = Substitute.For<ITokenMapService>();
    private readonly IBackgroundChatNotifier _notifier = Substitute.For<IBackgroundChatNotifier>();
    private readonly Pia.Services.Flow.IFlowService _flow = Substitute.For<Pia.Services.Flow.IFlowService>();
    private readonly IToolPermissionService _permissions = Substitute.For<IToolPermissionService>();
    private readonly IFilesToolHandler _files = Substitute.For<IFilesToolHandler>();
    private readonly IAgentRunService _runService = Substitute.For<IAgentRunService>();
    private readonly IProviderCapabilityService _capability = Substitute.For<IProviderCapabilityService>();
    private readonly IHeadlessRunLauncher _headlessLauncher = Substitute.For<IHeadlessRunLauncher>();
    private readonly IWindowManagerService _windowManager = Substitute.For<IWindowManagerService>();

    /// <summary>
    /// The REAL A2 launch-bracket index: these tests assert through it, not around it. Fully qualified
    /// because this file deliberately has no `using Pia.Services;` (see AgentRunOrchestrator below).
    /// </summary>
    private readonly Pia.Services.ExecutingRunStore _executingRuns = new();

    public ChatSessionManagerTests()
    {
        _settings.GetSettingsAsync().Returns(new AppSettings());
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        _capability.GetPlanningCapabilityAsync(Arg.Any<AiProvider>(), Arg.Any<CancellationToken>())
            .Returns(PlanningCapability.Capable);
    }

    /// <summary>
    /// Runs every posted callback INLINE, on the posting thread, in order.
    /// <para>
    /// A bare <see cref="SynchronizationContext"/> forwards <c>Post</c> to the ThreadPool, which guarantees
    /// NO ordering — so two <c>RunChanged</c> events raised in quick succession could have their handlers
    /// execute out of order, and a test that raises "terminal, then Running" would observe the Running
    /// recompute first and settle on the wrong final state. That produced a real intermittent failure in
    /// <c>RunChanged_OwnRunTerminal_RetiresOwnership</c> (~1 run in 6 under full-suite load), which is a
    /// FIXTURE defect, not a product one: in production these events are separated by real work, and the
    /// handler is invoked on the WPF dispatcher, which is ordered.
    /// </para>
    /// <para>
    /// Inline is the faithful choice rather than merely the convenient one — it is exactly what the WPF
    /// dispatcher does when a post originates on the UI thread, which is the case these tests model. It also
    /// makes the handler's effects observable immediately after the raise, so a test never has to sleep to
    /// find out whether bookkeeping ran.
    /// </para>
    /// </summary>
    private sealed class InlineSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);

        public override void Send(SendOrPostCallback d, object? state) => d(state);
    }

    private ChatSessionManager CreateSut()
    {
        // Set unconditionally, not only when null: xunit reuses pool threads, so a context left behind by an
        // earlier test would otherwise be inherited and reintroduce the reordering this replaces.
        SynchronizationContext.SetSynchronizationContext(new InlineSynchronizationContext());

        var orchestrator = new Pia.Services.AgentRunOrchestrator(
            _runService, Substitute.For<Pia.Services.Interfaces.IAgentPlanner>(),
            new Pia.Tests.Services.FakeVerifier(),
            NullLogger<Pia.Services.AgentRunOrchestrator>.Instance);

        return new ChatSessionManager(
            NullLogger<ChatSessionManager>.Instance,
            NullLoggerFactory.Instance,
            _chatService, _settings, _personas, _providers, _composer,
            _titleService, _cards, _plugins, _ai, _permissions, _loc,
            () => _tokenMap, _notifier, _flow, _files, orchestrator, _runService, _capability,
            _headlessLauncher, _windowManager, _executingRuns);
    }

    /// <summary>
    /// The same manager, plus Batch 06 D4's two moving parts: the workspace provisioner it must call for an
    /// interactive <c>Planned</c> run, and a planner that captures the <c>RunContext</c> the orchestrator
    /// builds — the only place the root the manager provisioned becomes observable from out here.
    /// <para>
    /// Deliberately a SECOND builder rather than two defaulted parameters on <see cref="CreateSut"/>: that one
    /// passes the ctor positionally and omits every trailing optional, and keeping it that way is what proves
    /// the new ctor parameter is source-compatible with the hand-constructed production call sites.
    /// </para>
    /// </summary>
    private ChatSessionManager CreateIsolatingSut(
        Pia.Services.Interfaces.IRunWorkspaceService workspaces,
        Pia.Services.Interfaces.IAgentPlanner planner)
    {
        SynchronizationContext.SetSynchronizationContext(new InlineSynchronizationContext());

        var orchestrator = new Pia.Services.AgentRunOrchestrator(
            _runService, planner,
            new Pia.Tests.Services.FakeVerifier(),
            NullLogger<Pia.Services.AgentRunOrchestrator>.Instance);

        return new ChatSessionManager(
            NullLogger<ChatSessionManager>.Instance,
            NullLoggerFactory.Instance,
            _chatService, _settings, _personas, _providers, _composer,
            _titleService, _cards, _plugins, _ai, _permissions, _loc,
            () => _tokenMap, _notifier, _flow, _files, orchestrator, _runService, _capability,
            _headlessLauncher, _windowManager, _executingRuns,
            agentTimelineService: null, workspaces: workspaces);
    }

    [Fact]
    public void GetOrCreateActiveForNewChat_SetsActiveSession()
    {
        var sut = CreateSut();
        var session = sut.GetOrCreateActiveForNewChat();

        Assert.Same(session, sut.ActiveSession);
        Assert.Null(session.Id);
        Assert.Equal(ChatState.Idle, session.State);
    }

    [Fact]
    public async Task ActivateAsync_NoLiveSession_LoadsFromStore_AndTouches()
    {
        var chatId = Guid.NewGuid();
        var stored = new SyncAssistantChat
        {
            Id = chatId,
            SchemaVersion = 1,
            Title = "Stored chat",
            CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTime.UtcNow,
            LastAccessedAt = DateTime.UtcNow,
            WindowMode = WindowMode.Assistant.ToString(),
            Messages =
            [
                new SyncAssistantChatMessage { Id = Guid.NewGuid(), Role = "user", Content = "hello", Timestamp = DateTime.UtcNow },
                new SyncAssistantChatMessage { Id = Guid.NewGuid(), Role = "assistant", Content = "hi there", Timestamp = DateTime.UtcNow },
            ],
        };
        _chatService.GetAsync(chatId, Arg.Any<CancellationToken>()).Returns(stored);

        var sut = CreateSut();
        var session = await sut.ActivateAsync(chatId);

        Assert.NotNull(session);
        Assert.Equal(chatId, session!.Id);
        Assert.Equal(2, session.Messages.Count);
        Assert.Same(session, sut.ActiveSession);
        await _chatService.Received(1).TouchLastAccessedAsync(chatId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ActivateAsync_MissingChat_ReturnsNull()
    {
        var chatId = Guid.NewGuid();
        _chatService.GetAsync(chatId, Arg.Any<CancellationToken>()).Returns((SyncAssistantChat?)null);

        var sut = CreateSut();
        var session = await sut.ActivateAsync(chatId);

        Assert.Null(session);
    }

    /// <summary>
    /// Drive a real interactive Planned launch and hand back the <see cref="AgentRunCreateRequest"/> the
    /// manager built, so the persisted envelope can be inspected. Shared by the two facts below: they assert on
    /// the two independent authority channels of the same document.
    /// </summary>
    private async Task<AgentRunCreateRequest> CapturePlannedRunRequestAsync(AppSettings? appSettings = null)
    {
        if (appSettings is not null)
            _settings.GetSettingsAsync().Returns(appSettings);

        var sut = CreateSut();
        var session = sut.GetOrCreateActiveForNewChat();

        var persona = new Persona { Name = "Tester", SystemPrompt = "be helpful" };
        _personas.ResolveActiveAsync(Arg.Any<WindowMode>(), Arg.Any<UserOperatingMode>()).Returns(persona);
        var provider = new AiProvider { Id = Guid.NewGuid(), Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI };
        _providers.GetDefaultProviderForModeAsync(Arg.Any<WindowMode>()).Returns(provider);
        _composer.PrepareTurn(Arg.Any<Persona>(), Arg.Any<AiProvider>(), Arg.Any<IReadOnlyList<AtCommand>>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(new AssistantTurnSetup("system", null, SupportsTools: false, WebSearchActive: false));

        AgentRunCreateRequest? captured = null;
        _runService.CreateAsync(Arg.Any<AgentRunCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                captured = ci.Arg<AgentRunCreateRequest>();
                return Task.FromResult(new AgentRun
                {
                    Id = Guid.NewGuid(),
                    ChatId = captured.ChatId,
                    RunShape = RunShape.Planned,
                    State = AgentRunState.Planning,
                    Goal = captured.Goal,
                });
            });

        await sut.StartPlannedTurnAsync(session, "do the thing");

        Assert.NotNull(captured);
        return captured!;
    }

    /// <summary>
    /// The envelope's SECOND authority channel (04 D10/D11). The name of the next fact promises "parking
    /// cannot widen authority" but its body only inspects the grant LIST, and a resume honours the policy too —
    /// so with the setting on, a parked run resumes with the Files-covering preset even though its grant list is
    /// empty. That is D10 working as designed (the launch itself carried the policy), but it has to be stated,
    /// and it is the only assertion that fails if <c>ChatSessionManager</c>'s <c>SerializeGrantEnvelope(...,
    /// policy)</c> argument is dropped — it is optional and defaulted, so dropping it compiles.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartPlannedTurn_PersistsTheSettingsPolicyInTheEnvelope(bool settingOn)
    {
        var captured = await CapturePlannedRunRequestAsync(
            new AppSettings { AgentRunAutoApproveBuiltInWrites = settingOn });

        var policy = Pia.Services.HeadlessRunLauncher.TryRestorePolicy(captured.PolicyJson);

        if (!settingOn)
        {
            // Off ⇒ NO policy member at all, so the document stays byte-identical to a pre-Batch-04 one.
            Assert.Null(policy);
            return;
        }

        Assert.NotNull(policy);
        Assert.True(policy!.Covers(ToolClass.Files));
        Assert.True(policy.Covers(ToolClass.Memory));
        // Git and External are excluded from the preset: git_switch/git_restore/git_stash shed uncommitted work
        // without being delete-like, and a class grant on External would cover an MCP server's NEXT tool.
        Assert.False(policy.Covers(ToolClass.Git));
        Assert.False(policy.Covers(ToolClass.External));

        // The grant list is still empty — the two channels are independent.
        Assert.Empty(Pia.Services.HeadlessRunLauncher.TryRestoreGrantEnvelope(captured.PolicyJson)!);
    }

    [Fact]
    public async Task StartPlannedTurn_PersistsAnEmptyGrantEnvelope_SoParkingCannotWidenAuthority()
    {
        // D1's producer half for the INTERACTIVE origin. An interactive run holds no standing write grant:
        // write_file is not auto-approve eligible, so every write raises an action card the user clicks. A
        // resume, though, runs UNATTENDED through HeadlessRunLauncher, and a run whose PolicyJson is null
        // falls back to the {write_file} resume floor — so parking would ESCALATE the run to card-free
        // writes with nobody watching. The create must persist the honoured-EMPTY envelope instead.
        //
        // Scope: this covers the grant LIST only. The policy channel of the same document is
        // StartPlannedTurn_PersistsTheSettingsPolicyInTheEnvelope, above.
        var captured = await CapturePlannedRunRequestAsync();

        var restored = Pia.Services.HeadlessRunLauncher.TryRestoreGrantEnvelope(captured.PolicyJson);
        Assert.NotNull(restored);  // an envelope IS written — null would resume on the {write_file} floor
        Assert.Empty(restored!);   // and it grants nothing, which is exactly what the launch granted
    }

    // ---- C2: a parked run must stay reachable after a restart (ActiveRunId is runtime-only) ----

    private static AgentRun Run(Guid chatId, AgentRunState state, DateTime createdAt, RunShape shape = RunShape.Planned) =>
        new() { Id = Guid.NewGuid(), ChatId = chatId, RunShape = shape, State = state, CreatedAt = createdAt };

    private SyncAssistantChat StoredChat(Guid chatId) => new()
    {
        Id = chatId,
        SchemaVersion = 1,
        Title = "Stored chat",
        CreatedAt = DateTime.UtcNow.AddMinutes(-5),
        UpdatedAt = DateTime.UtcNow,
        LastAccessedAt = DateTime.UtcNow,
        WindowMode = WindowMode.Assistant.ToString(),
        Messages =
        [
            new SyncAssistantChatMessage { Id = Guid.NewGuid(), Role = "user", Content = "plan my week", Timestamp = DateTime.UtcNow },
        ],
    };

    [Fact]
    public async Task ActivateAsync_ChatWithParkedRun_RestoresTheRunPanel()
    {
        // The headline C2 case: after an app restart the run is durable but was unreachable — no panel, no
        // Continue button, and the Flow WaitingForInput card is suppressed for the foreground active chat.
        var chatId = Guid.NewGuid();
        var parked = Run(chatId, AgentRunState.WaitingForInput, DateTime.UtcNow.AddMinutes(-2));
        _chatService.GetAsync(chatId, Arg.Any<CancellationToken>()).Returns(StoredChat(chatId));
        _runService.GetByChatAsync(chatId, Arg.Any<CancellationToken>()).Returns(new List<AgentRun> { parked });

        var sut = CreateSut();
        var session = await sut.ActivateAsync(chatId);
        Assert.NotNull(session);
        Guid? raised = null;
        session!.ActiveRunChanged += (_, id) => raised = id;

        // The lookup is fire-and-forget off the UI thread (an activation must never stall on it), so wait
        // for it rather than assuming it already landed.
        for (var i = 0; i < 200 && session.ActiveRunId is null; i++)
            await Task.Delay(10, TestContext.Current.CancellationToken);

        Assert.Equal(parked.Id, session.ActiveRunId);
        // Late subscribers still get the panel: AssistantViewModel reads ActiveRunId when it attaches.
        Assert.True(raised is null || raised == parked.Id);
    }

    // ---- W2c: a re-attached, still-EXECUTING run is a foreign writer, so the composer must be blocked ----

    [Theory]
    [InlineData(AgentRunState.Planning)]
    [InlineData(AgentRunState.Running)]
    [InlineData(AgentRunState.Verifying)]
    // Batch 07 G8: a parent parked awaiting its children IS live — its children are writing its workspace and
    // it will write this chat again the moment they settle — unlike the parked WaitingForInput/Paused rows in
    // the theory below, whose "continue in chat" path must stay open. It also has to be re-attachable at all:
    // without it in the re-attach scan the panel would not find the run.
    [InlineData(AgentRunState.WaitingForChildren)]
    public async Task RestoreActiveRunAsync_ExecutingRun_MarksTheSessionsRunForeign(AgentRunState state)
    {
        // A hydrated session never executes its own run — this manager did not launch it. So a re-attached run
        // that is still executing is by definition a SECOND full-chat writer, and the live composer must not
        // start a turn against it (a live full replace would delete the run's step rows).
        var chatId = Guid.NewGuid();
        var executing = Run(chatId, state, DateTime.UtcNow.AddMinutes(-1));
        _chatService.GetAsync(chatId, Arg.Any<CancellationToken>()).Returns(StoredChat(chatId));
        _runService.GetByChatAsync(chatId, Arg.Any<CancellationToken>()).Returns(new List<AgentRun> { executing });

        var sut = CreateSut();
        var session = await sut.ActivateAsync(chatId);
        session!.SetActiveRun(null);

        await sut.RestoreActiveRunAsync(session);

        Assert.Equal(executing.Id, session.ActiveRunId);
        Assert.True(session.ForeignRunActive);
    }

    [Theory]
    [InlineData(AgentRunState.WaitingForInput)]
    [InlineData(AgentRunState.Paused)]
    public async Task RestoreActiveRunAsync_ParkedRun_DoesNotMarkItForeign(AgentRunState state)
    {
        // The parked "continue in chat" path must stay OPEN: a parked run is not writing, and blocking Send
        // there would break the headline C2 case (resume the run by talking to the chat).
        var chatId = Guid.NewGuid();
        var parked = Run(chatId, state, DateTime.UtcNow.AddMinutes(-1));
        _chatService.GetAsync(chatId, Arg.Any<CancellationToken>()).Returns(StoredChat(chatId));
        _runService.GetByChatAsync(chatId, Arg.Any<CancellationToken>()).Returns(new List<AgentRun> { parked });

        var sut = CreateSut();
        var session = await sut.ActivateAsync(chatId);
        session!.SetActiveRun(null);

        await sut.RestoreActiveRunAsync(session);

        Assert.Equal(parked.Id, session.ActiveRunId);
        Assert.False(session.ForeignRunActive);
    }

    [Fact]
    public async Task RunChanged_ToPaused_ClearsTheForeignFlag()
    {
        var chatId = Guid.NewGuid();
        var running = Run(chatId, AgentRunState.Running, DateTime.UtcNow.AddMinutes(-1));
        _chatService.GetAsync(chatId, Arg.Any<CancellationToken>()).Returns(StoredChat(chatId));
        _runService.GetByChatAsync(chatId, Arg.Any<CancellationToken>()).Returns(new List<AgentRun> { running });

        var sut = CreateSut();
        var session = await sut.ActivateAsync(chatId);
        session!.SetActiveRun(null);
        await sut.RestoreActiveRunAsync(session);
        Assert.True(session.ForeignRunActive);

        // AgentRunService raises RunChanged from a pool thread; the manager marshals the flip (G3), so poll.
        _runService.RunChanged += Raise.EventWith(new AgentRunChangedEventArgs(running.Id, AgentRunState.Paused));

        for (var i = 0; i < 200 && session.ForeignRunActive; i++)
            await Task.Delay(10, TestContext.Current.CancellationToken);

        Assert.False(session.ForeignRunActive);
    }

    [Fact]
    public async Task RunChanged_BackToRunning_SetsTheForeignFlagAgain()
    {
        var chatId = Guid.NewGuid();
        var parked = Run(chatId, AgentRunState.WaitingForInput, DateTime.UtcNow.AddMinutes(-1));
        _chatService.GetAsync(chatId, Arg.Any<CancellationToken>()).Returns(StoredChat(chatId));
        _runService.GetByChatAsync(chatId, Arg.Any<CancellationToken>()).Returns(new List<AgentRun> { parked });

        var sut = CreateSut();
        var session = await sut.ActivateAsync(chatId);
        session!.SetActiveRun(null);
        await sut.RestoreActiveRunAsync(session);
        Assert.False(session.ForeignRunActive);

        // The user hit Continue on the Flow card: the run resumes HEADLESSLY, so it becomes a writer again.
        _runService.RunChanged += Raise.EventWith(new AgentRunChangedEventArgs(parked.Id, AgentRunState.Running));

        for (var i = 0; i < 200 && !session.ForeignRunActive; i++)
            await Task.Delay(10, TestContext.Current.CancellationToken);

        Assert.True(session.ForeignRunActive);
    }

    [Fact]
    public async Task RunChanged_ForAnInteractiveRunThisManagerLaunched_NeverMarksItForeign()
    {
        // No interactive regression: the session that OWNS the run executes it itself, and its own IsStreaming
        // already blocks Send. Flagging it would disable the composer for every ordinary agent run.
        var provider = new AiProvider { Id = Guid.NewGuid(), Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI };
        _providers.GetDefaultProviderForModeAsync(Arg.Any<WindowMode>()).Returns(provider);
        _personas.ResolveActiveAsync(Arg.Any<WindowMode>(), Arg.Any<UserOperatingMode>())
            .Returns(new Persona { Name = "Pia", SystemPrompt = "sys" });
        _composer.PrepareTurn(Arg.Any<Persona>(), Arg.Any<AiProvider>(), Arg.Any<IReadOnlyList<AtCommand>>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(new AssistantTurnSetup("system", null, SupportsTools: false, WebSearchActive: false));
        var runId = Guid.NewGuid();
        _runService.CreateAsync(Arg.Any<AgentRunCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AgentRun { Id = runId, RunShape = RunShape.Planned, State = AgentRunState.Planning });

        var sut = CreateSut();
        var session = sut.GetOrCreateActiveForNewChat();
        await sut.StartPlannedTurnAsync(session, "plan my week");

        Assert.Equal(runId, session.ActiveRunId);

        // The orchestrator's per-step RunChanged events fire for THIS run; none may flag the session.
        _runService.RunChanged += Raise.EventWith(new AgentRunChangedEventArgs(runId, AgentRunState.Running));
        _runService.RunChanged += Raise.EventWith(new AgentRunChangedEventArgs(runId, AgentRunState.Verifying));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.False(session.ForeignRunActive);
    }

    /// <summary>
    /// Batch 07 G8, <b>REGRESSION</b>. A parent that parks at <c>WaitingForChildren</c> must keep its ownership
    /// exemption. Read as non-executing, the state retires this manager's <c>_ownRunIds</c> entry the instant the
    /// run delegates — and then the very next executing event (the un-park CAS's <c>RunChanged(Running)</c>, or
    /// any per-step one after it) treats the run as a FOREIGN full-chat writer on its own session: composer
    /// blocked, Send disabled, and no later event to correct it, because ownership is never re-granted.
    /// <para>
    /// The two events must be raised in this order and both asserted; the park event alone flags nothing, so a
    /// test that stopped there would pass on the broken build.
    /// </para>
    /// Neutralize: drop <c>WaitingForChildren</c> from the <c>executing</c> set in <c>OnAgentRunChanged</c>.
    /// </summary>
    [Fact]
    public async Task RunChanged_OwnRunAwaitingChildren_KeepsItsOwnershipExemption()
    {
        var provider = new AiProvider { Id = Guid.NewGuid(), Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI };
        _providers.GetDefaultProviderForModeAsync(Arg.Any<WindowMode>()).Returns(provider);
        _personas.ResolveActiveAsync(Arg.Any<WindowMode>(), Arg.Any<UserOperatingMode>())
            .Returns(new Persona { Name = "Pia", SystemPrompt = "sys" });
        _composer.PrepareTurn(Arg.Any<Persona>(), Arg.Any<AiProvider>(), Arg.Any<IReadOnlyList<AtCommand>>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(new AssistantTurnSetup("system", null, SupportsTools: false, WebSearchActive: false));
        var runId = Guid.NewGuid();
        _runService.CreateAsync(Arg.Any<AgentRunCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AgentRun { Id = runId, RunShape = RunShape.Planned, State = AgentRunState.Planning });

        var sut = CreateSut();
        var session = sut.GetOrCreateActiveForNewChat();
        await sut.StartPlannedTurnAsync(session, "plan my week");

        // The parent delegates (parks), then the un-park CAS brings it back. Neither may flag its own session.
        _runService.RunChanged += Raise.EventWith(new AgentRunChangedEventArgs(runId, AgentRunState.WaitingForChildren));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(session.ForeignRunActive);

        _runService.RunChanged += Raise.EventWith(new AgentRunChangedEventArgs(runId, AgentRunState.Running));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(session.ForeignRunActive);
    }

    [Fact]
    public async Task RunChanged_OwnRunParkedThenResumedHeadlessly_IsFlaggedForeign()
    {
        // The two-writer path the product hits most: the user launches an agent run interactively, it parks at
        // its step budget, IsStreaming goes false and the composer comes back — and then Continue resumes it
        // through HeadlessRunLauncher, unattended, against the same chat. Ownership must NOT survive the park,
        // or Send stays enabled and the live turn's full replace deletes every row the resumed run wrote.
        var provider = new AiProvider { Id = Guid.NewGuid(), Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI };
        _providers.GetDefaultProviderForModeAsync(Arg.Any<WindowMode>()).Returns(provider);
        _personas.ResolveActiveAsync(Arg.Any<WindowMode>(), Arg.Any<UserOperatingMode>())
            .Returns(new Persona { Name = "Pia", SystemPrompt = "sys" });
        _composer.PrepareTurn(Arg.Any<Persona>(), Arg.Any<AiProvider>(), Arg.Any<IReadOnlyList<AtCommand>>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(new AssistantTurnSetup("system", null, SupportsTools: false, WebSearchActive: false));
        var runId = Guid.NewGuid();
        _runService.CreateAsync(Arg.Any<AgentRunCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AgentRun { Id = runId, RunShape = RunShape.Planned, State = AgentRunState.Planning });

        var sut = CreateSut();
        var session = sut.GetOrCreateActiveForNewChat();
        await sut.StartPlannedTurnAsync(session, "plan my week");
        Assert.Equal(runId, session.ActiveRunId);

        // Still ours while it runs — no interactive regression.
        _runService.RunChanged += Raise.EventWith(new AgentRunChangedEventArgs(runId, AgentRunState.Running));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(session.ForeignRunActive);

        // Parked at the budget: the live executor released the session and handed the run back.
        _runService.RunChanged += Raise.EventWith(new AgentRunChangedEventArgs(runId, AgentRunState.WaitingForInput));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(session.ForeignRunActive);   // parked is not writing — Continue-in-chat stays open

        // Continue -> HeadlessRunLauncher.ResumeAsync -> TryBeginResumeAsync CAS'd it to Running.
        _runService.RunChanged += Raise.EventWith(new AgentRunChangedEventArgs(runId, AgentRunState.Running));
        for (var i = 0; i < 200 && !session.ForeignRunActive; i++)
            await Task.Delay(10, TestContext.Current.CancellationToken);

        Assert.True(session.ForeignRunActive);
    }

    [Theory]
    [InlineData(AgentRunState.Completed)]
    [InlineData(AgentRunState.Failed)]
    [InlineData(AgentRunState.Cancelled)]
    public async Task RunChanged_OwnRunTerminal_RetiresOwnership(AgentRunState terminal)
    {
        // Same retirement on the terminal states (which is also what keeps _ownRunIds from growing for the
        // lifetime of the process). A run id can only be re-used by a re-attach/resume, so treating a
        // post-terminal executing state as foreign is the safe reading.
        var provider = new AiProvider { Id = Guid.NewGuid(), Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI };
        _providers.GetDefaultProviderForModeAsync(Arg.Any<WindowMode>()).Returns(provider);
        _personas.ResolveActiveAsync(Arg.Any<WindowMode>(), Arg.Any<UserOperatingMode>())
            .Returns(new Persona { Name = "Pia", SystemPrompt = "sys" });
        _composer.PrepareTurn(Arg.Any<Persona>(), Arg.Any<AiProvider>(), Arg.Any<IReadOnlyList<AtCommand>>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(new AssistantTurnSetup("system", null, SupportsTools: false, WebSearchActive: false));
        var runId = Guid.NewGuid();
        _runService.CreateAsync(Arg.Any<AgentRunCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AgentRun { Id = runId, RunShape = RunShape.Planned, State = AgentRunState.Planning });

        var sut = CreateSut();
        var session = sut.GetOrCreateActiveForNewChat();
        await sut.StartPlannedTurnAsync(session, "plan my week");

        _runService.RunChanged += Raise.EventWith(new AgentRunChangedEventArgs(runId, terminal));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(session.ForeignRunActive);

        _runService.RunChanged += Raise.EventWith(new AgentRunChangedEventArgs(runId, AgentRunState.Running));
        for (var i = 0; i < 200 && !session.ForeignRunActive; i++)
            await Task.Delay(10, TestContext.Current.CancellationToken);

        Assert.True(session.ForeignRunActive);
    }

    [Fact]
    public async Task RunChanged_WithNoMatchingSession_IsHarmless()
    {
        // Guardrail 1: RunChanged is raised by AgentRunService on the write path. The handler is bookkeeping —
        // an unknown run id, or a disposed manager, must not throw back into the run.
        var sut = CreateSut();
        sut.GetOrCreateActiveForNewChat();

        _runService.RunChanged += Raise.EventWith(new AgentRunChangedEventArgs(Guid.NewGuid(), AgentRunState.Running));
        await Task.Delay(30, TestContext.Current.CancellationToken);

        sut.Dispose();
        // After Dispose the handler is detached; raising again must still be inert.
        _runService.RunChanged += Raise.EventWith(new AgentRunChangedEventArgs(Guid.NewGuid(), AgentRunState.Running));
        await Task.Delay(30, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RestoreActiveRunAsync_OnlyTerminalRuns_RestoresNothing()
    {
        var chatId = Guid.NewGuid();
        _chatService.GetAsync(chatId, Arg.Any<CancellationToken>()).Returns(StoredChat(chatId));
        _runService.GetByChatAsync(chatId, Arg.Any<CancellationToken>()).Returns(new List<AgentRun>
        {
            Run(chatId, AgentRunState.Completed, DateTime.UtcNow.AddMinutes(-9)),
            Run(chatId, AgentRunState.Failed, DateTime.UtcNow.AddMinutes(-6)),
            Run(chatId, AgentRunState.Cancelled, DateTime.UtcNow.AddMinutes(-3)),
        });

        var sut = CreateSut();
        var session = await sut.ActivateAsync(chatId);

        await sut.RestoreActiveRunAsync(session!);

        Assert.Null(session!.ActiveRunId); // a finished run must never resurrect a panel
    }

    [Fact]
    public async Task RestoreActiveRunAsync_PicksTheNewestNonTerminalPlannedRun()
    {
        var chatId = Guid.NewGuid();
        var older = Run(chatId, AgentRunState.WaitingForInput, DateTime.UtcNow.AddMinutes(-10));
        var newest = Run(chatId, AgentRunState.Paused, DateTime.UtcNow.AddMinutes(-1));
        _chatService.GetAsync(chatId, Arg.Any<CancellationToken>()).Returns(StoredChat(chatId));
        _runService.GetByChatAsync(chatId, Arg.Any<CancellationToken>()).Returns(new List<AgentRun>
        {
            older,
            Run(chatId, AgentRunState.Completed, DateTime.UtcNow),                              // terminal → ignored
            Run(chatId, AgentRunState.Running, DateTime.UtcNow, RunShape.SingleTurn),           // not Planned → ignored
            newest,
        });

        var sut = CreateSut();
        var session = await sut.ActivateAsync(chatId);
        session!.SetActiveRun(null); // ignore whatever the activation's own fire-and-forget lookup did

        await sut.RestoreActiveRunAsync(session);

        Assert.Equal(newest.Id, session.ActiveRunId);
    }

    [Fact]
    public async Task RestoreActiveRunAsync_SessionAlreadyHasARun_DoesNotQueryOrReplaceIt()
    {
        var sut = CreateSut();
        var session = sut.GetOrCreateActiveForNewChat();
        session.Messages.Add(new AssistantMessage(Microsoft.Extensions.AI.ChatRole.User, "hi"));
        await sut.PersistAsync(session); // assigns the chat id
        var liveRunId = Guid.NewGuid();
        session.SetActiveRun(liveRunId);
        _runService.ClearReceivedCalls();

        await sut.RestoreActiveRunAsync(session);

        Assert.Equal(liveRunId, session.ActiveRunId);
        await _runService.DidNotReceive().GetByChatAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RestoreActiveRunAsync_LookupThrows_LeavesTheChatUsable()
    {
        // Guardrail 1: the rehydration query is bookkeeping — a fault must not fail the activation.
        var chatId = Guid.NewGuid();
        _chatService.GetAsync(chatId, Arg.Any<CancellationToken>()).Returns(StoredChat(chatId));
        _runService.GetByChatAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<AgentRun>>>(_ => throw new InvalidOperationException("runs boom"));

        var sut = CreateSut();
        var session = await sut.ActivateAsync(chatId); // a rethrow here fails the test

        Assert.NotNull(session);
        Assert.Single(session!.Messages);

        await sut.RestoreActiveRunAsync(session); // awaited directly: still must not throw
        Assert.Null(session.ActiveRunId);
    }

    [Fact]
    public async Task RestoreActiveRunAsync_UnpersistedSession_DoesNothing()
    {
        var sut = CreateSut();
        var session = sut.GetOrCreateActiveForNewChat(); // Id still null → nothing to look up

        await sut.RestoreActiveRunAsync(session);

        Assert.Null(session.ActiveRunId);
        await _runService.DidNotReceive().GetByChatAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void GetState_UnknownChat_ReturnsIdle()
    {
        var sut = CreateSut();
        Assert.Equal(ChatState.Idle, sut.GetState(Guid.NewGuid()));
    }

    [Fact]
    public async Task PersistAsync_AssignsId_AndSaves()
    {
        var sut = CreateSut();
        var session = sut.GetOrCreateActiveForNewChat();
        session.Messages.Add(new AssistantMessage(Microsoft.Extensions.AI.ChatRole.User, "remember this"));
        session.Messages.Add(new AssistantMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "ok"));

        await sut.PersistAsync(session);

        Assert.NotNull(session.Id);
        await _chatService.Received(1).SaveAsync(Arg.Is<SyncAssistantChat>(c => c.Id == session.Id), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersistRequested_SavesTheTranscript_WithoutTurnCompletedOrAutoTitle()
    {
        // E2 (interactive per-step durability): the live executor asks for a persist after each completed
        // step. The manager must save — so a budget pause or a crash cannot lose the step replies — while
        // staying NON-terminal: no TurnCompleted, no terminal state, and no auto-title (that stays with the
        // terminal persist, so titling behaviour is unchanged and the rename never read-modify-writes a
        // chat that is still growing).
        _settings.GetSettingsAsync().Returns(new AppSettings { ChatAutoTitleEnabled = true });
        var sut = CreateSut();
        var session = sut.GetOrCreateActiveForNewChat();
        session.Messages.Add(new AssistantMessage(Microsoft.Extensions.AI.ChatRole.User, "plan my week"));
        session.Messages.Add(new AssistantMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "step 1 done"));
        _chatService.ClearReceivedCalls();
        var turnCompleted = 0;
        session.TurnCompleted += (_, _) => turnCompleted++;

        session.RequestPersist();

        await _chatService.Received(1).SaveAsync(
            Arg.Is<SyncAssistantChat>(c => c.Messages.Count == 2), Arg.Any<CancellationToken>());
        await _titleService.DidNotReceive().GenerateAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Equal(0, turnCompleted);
        Assert.Equal(ChatState.Idle, session.State);
        Assert.NotNull(session.Id); // the save assigned/kept the chat identity, as the terminal persist does
    }

    [Fact]
    public async Task TurnCompleted_StillStartsAutoTitle_AfterAnInterimPersist()
    {
        // The contrast to the test above: suppressing auto-title is scoped to the interim persist only —
        // the terminal path still triggers it (unchanged behaviour).
        _settings.GetSettingsAsync().Returns(new AppSettings { ChatAutoTitleEnabled = true });
        var sut = CreateSut();
        var session = sut.GetOrCreateActiveForNewChat();
        session.Messages.Add(new AssistantMessage(Microsoft.Extensions.AI.ChatRole.User, "plan my week"));
        session.Messages.Add(new AssistantMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "step 1 done"));

        session.RequestPersist();
        session.RaiseTurnCompleted(new TurnCompletedEventArgs { Succeeded = true });

        await _titleService.Received(1).GenerateAsync(
            "plan my week", "step 1 done", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AutoTitle_WritesTheTitleOnly_AndNeverFullReplacesTheChat()
    {
        // W2: the auto-title rename used to be GetAsync -> mutate Title -> SaveAsync, a fire-and-forget
        // read-modify-write. Its DB snapshot is routinely stale by the time it lands (the title LLM call sits
        // in the middle), so it could revert message rows a headless step appended in between — a second
        // effective writer on the chat row. It must now issue exactly one SetTitleAsync and NO SaveAsync.
        _settings.GetSettingsAsync().Returns(new AppSettings { ChatAutoTitleEnabled = true });
        _titleService.GenerateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("Weekly plan");
        _chatService.SetTitleAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        var sut = CreateSut();
        var session = sut.GetOrCreateActiveForNewChat();
        session.Messages.Add(new AssistantMessage(Microsoft.Extensions.AI.ChatRole.User, "plan my week"));
        session.Messages.Add(new AssistantMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "step 1 done"));

        session.RaiseTurnCompleted(new TurnCompletedEventArgs { Succeeded = true });

        // Terminal persist + rename are both fire-and-forget; wait for the rename to land.
        for (var i = 0; i < 200 && session.Title != "Weekly plan"; i++)
            await Task.Delay(10, TestContext.Current.CancellationToken);

        Assert.Equal("Weekly plan", session.Title);
        await _chatService.Received(1).SetTitleAsync(session.Id!.Value, "Weekly plan", Arg.Any<CancellationToken>());
        // EXACTLY ONE full replace for the whole turn — the terminal persist's. Before W2 the rename added a
        // second one, from a snapshot read before the title LLM call.
        await _chatService.Received(1).SaveAsync(Arg.Any<SyncAssistantChat>(), Arg.Any<CancellationToken>());
        // And it no longer READS the chat back: that read existed only to carry the message payload.
        await _chatService.DidNotReceive().GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AutoTitle_ChatDeletedBeforeTheRename_KeepsTheSessionTitleUnchanged()
    {
        // The zero-row update IS the "chat disappeared before rename" case; SetTitleAsync reports it as false
        // (the store owns no logger) and the session title must not be moved.
        _settings.GetSettingsAsync().Returns(new AppSettings { ChatAutoTitleEnabled = true });
        _titleService.GenerateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("Weekly plan");
        _chatService.SetTitleAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        var sut = CreateSut();
        var session = sut.GetOrCreateActiveForNewChat();
        session.Messages.Add(new AssistantMessage(Microsoft.Extensions.AI.ChatRole.User, "plan my week"));
        session.Messages.Add(new AssistantMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "step 1 done"));

        session.RaiseTurnCompleted(new TurnCompletedEventArgs { Succeeded = true });

        for (var i = 0; i < 200; i++)
        {
            if (_chatService.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IAssistantChatService.SetTitleAsync)))
                break;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        await _chatService.Received(1).SetTitleAsync(session.Id!.Value, "Weekly plan", Arg.Any<CancellationToken>());
        // The terminal persist's derived title stands; the LLM title is NOT applied to a chat that is gone.
        Assert.Equal("plan my week", session.Title);
    }

    [Fact]
    public async Task ActivateAsync_LiveSession_ReturnsSameInstance_WithoutLoadingOrCancelling()
    {
        var sut = CreateSut();

        // Make a live, persisted session, then switch away so it becomes background.
        var live = sut.GetOrCreateActiveForNewChat();
        live.Messages.Add(new AssistantMessage(Microsoft.Extensions.AI.ChatRole.User, "hi"));
        await sut.PersistAsync(live);
        var chatId = live.Id!.Value;
        _chatService.ClearReceivedCalls();

        sut.GetOrCreateActiveForNewChat(); // a different active session

        var attached = await sut.ActivateAsync(chatId);

        Assert.Same(live, attached);                  // live-attach, not a reload
        Assert.Same(live, sut.ActiveSession);
        await _chatService.DidNotReceive().GetAsync(chatId, Arg.Any<CancellationToken>());
        await _chatService.DidNotReceive().TouchLastAccessedAsync(chatId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BackgroundSession_SurfaceWorthyState_RoutesToNotifier()
    {
        var sut = CreateSut();

        var background = sut.GetOrCreateActiveForNewChat();
        background.Messages.Add(new AssistantMessage(Microsoft.Extensions.AI.ChatRole.User, "hi"));
        await sut.PersistAsync(background);
        var chatId = background.Id!.Value;

        sut.GetOrCreateActiveForNewChat(); // switch active away → background is non-active
        _notifier.ClearReceivedCalls();

        background.SetState(ChatState.WaitingForTool);

        _notifier.Received(1).NotifyStateChange(chatId, Arg.Any<string>(), ChatState.WaitingForTool);
    }

    [Fact]
    public async Task BackgroundSession_RunningOrIdle_DoesNotNotify()
    {
        var sut = CreateSut();

        var background = sut.GetOrCreateActiveForNewChat();
        background.Messages.Add(new AssistantMessage(Microsoft.Extensions.AI.ChatRole.User, "hi"));
        await sut.PersistAsync(background);

        sut.GetOrCreateActiveForNewChat();
        _notifier.ClearReceivedCalls();

        background.SetState(ChatState.Running);
        background.SetState(ChatState.Idle);

        _notifier.DidNotReceive().NotifyStateChange(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<ChatState>());
    }

    [Fact]
    public async Task ActiveSession_SurfaceWorthyState_DoesNotNotify()
    {
        var sut = CreateSut();

        var active = sut.GetOrCreateActiveForNewChat();
        active.Messages.Add(new AssistantMessage(Microsoft.Extensions.AI.ChatRole.User, "hi"));
        await sut.PersistAsync(active);
        _notifier.ClearReceivedCalls();

        // active stays the ActiveSession — its state changes must NOT notify.
        active.SetState(ChatState.WaitingForTool);

        _notifier.DidNotReceive().NotifyStateChange(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<ChatState>());
    }

    [Fact]
    public async Task FirstTurn_BackgroundSession_SurfaceWorthyState_RoutesToNotifier_BeforeAnyPersist()
    {
        // Regression: a brand-new chat backgrounded mid-first-turn must still notify.
        // Before the fix the session Id was null until the end-of-turn persist, so the
        // notifier-routing gate (session.Id is { } chatId) silently dropped the toast.
        // (Under C4 the failed background turn is now also persisted by
        // FinalizeFailedSetupAsync — but the notifier still fires first, on SetState(Error),
        // before that persist runs.)
        var sut = CreateSut();

        var background = sut.GetOrCreateActiveForNewChat(); // first turn, Id still null
        sut.GetOrCreateActiveForNewChat();                  // switch active away → background is non-active
        _notifier.ClearReceivedCalls();

        var persona = new Persona { Name = "Tester", SystemPrompt = "be helpful" };
        _personas.ResolveActiveAsync(Arg.Any<WindowMode>(), Arg.Any<UserOperatingMode>()).Returns(persona);
        // Leave _providers unmocked → no provider → StartTurnAsync's early Error return
        // (deterministic: no RunTurnAsync, no fire-and-forget, no streaming mock needed).

        await sut.StartTurnAsync(background, "hi", null);

        Assert.NotNull(background.Id);
        Assert.Equal(ChatState.Error, background.State);
        _notifier.Received(1).NotifyStateChange(background.Id!.Value, Arg.Any<string>(), ChatState.Error);
    }

    [Fact]
    public void SetActive_ClearsCompletedToIdle()
    {
        var sut = CreateSut();

        var session = sut.GetOrCreateActiveForNewChat();
        sut.GetOrCreateActiveForNewChat(); // switch away
        session.SetState(ChatState.Completed);

        sut.SetActive(session); // activating an unread-completed chat marks it read

        Assert.Equal(ChatState.Idle, session.State);
    }

    [Fact]
    public async Task StartTurnAsync_NoProvider_BackgroundSession_PersistsForRecovery()
    {
        var sut = CreateSut();

        var background = sut.GetOrCreateActiveForNewChat();
        sut.GetOrCreateActiveForNewChat(); // switch active away → background is non-active
        _chatService.ClearReceivedCalls();

        var persona = new Persona { Name = "Tester", SystemPrompt = "be helpful" };
        _personas.ResolveActiveAsync(Arg.Any<WindowMode>(), Arg.Any<UserOperatingMode>()).Returns(persona);
        // No provider mocked → no-provider Error path.

        await sut.StartTurnAsync(background, "hi", null);

        Assert.Equal(ChatState.Error, background.State);
        Assert.NotNull(background.Id);
        // C4: a backgrounded setup failure is persisted so its Error toast re-hydrates
        // from the store after a reap instead of dead-linking.
        await _chatService.Received(1).SaveAsync(Arg.Is<SyncAssistantChat>(c => c.Id == background.Id), Arg.Any<CancellationToken>());
        // LLM auto-title (which needs a provider) is suppressed for the errored chat.
        await _titleService.DidNotReceive().GenerateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartTurnAsync_NoProvider_ActiveSession_DoesNotPersist()
    {
        var sut = CreateSut();

        var active = sut.GetOrCreateActiveForNewChat(); // stays the active session
        _chatService.ClearReceivedCalls();

        var persona = new Persona { Name = "Tester", SystemPrompt = "be helpful" };
        _personas.ResolveActiveAsync(Arg.Any<WindowMode>(), Arg.Any<UserOperatingMode>()).Returns(persona);

        await sut.StartTurnAsync(active, "hi", null);

        Assert.Equal(ChatState.Error, active.State);
        // C4 scope: a FOREGROUND no-provider failure is NOT persisted — no junk history
        // entries for an unconfigured user, and the toast never fires for the active chat.
        await _chatService.DidNotReceive().SaveAsync(Arg.Any<SyncAssistantChat>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartTurnAsync_FirstTurn_WithProvider_PersistsImmediately()
    {
        // Point 3: a brand-new chat must enter history on the first message — not only
        // after the turn finishes. With provider resolution succeeding, StartTurnAsync
        // persists the first turn (user + streaming placeholder) before dispatching the run.
        var sut = CreateSut();

        var session = sut.GetOrCreateActiveForNewChat(); // first turn, Id still null
        _chatService.ClearReceivedCalls();

        var persona = new Persona { Name = "Tester", SystemPrompt = "be helpful" };
        _personas.ResolveActiveAsync(Arg.Any<WindowMode>(), Arg.Any<UserOperatingMode>()).Returns(persona);
        _providers.GetDefaultProviderForModeAsync(WindowMode.Assistant)
            .Returns(new AiProvider { Name = "Test", Endpoint = "https://example.test" });
        _composer.PrepareTurn(default!, default!, default!, default)
            .ReturnsForAnyArgs(new AssistantTurnSetup("system", null, false, false));

        await sut.StartTurnAsync(session, "hello", null);

        Assert.NotNull(session.Id);
        // SaveAsync runs synchronously (mocked) inside the first-turn persist, before
        // RunTurnAsync streams — so the chat is already in history. The snapshot holds the
        // user message + the (still streaming) assistant placeholder = 2 messages.
        await _chatService.Received().SaveAsync(
            Arg.Is<SyncAssistantChat>(c => c.Id == session.Id && c.Messages.Count == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartTurnAsync_AtFilesCommand_ReadsTaggedFileForInjection()
    {
        // The manager glue: an @Files-tagged turn reads the file at setup (capped to the per-file
        // line limit) so its content can be inlined into the user message. The preview read is
        // awaited inside StartTurnAsync, before the run is dispatched.
        var sut = CreateSut();
        var session = sut.GetOrCreateActiveForNewChat();

        var persona = new Persona { Name = "Tester", SystemPrompt = "be helpful" };
        _personas.ResolveActiveAsync(Arg.Any<WindowMode>(), Arg.Any<UserOperatingMode>()).Returns(persona);
        _providers.GetDefaultProviderForModeAsync(WindowMode.Assistant)
            .Returns(new AiProvider { Name = "Test", Endpoint = "https://example.test" });
        _composer.PrepareTurn(default!, default!, default!, default)
            .ReturnsForAnyArgs(new AssistantTurnSetup("system", null, false, false));

        _files.IsAvailable.Returns(true);
        _files.ReadPromptPreviewAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new FilePromptPreview("test.ps1", Found: true, Text: "Write-Host \"hi\"", TotalLines: 1, ShownLines: 1, Truncated: false, Error: null));

        await sut.StartTurnAsync(session, "@Files:\"test.ps1\" what does this do?", null);

        await _files.Received().ReadPromptPreviewAsync("test.ps1", Arg.Any<string?>(), 100, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartTurnAsync_NoFilesFolder_SkipsPreviewRead()
    {
        var sut = CreateSut();
        var session = sut.GetOrCreateActiveForNewChat();

        var persona = new Persona { Name = "Tester", SystemPrompt = "be helpful" };
        _personas.ResolveActiveAsync(Arg.Any<WindowMode>(), Arg.Any<UserOperatingMode>()).Returns(persona);
        _providers.GetDefaultProviderForModeAsync(WindowMode.Assistant)
            .Returns(new AiProvider { Name = "Test", Endpoint = "https://example.test" });
        _composer.PrepareTurn(default!, default!, default!, default)
            .ReturnsForAnyArgs(new AssistantTurnSetup("system", null, false, false));

        _files.IsAvailable.Returns(false); // no sandbox configured

        await sut.StartTurnAsync(session, "@Files:\"test.ps1\" what does this do?", null);

        await _files.DidNotReceive().ReadPromptPreviewAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartTurnAsync_SetupThrows_BackgroundSession_FailsGracefully()
    {
        var sut = CreateSut();

        var background = sut.GetOrCreateActiveForNewChat();
        sut.GetOrCreateActiveForNewChat(); // background is non-active
        _chatService.ClearReceivedCalls();

        // Settings resolution throws — both at setup AND again on the persist-path re-read
        // (TryStartAutoTitleAsync), exercising the failure path's own fallibility.
        // StartTurnAsync must settle to Error and NOT rethrow at the send command.
        _settings.GetSettingsAsync().Returns<AppSettings>(_ => throw new InvalidOperationException("settings boom"));

        await sut.StartTurnAsync(background, "hi", null); // a rethrow here fails the test

        Assert.Equal(ChatState.Error, background.State);
        // The persist was attempted (SaveAsync) before the auto-title re-read threw and was swallowed.
        await _chatService.Received(1).SaveAsync(Arg.Any<SyncAssistantChat>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartTurnAsync_Planned_CreatesPlannedRun_AndSurfacesActiveRunId()
    {
        // 1.3 lever wiring: planned:true creates a RunShape.Planned run and stamps its id onto the
        // session (SetActiveRun) so the active VM can embed the run-progress panel.
        var sut = CreateSut();
        var session = sut.GetOrCreateActiveForNewChat();

        var persona = new Persona { Name = "Tester", SystemPrompt = "be helpful", ToolScope = PersonaToolScope.Full };
        _personas.ResolveActiveAsync(Arg.Any<WindowMode>(), Arg.Any<UserOperatingMode>()).Returns(persona);
        _providers.GetDefaultProviderForModeAsync(WindowMode.Assistant)
            .Returns(new AiProvider { Name = "Test", Endpoint = "https://example.test", SupportsToolCalling = true });
        _composer.PrepareTurn(default!, default!, default!, default)
            .ReturnsForAnyArgs(new AssistantTurnSetup("system", null, true, false));

        var runId = Guid.NewGuid();
        _runService.CreateAsync(Arg.Any<AgentRunCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => new AgentRun { Id = runId, ChatId = session.Id ?? Guid.Empty, RunShape = RunShape.Planned });

        Guid? raised = null;
        session.ActiveRunChanged += (_, id) => raised = id;

        await sut.StartTurnAsync(session, "plan my week", null, planned: true);

        Assert.Equal(runId, session.ActiveRunId);
        Assert.Equal(runId, raised);
        await _runService.Received(1).CreateAsync(
            Arg.Is<AgentRunCreateRequest>(r => r.Shape == RunShape.Planned && r.Trigger == AgentRunTrigger.User),
            Arg.Any<CancellationToken>());
        // The empty streaming placeholder is removed for a Planned transcript (only the user goal remains pre-run).
        Assert.DoesNotContain(session.Messages, m => !m.IsUser && string.IsNullOrEmpty(m.Content) && m.IsStreaming);
    }

    [Fact]
    public async Task StartTurnAsync_NotPlanned_DoesNotCreateRun_NorSetActiveRunId()
    {
        var sut = CreateSut();
        var session = sut.GetOrCreateActiveForNewChat();

        var persona = new Persona { Name = "Tester", SystemPrompt = "be helpful", ToolScope = PersonaToolScope.Full };
        _personas.ResolveActiveAsync(Arg.Any<WindowMode>(), Arg.Any<UserOperatingMode>()).Returns(persona);
        _providers.GetDefaultProviderForModeAsync(WindowMode.Assistant)
            .Returns(new AiProvider { Name = "Test", Endpoint = "https://example.test", SupportsToolCalling = true });
        _composer.PrepareTurn(default!, default!, default!, default)
            .ReturnsForAnyArgs(new AssistantTurnSetup("system", null, false, false));

        await sut.StartTurnAsync(session, "just chat", null, planned: false);

        Assert.Null(session.ActiveRunId);
        await _runService.DidNotReceive().CreateAsync(Arg.Any<AgentRunCreateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartTurnAsync_Planned_WeakProvider_StillCreatesRun()
    {
        // R10 never-blocks contract at the consumer: a Weak/Unknown provider surfaces the banner in the
        // VM but must NOT gate the Planned send — the run is still created and stamped onto the session.
        var sut = CreateSut();
        var session = sut.GetOrCreateActiveForNewChat();

        var persona = new Persona { Name = "Tester", SystemPrompt = "be helpful", ToolScope = PersonaToolScope.Full };
        _personas.ResolveActiveAsync(Arg.Any<WindowMode>(), Arg.Any<UserOperatingMode>()).Returns(persona);
        _providers.GetDefaultProviderForModeAsync(WindowMode.Assistant)
            .Returns(new AiProvider { Name = "Local", Endpoint = "https://example.test", SupportsToolCalling = false });
        _capability.GetPlanningCapabilityAsync(Arg.Any<AiProvider>(), Arg.Any<CancellationToken>())
            .Returns(PlanningCapability.Weak);
        _composer.PrepareTurn(default!, default!, default!, default)
            .ReturnsForAnyArgs(new AssistantTurnSetup("system", null, true, false));

        var runId = Guid.NewGuid();
        _runService.CreateAsync(Arg.Any<AgentRunCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => new AgentRun { Id = runId, ChatId = session.Id ?? Guid.Empty, RunShape = RunShape.Planned });

        await sut.StartTurnAsync(session, "plan my week", null, planned: true);

        Assert.Equal(runId, session.ActiveRunId);
        await _runService.Received(1).CreateAsync(
            Arg.Is<AgentRunCreateRequest>(r => r.Shape == RunShape.Planned), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartTurnAsync_ChatTurn_CapableProvider_MarksSuggestEligible()
    {
        // §14.3/R7: only an interactive Chat turn on a tool-Capable provider offers suggest_agent_mode.
        var sut = CreateSut();
        var session = sut.GetOrCreateActiveForNewChat();

        var persona = new Persona { Name = "Tester", SystemPrompt = "be helpful", ToolScope = PersonaToolScope.Full };
        _personas.ResolveActiveAsync(Arg.Any<WindowMode>(), Arg.Any<UserOperatingMode>()).Returns(persona);
        _providers.GetDefaultProviderForModeAsync(WindowMode.Assistant)
            .Returns(new AiProvider { Name = "Test", Endpoint = "https://example.test", SupportsToolCalling = true });
        _capability.GetPlanningCapabilityAsync(Arg.Any<AiProvider>(), Arg.Any<CancellationToken>())
            .Returns(PlanningCapability.Capable);
        _composer.PrepareTurn(default!, default!, default!, default)
            .ReturnsForAnyArgs(new AssistantTurnSetup("system", null, true, false));

        await sut.StartTurnAsync(session, "plan my whole week end to end", null, planned: false);

        _composer.Received().PrepareTurn(
            Arg.Any<Persona>(), Arg.Any<AiProvider>(), Arg.Any<IReadOnlyList<AtCommand>>(),
            Arg.Any<bool>(), suggestAgentModeEligible: true);
    }

    [Fact]
    public async Task StartTurnAsync_ChatTurn_WeakProvider_NotSuggestEligible()
    {
        var sut = CreateSut();
        var session = sut.GetOrCreateActiveForNewChat();

        var persona = new Persona { Name = "Tester", SystemPrompt = "be helpful", ToolScope = PersonaToolScope.Full };
        _personas.ResolveActiveAsync(Arg.Any<WindowMode>(), Arg.Any<UserOperatingMode>()).Returns(persona);
        _providers.GetDefaultProviderForModeAsync(WindowMode.Assistant)
            .Returns(new AiProvider { Name = "Local", Endpoint = "https://example.test", SupportsToolCalling = false });
        _capability.GetPlanningCapabilityAsync(Arg.Any<AiProvider>(), Arg.Any<CancellationToken>())
            .Returns(PlanningCapability.Weak);
        _composer.PrepareTurn(default!, default!, default!, default)
            .ReturnsForAnyArgs(new AssistantTurnSetup("system", null, true, false));

        await sut.StartTurnAsync(session, "hi", null, planned: false);

        _composer.Received().PrepareTurn(
            Arg.Any<Persona>(), Arg.Any<AiProvider>(), Arg.Any<IReadOnlyList<AtCommand>>(),
            Arg.Any<bool>(), suggestAgentModeEligible: false);
    }

    [Fact]
    public async Task StartTurnAsync_PlannedTurn_NeverSuggestEligible()
    {
        // A Planned dispatch must never inject suggest_agent_mode, even on a Capable provider.
        var sut = CreateSut();
        var session = sut.GetOrCreateActiveForNewChat();

        var persona = new Persona { Name = "Tester", SystemPrompt = "be helpful", ToolScope = PersonaToolScope.Full };
        _personas.ResolveActiveAsync(Arg.Any<WindowMode>(), Arg.Any<UserOperatingMode>()).Returns(persona);
        _providers.GetDefaultProviderForModeAsync(WindowMode.Assistant)
            .Returns(new AiProvider { Name = "Test", Endpoint = "https://example.test", SupportsToolCalling = true });
        _capability.GetPlanningCapabilityAsync(Arg.Any<AiProvider>(), Arg.Any<CancellationToken>())
            .Returns(PlanningCapability.Capable);
        _composer.PrepareTurn(default!, default!, default!, default)
            .ReturnsForAnyArgs(new AssistantTurnSetup("system", null, true, false));
        _runService.CreateAsync(Arg.Any<AgentRunCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => new AgentRun { Id = Guid.NewGuid(), ChatId = session.Id ?? Guid.Empty, RunShape = RunShape.Planned });

        await sut.StartTurnAsync(session, "plan my week", null, planned: true);

        _composer.Received().PrepareTurn(
            Arg.Any<Persona>(), Arg.Any<AiProvider>(), Arg.Any<IReadOnlyList<AtCommand>>(),
            Arg.Any<bool>(), suggestAgentModeEligible: false);
    }

    [Fact]
    public void Reaper_OverCap_DropsOldestIdle_KeepsRecentAndActive()
    {
        var sut = CreateSut();

        var created = new List<ChatSession>();
        for (var i = 0; i < 12; i++)
            created.Add(sut.GetOrCreateActiveForNewChat());

        // All Idle; each creation activated the newest and reaped the oldest non-active
        // Idle one, holding the live set at the cap.
        Assert.Equal(8, sut.LiveSessions.Count);
        Assert.Same(created[^1], sut.ActiveSession);
        Assert.Contains(created[^1], sut.LiveSessions);   // active survives
        Assert.DoesNotContain(created[0], sut.LiveSessions); // oldest reaped
    }

    [Fact]
    public async Task Reaper_NeverDropsUnreadCompletedSession()
    {
        var sut = CreateSut();

        var unread = sut.GetOrCreateActiveForNewChat();
        unread.Messages.Add(new AssistantMessage(Microsoft.Extensions.AI.ChatRole.User, "hi"));
        await sut.PersistAsync(unread);
        var unreadId = unread.Id!.Value;

        // Switch away → background, then mark it Completed (an unread background result).
        sut.GetOrCreateActiveForNewChat();
        unread.SetState(ChatState.Completed);

        // Churn well past the cap so `unread` is the oldest by activation order.
        for (var i = 0; i < 12; i++)
            sut.GetOrCreateActiveForNewChat();

        // Completed = unread result → never reaped, even as the oldest session.
        Assert.Same(unread, sut.TryGetLive(unreadId));
        Assert.Equal(ChatState.Completed, unread.State);
    }

    [Fact]
    public void Reaper_NeverDropsInFlightSession()
    {
        var sut = CreateSut();

        // A backgrounded session mid-turn — reaping it would dispose its Cts in-stream
        // and break background continuation, so the reaper must always exclude it.
        var inFlight = sut.GetOrCreateActiveForNewChat();
        inFlight.Messages.Add(new AssistantMessage(Microsoft.Extensions.AI.ChatRole.User, "long turn"));
        sut.GetOrCreateActiveForNewChat(); // switch away → background
        inFlight.SetState(ChatState.Running);

        // Churn well past the cap; the oldest session here is the in-flight one.
        for (var i = 0; i < 12; i++)
            sut.GetOrCreateActiveForNewChat();

        Assert.Contains(inFlight, sut.LiveSessions);
        Assert.Equal(ChatState.Running, inFlight.State);
    }

    [Fact]
    public async Task Reaper_ReapedIdleSession_RehydratesFromStoreOnActivate()
    {
        var sut = CreateSut();

        // A persisted, then-backgrounded Idle session that will be pushed out of the window.
        var victim = sut.GetOrCreateActiveForNewChat();
        victim.Messages.Add(new AssistantMessage(Microsoft.Extensions.AI.ChatRole.User, "remember"));
        await sut.PersistAsync(victim);
        var victimId = victim.Id!.Value;

        // The store still holds it, so a resume after reaping must re-hydrate.
        var stored = new SyncAssistantChat
        {
            Id = victimId,
            SchemaVersion = 1,
            Title = "victim",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            LastAccessedAt = DateTime.UtcNow,
            WindowMode = WindowMode.Assistant.ToString(),
            Messages =
            [
                new SyncAssistantChatMessage { Id = Guid.NewGuid(), Role = "user", Content = "remember", Timestamp = DateTime.UtcNow },
            ],
        };
        _chatService.GetAsync(victimId, Arg.Any<CancellationToken>()).Returns(stored);

        // Churn past the cap so the Idle victim is reaped.
        for (var i = 0; i < 12; i++)
            sut.GetOrCreateActiveForNewChat();

        Assert.Null(sut.TryGetLive(victimId)); // dropped from memory

        var rehydrated = await sut.ActivateAsync(victimId); // re-loads from the store

        Assert.NotNull(rehydrated);
        Assert.Equal(victimId, rehydrated!.Id);
        Assert.Single(rehydrated.Messages);
    }

    // ---- A2: the composer gate is seeded SYNCHRONOUSLY from the launch-bracket index (closes W2c) ----

    [Fact]
    public async Task ActivateAsync_BracketedRun_GatesTheComposer_BeforeItEverGoesLive()
    {
        // The gate must already be set by the time SetActive raises ActiveChanged — that is the instant
        // AssistantViewModel attaches and reads ChatSession.ForeignRunActive (AssistantViewModel.cs:356),
        // i.e. the instant Send becomes clickable. Before A2 the flag arrived from a fire-and-forget lookup
        // that blocked on AgentRunService's gate, so an Enter press in that window reached a full-replace
        // SaveAsync. Drop the seed line from ActivateAsync and this test fails.
        //
        // It also pins the SingleTurn hole: nothing is ATTACHED to this session (RestoreActiveRunAsync only
        // ever attaches RunShape.Planned), yet the composer is gated — which is the whole point of keying the
        // decision on the chat rather than on ChatSession.ActiveRunId.
        var chatId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        _executingRuns.Register(chatId, runId);
        _chatService.GetAsync(chatId, Arg.Any<CancellationToken>()).Returns(StoredChat(chatId));

        var sut = CreateSut();
        bool? gatedWhenActivated = null;
        sut.ActiveChanged += (_, s) => gatedWhenActivated = s?.ForeignRunActive;

        var session = await sut.ActivateAsync(chatId);

        Assert.NotNull(session);
        Assert.True(gatedWhenActivated);
        Assert.True(session!.ForeignRunActive);
        Assert.Null(session.ActiveRunId);
    }

    [Fact]
    public async Task ActivateAsync_NoBracketedRun_LeavesTheComposerLive()
    {
        // GUARD, not a regression test: this passes before and after. It exists because the rejected
        // alternative to A2 was a pessimistic flicker-disable on every activation, and this is what would
        // catch that — an ordinary history click must not disable Send.
        var chatId = Guid.NewGuid();
        _chatService.GetAsync(chatId, Arg.Any<CancellationToken>()).Returns(StoredChat(chatId));

        var sut = CreateSut();
        var session = await sut.ActivateAsync(chatId);

        Assert.NotNull(session);
        Assert.False(session!.ForeignRunActive);
    }

    [Fact]
    public async Task RunChanged_Terminal_ReleasesTheBracket_AndUngatesTheComposer()
    {
        // The release must happen HERE and not only in the launcher's finally: AgentRunService raises the
        // terminal RunChanged BEFORE that finally runs (it raises outside its own gate), so a handler that
        // merely recomputed would read a still-present entry, conclude "executing", and never be woken again —
        // a permanently dead composer, because re-activating takes the live-attach branch and never re-seeds.
        var chatId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        _executingRuns.Register(chatId, runId);
        _chatService.GetAsync(chatId, Arg.Any<CancellationToken>()).Returns(StoredChat(chatId));

        var sut = CreateSut();
        var session = await sut.ActivateAsync(chatId);
        Assert.True(session!.ForeignRunActive);

        // AgentRunService raises RunChanged from a pool thread; the manager marshals the flip (G3), so poll.
        // The handler releases BEFORE it recomputes, so a cleared flag proves the release already landed.
        _runService.RunChanged += Raise.EventWith(new AgentRunChangedEventArgs(runId, AgentRunState.Completed));

        for (var i = 0; i < 200 && session.ForeignRunActive; i++)
            await Task.Delay(10, TestContext.Current.CancellationToken);

        Assert.False(session.ForeignRunActive);
        Assert.Null(_executingRuns.GetChatId(runId)); // reverse-looked-up from the run id alone

        // ...and the launcher's later finally is a harmless no-op.
        _executingRuns.Release(runId);
        Assert.False(_executingRuns.IsExecuting(chatId));
    }

    // ---------------------------------------------------------------------------------------------------
    // Batch 06 G5 / plan D4: the interactive Planned launch owns a workspace lifecycle now. Before this
    // group the branch was a bare CreateAsync and no directory was created anywhere on this path.
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Captures the <see cref="Pia.Services.RunContext"/> the orchestrator builds, then PARKS. The fact is
    /// what the manager handed the executor; letting the run drain would execute steps against a substituted
    /// AI client for no added coverage. Released by cancelling the session at the end of each fact.
    /// </summary>
    private sealed class CapturingPlanner : Pia.Services.Interfaces.IAgentPlanner
    {
        public TaskCompletionSource<string?> WorkspaceRoot { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<Pia.Services.Interfaces.PlanResult> PlanAsync(
            string goal, Pia.Services.RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct)
        {
            WorkspaceRoot.TrySetResult(ctx.WorkspaceRoot);
            await Task.Delay(Timeout.Infinite, ct);
            return Pia.Services.Interfaces.PlanResult.Fallback;
        }

        public Task<Pia.Services.Interfaces.PlanResult> ReplanAsync(
            Pia.Services.RunContext ctx, string? failure, Persona persona, AiProvider provider, CancellationToken ct)
            => Task.FromResult(Pia.Services.Interfaces.PlanResult.Fallback);
    }

    /// <summary>Drives a real interactive Planned launch with a working directory set, and hands back the
    /// workspace root that reached the run context (null ⇒ the run is not isolated).</summary>
    private async Task<(Guid RunId, string? CapturedRoot)> StartIsolatedPlannedTurnAsync(
        Pia.Services.Interfaces.IRunWorkspaceService workspaces)
    {
        var planner = new CapturingPlanner();
        var sut = CreateIsolatingSut(workspaces, planner);
        var session = sut.GetOrCreateActiveForNewChat();
        session.SetWorkingDirectory("sub");

        _personas.ResolveActiveAsync(Arg.Any<WindowMode>(), Arg.Any<UserOperatingMode>())
            .Returns(new Persona { Name = "Tester", SystemPrompt = "be helpful" });
        _providers.GetDefaultProviderForModeAsync(Arg.Any<WindowMode>())
            .Returns(new AiProvider { Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI });
        _composer.PrepareTurn(default!, default!, default!, default)
            .ReturnsForAnyArgs(new AssistantTurnSetup("system", null, SupportsTools: false, WebSearchActive: false));

        var runId = Guid.NewGuid();
        _runService.CreateAsync(Arg.Any<AgentRunCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => new AgentRun
            {
                Id = runId,
                ChatId = ci.Arg<AgentRunCreateRequest>().ChatId,
                RunShape = RunShape.Planned,
                State = AgentRunState.Planning,
            });

        await sut.StartPlannedTurnAsync(session, "do the thing");

        // The dispatch is fire-and-forget (posted onto the inline context), so bound the wait rather than
        // assuming it already landed.
        var captured = planner.WorkspaceRoot.Task;
        var settled = await Task.WhenAny(captured, Task.Delay(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken)) == captured;
        Assert.True(settled, "the run never reached the planner: the turn did not start");

        session.Cancel(); // release the parked planner so no dispatch outlives the fact
        return (runId, await captured);
    }

    /// <summary>
    /// REGRESSION, and the seam nothing else can see: the manager PROVISIONS a workspace for an interactive
    /// Planned run, asks for it with the CHAT's working subpath (B6 — the workspace stands in for
    /// <c>&lt;folder&gt;\sub</c>, which is why the steps must not narrow again), and hands the resulting root
    /// to the live executor. Drop the provisioning call and nothing is provisioned; drop the
    /// <c>workspaceRoot</c> argument to <c>new LiveTurnExecutor(...)</c> — which compiles, it is trailing and
    /// defaulted — and the run context comes back with a null root, i.e. every interactive step ships
    /// un-isolated.
    /// </summary>
    [Fact]
    public async Task StartPlannedTurn_ProvisionsAWorkspaceForTheChatSubpath_AndHandsItsRootToTheExecutor()
    {
        var runsBase = Path.Combine(Path.GetTempPath(), "PiaMgrWs_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runsBase);
        try
        {
            var workspaces = new Pia.Tests.Services.FakeRunWorkspaceService(runsBase);

            var (runId, capturedRoot) = await StartIsolatedPlannedTurnAsync(workspaces);

            Assert.Equal(runId, Assert.Single(workspaces.Provisioned));
            Assert.Equal("sub", Assert.Single(workspaces.ProvisionedSubpaths));
            Assert.Equal(workspaces.RootFor(runId), capturedRoot);
        }
        finally
        {
            try { Directory.Delete(runsBase, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// GUARD: provisioning is BOOKKEEPING with a user watching (guardrail 1). A provisioner that degrades to
    /// "no isolation" (null) or that throws must leave the turn running on the pre-Batch-06 path — writing
    /// straight into the assistant files folder — never refuse to start it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartPlannedTurn_WhenProvisioningDegradesOrThrows_TheTurnStillStarts(bool provisionerThrows)
    {
        var runsBase = Path.Combine(Path.GetTempPath(), "PiaMgrWs_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runsBase);
        try
        {
            Pia.Services.Interfaces.IRunWorkspaceService workspaces;
            if (provisionerThrows)
            {
                var throwing = Substitute.For<Pia.Services.Interfaces.IRunWorkspaceService>();
                throwing.ProvisionAsync(Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                    .Returns<Task<Pia.Services.Interfaces.RunWorkspace?>>(_ => throw new IOException("disk full"));
                workspaces = throwing;
            }
            else
            {
                workspaces = new Pia.Tests.Services.FakeRunWorkspaceService(runsBase) { ProvisionSucceeds = false };
            }

            var (_, capturedRoot) = await StartIsolatedPlannedTurnAsync(workspaces);

            // Reaching the planner at all is the "the turn started" half; the null root is the "no isolation"
            // half, i.e. exactly today's behaviour.
            Assert.Null(capturedRoot);
        }
        finally
        {
            try { Directory.Delete(runsBase, recursive: true); } catch { /* best effort */ }
        }
    }
}
