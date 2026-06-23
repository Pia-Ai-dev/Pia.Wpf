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

    public ChatSessionManagerTests()
    {
        _settings.GetSettingsAsync().Returns(new AppSettings());
        _loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
    }

    private ChatSessionManager CreateSut()
    {
        // The manager guards against a missing UI SynchronizationContext.
        if (SynchronizationContext.Current is null)
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());

        return new ChatSessionManager(
            NullLogger<ChatSessionManager>.Instance,
            NullLoggerFactory.Instance,
            _chatService, _settings, _personas, _providers, _composer,
            _titleService, _cards, _plugins, _ai, _permissions, _loc,
            () => _tokenMap, _notifier, _flow);
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
