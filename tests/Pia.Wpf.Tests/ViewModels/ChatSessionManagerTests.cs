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

    public ChatSessionManagerTests()
    {
        _settings.GetSettingsAsync().Returns(new AppSettings());
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        _capability.GetPlanningCapabilityAsync(Arg.Any<AiProvider>(), Arg.Any<CancellationToken>())
            .Returns(PlanningCapability.Capable);
    }

    private ChatSessionManager CreateSut()
    {
        // The manager guards against a missing UI SynchronizationContext.
        if (SynchronizationContext.Current is null)
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());

        var orchestrator = new Pia.Services.AgentRunOrchestrator(
            _runService, Substitute.For<Pia.Services.IAgentPlanner>(),
            new Pia.Tests.Services.FakeVerifier(),
            NullLogger<Pia.Services.AgentRunOrchestrator>.Instance);

        return new ChatSessionManager(
            NullLogger<ChatSessionManager>.Instance,
            NullLoggerFactory.Instance,
            _chatService, _settings, _personas, _providers, _composer,
            _titleService, _cards, _plugins, _ai, _permissions, _loc,
            () => _tokenMap, _notifier, _flow, _files, orchestrator, _runService, _capability,
            _headlessLauncher, _windowManager);
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
        _chatService.GetAsync(chatId).Returns(stored);

        var sut = CreateSut();
        var session = await sut.ActivateAsync(chatId);

        Assert.NotNull(session);
        Assert.Equal(chatId, session!.Id);
        Assert.Equal(2, session.Messages.Count);
        Assert.Same(session, sut.ActiveSession);
        await _chatService.Received(1).TouchLastAccessedAsync(chatId);
    }

    [Fact]
    public async Task ActivateAsync_MissingChat_ReturnsNull()
    {
        var chatId = Guid.NewGuid();
        _chatService.GetAsync(chatId).Returns((SyncAssistantChat?)null);

        var sut = CreateSut();
        var session = await sut.ActivateAsync(chatId);

        Assert.Null(session);
    }

    [Fact]
    public async Task StartPlannedTurn_PersistsAnEmptyGrantEnvelope_SoParkingCannotWidenAuthority()
    {
        // D1's producer half for the INTERACTIVE origin. An interactive run holds no standing write grant:
        // write_file is not auto-approve eligible, so every write raises an action card the user clicks. A
        // resume, though, runs UNATTENDED through HeadlessRunLauncher, and a run whose PolicyJson is null
        // falls back to the {write_file} resume floor — so parking would ESCALATE the run to card-free
        // writes with nobody watching. The create must persist the honoured-EMPTY envelope instead.
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
        var restored = Pia.Services.HeadlessRunLauncher.TryRestoreGrantEnvelope(captured!.PolicyJson);
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
        await _chatService.DidNotReceive().GetAsync(chatId);
        await _chatService.DidNotReceive().TouchLastAccessedAsync(chatId);
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
        _chatService.GetAsync(victimId).Returns(stored);

        // Churn past the cap so the Idle victim is reaped.
        for (var i = 0; i < 12; i++)
            sut.GetOrCreateActiveForNewChat();

        Assert.Null(sut.TryGetLive(victimId)); // dropped from memory

        var rehydrated = await sut.ActivateAsync(victimId); // re-loads from the store

        Assert.NotNull(rehydrated);
        Assert.Equal(victimId, rehydrated!.Id);
        Assert.Single(rehydrated.Messages);
    }
}
