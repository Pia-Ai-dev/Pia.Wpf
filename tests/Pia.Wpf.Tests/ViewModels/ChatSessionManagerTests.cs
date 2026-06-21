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
            _titleService, _cards, _plugins, _ai, _loc,
            () => _tokenMap, _notifier);
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
}
