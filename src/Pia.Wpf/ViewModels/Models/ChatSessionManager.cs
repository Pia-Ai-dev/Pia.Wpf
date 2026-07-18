using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Logging;
using Pia.Models;
using Pia.Services;
using Pia.Services.Flow;
using Pia.Services.Interfaces;
using Pia.Shared.Models;

namespace Pia.ViewModels.Models;

/// <summary>
/// Scoped-per-window owner of the live <see cref="ChatSession"/> set. It prepares
/// turns (persona/provider/prompt resolution), runs them on the UI thread,
/// persists finished turns, drives auto-title, and re-raises per-session state /
/// title changes for the active view model and history.
///
/// Lives in <c>Pia.ViewModels.Models</c> (alongside <see cref="ChatSession"/>) so
/// it can reference the session type without violating the
/// Services-must-not-depend-on-ViewModels layer rule, while still depending only
/// on service interfaces.
/// </summary>
public sealed class ChatSessionManager : IChatSessionManager, IDisposable
{
    private readonly ILogger<ChatSessionManager> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IAssistantChatService _chatService;
    private readonly ISettingsService _settingsService;
    private readonly IPersonaService _personaService;
    private readonly IProviderService _providerService;
    private readonly IAssistantPromptComposer _promptComposer;
    private readonly IChatTitleService _chatTitleService;
    private readonly IActionCardBuilder _actionCardBuilder;
    private readonly IPluginService _pluginService;
    private readonly IAiClientService _aiClientService;
    private readonly IToolPermissionService _permissionService;
    private readonly ILocalizationService _localizationService;
    private readonly Func<ITokenMapService> _tokenMapFactory;
    private readonly IBackgroundChatNotifier _backgroundChatNotifier;
    private readonly IFlowService _flowService;
    private readonly IFilesToolHandler _filesToolHandler;
    private readonly AgentRunOrchestrator _agentRunOrchestrator;
    private readonly IAgentRunService _agentRunService;
    private readonly SynchronizationContext _syncContext;

    /// <summary>Per-file line cap for <c>@Files</c> content injected directly into the prompt.</summary>
    private const int FilePreviewLines = 100;

    /// <summary>Max distinct <c>@Files</c> files whose content is injected in one turn (others rely on tools).</summary>
    private const int MaxFilePreviews = 5;

    private readonly Dictionary<Guid, ChatSession> _sessions = new();
    private readonly HashSet<ChatSession> _allSessions = new();
    private long _activationCounter;
    private bool _disposed;

    /// <summary>
    /// Soft cap on retained live sessions (open-question A7). The reaper keeps the
    /// <see cref="MaxRetainedSessions"/> most-recently-active sessions; among the older
    /// remainder it retires only those safe to drop (non-active, Idle/Error). In-flight
    /// (Running/WaitingForTool) and unread Completed sessions are never reaped, so the
    /// live count can briefly exceed this cap.
    /// </summary>
    private const int MaxRetainedSessions = 8;

    public ChatSession? ActiveSession { get; private set; }

    public IReadOnlyCollection<ChatSession> LiveSessions => _allSessions;

    public event EventHandler<ChatSession?>? ActiveChanged;
    public event EventHandler<SessionStateChangedEventArgs>? SessionStateChanged;
    public event EventHandler<SessionTitleChangedEventArgs>? SessionTitleChanged;

    public ChatSessionManager(
        ILogger<ChatSessionManager> logger,
        ILoggerFactory loggerFactory,
        IAssistantChatService chatService,
        ISettingsService settingsService,
        IPersonaService personaService,
        IProviderService providerService,
        IAssistantPromptComposer promptComposer,
        IChatTitleService chatTitleService,
        IActionCardBuilder actionCardBuilder,
        IPluginService pluginService,
        IAiClientService aiClientService,
        IToolPermissionService permissionService,
        ILocalizationService localizationService,
        Func<ITokenMapService> tokenMapFactory,
        IBackgroundChatNotifier backgroundChatNotifier,
        IFlowService flowService,
        IFilesToolHandler filesToolHandler,
        AgentRunOrchestrator agentRunOrchestrator,
        IAgentRunService agentRunService)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _chatService = chatService;
        _settingsService = settingsService;
        _personaService = personaService;
        _providerService = providerService;
        _promptComposer = promptComposer;
        _chatTitleService = chatTitleService;
        _actionCardBuilder = actionCardBuilder;
        _pluginService = pluginService;
        _aiClientService = aiClientService;
        _permissionService = permissionService;
        _localizationService = localizationService;
        _tokenMapFactory = tokenMapFactory;
        _backgroundChatNotifier = backgroundChatNotifier;
        _flowService = flowService;
        _filesToolHandler = filesToolHandler;
        _agentRunOrchestrator = agentRunOrchestrator;
        _agentRunService = agentRunService;
        _syncContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("ChatSessionManager must be created on the UI thread");
    }

    private ChatSession CreateSession()
    {
        // Each session owns its OWN token map (per-session PII namespace) so two
        // background turns never collide once turns run concurrently. The decorator
        // reaches the running turn's map via TokenMapAmbient (set in RunTurnAsync);
        // tool handlers reach the running turn's Id via TaskAmbient (set the same way).
        var tokenMap = _tokenMapFactory();
        var session = new ChatSession(
            tokenMap,
            _aiClientService,
            _pluginService,
            _actionCardBuilder,
            _permissionService,
            _localizationService,
            _loggerFactory.CreateLogger<ChatSession>(),
            IsSessionActive);

        // Pre-load the map's PII keywords/memory so the first turn tokenizes correctly.
        InitializeTokenMapAsync(tokenMap).SafeFireAndForget(_logger);

        session.StateChanged += OnSessionStateChanged;
        // The manager persists every session's finished turns (covers active and,
        // in Step 4, background turns). The active VM separately subscribes
        // TurnCompleted for followups/TTS.
        session.TurnCompleted += OnSessionTurnCompleted;
        _allSessions.Add(session);
        return session;
    }

    private async Task InitializeTokenMapAsync(ITokenMapService tokenMap)
    {
        try { await tokenMap.InitializeAsync(); }
        catch (Exception ex) { _logger.LogError(ex, "Failed to initialize a session token map"); }
    }

    /// <summary>Synchronous is-active probe handed to each session for its terminal-state decision.</summary>
    private bool IsSessionActive(ChatSession session) => ReferenceEquals(session, ActiveSession);

    private void OnSessionTurnCompleted(object? sender, TurnCompletedEventArgs e)
    {
        // Persist on every terminal state (matches today's finally-persist, which
        // saved error turns too). Follow-up/TTS gating lives in the active VM.
        if (sender is ChatSession session)
            PersistAsync(session).SafeFireAndForget(_logger);
    }

    private void OnSessionStateChanged(object? sender, ChatStateChangedEventArgs e)
    {
        if (sender is not ChatSession session) return;
        var isActive = ReferenceEquals(session, ActiveSession);
        SessionStateChanged?.Invoke(this, new SessionStateChangedEventArgs
        {
            ChatId = session.Id,
            OldState = e.OldState,
            NewState = e.NewState,
            IsActive = isActive,
        });

        // Single notifier-routing point: a background (non-active) session that
        // reaches a surface-worthy state pings the user with a link to the chat.
        // Running/Idle never notify (SetState already de-dupes unchanged values).
        if (!isActive && session.Id is { } chatId
            && e.NewState is ChatState.WaitingForTool or ChatState.Completed or ChatState.Error)
        {
            var displayTitle = string.IsNullOrWhiteSpace(session.Title)
                ? _localizationService["AssistantChat_TitlePlaceholder_NewChat"]
                : session.Title!;
            _backgroundChatNotifier.NotifyStateChange(chatId, displayTitle, e.NewState);
        }
    }

    public ChatSession GetOrCreateActiveForNewChat()
    {
        var session = CreateSession();
        SetActive(session);
        return session;
    }

    public ChatSession? TryGetLive(Guid chatId) =>
        _sessions.TryGetValue(chatId, out var session) ? session : null;

    public ChatState GetState(Guid chatId) =>
        _sessions.TryGetValue(chatId, out var session) ? session.State : ChatState.Idle;

    public void SetActive(ChatSession session)
    {
        if (ReferenceEquals(session, ActiveSession)) return;
        ActiveSession = session;
        session.LastActivatedSequence = ++_activationCounter;

        // Clear Completed → Idle on activation: the result is now "read".
        if (session.State == ChatState.Completed)
            session.SetState(ChatState.Idle);

        // Opening/activating the chat resolves its background-chat Flow alert (design §6 auto-retract).
        // This covers every open path — toast click, Flow link, and in-window navigation — and is a
        // no-op when no alert is live for this chat.
        if (session.Id is { } chatId)
            _flowService.Retract(chatId.ToString());

        ActiveChanged?.Invoke(this, session);

        // Switching active is the only point at which sessions accumulate (both
        // GetOrCreateActiveForNewChat and ActivateAsync route through SetActive), so it
        // is the natural reaper hook. A long-lived single chat running many turns adds
        // no new session and so never grows the live set.
        ReapStaleSessions();
    }

    /// <summary>
    /// Drops background sessions that are safe to forget once the live set exceeds
    /// <see cref="MaxRetainedSessions"/>. Their finished turns are already persisted
    /// (see <see cref="OnSessionTurnCompleted"/>), so a later <see cref="ActivateAsync"/>
    /// re-hydrates them from the store — reaping only frees memory, it never loses a
    /// recoverable turn.
    /// </summary>
    internal void ReapStaleSessions()
    {
        if (_allSessions.Count <= MaxRetainedSessions) return;

        // Keep the N most-recently-active sessions; among the older remainder retire
        // only the non-active Idle/Error ones. In-flight (Running/WaitingForTool) and
        // unread Completed sessions are never dropped, which is why the live count can
        // exceed the cap. The previously-active session is protected because it holds
        // the second-highest stamp and so stays inside the keep-window for any N >= 2
        // (do not lower MaxRetainedSessions to 1 without revisiting this).
        var stale = _allSessions
            .OrderByDescending(s => s.LastActivatedSequence)
            .Skip(MaxRetainedSessions)
            .Where(s => !ReferenceEquals(s, ActiveSession)
                        && s.State is ChatState.Idle or ChatState.Error)
            .ToList();

        foreach (var session in stale)
            RetireSession(session);
    }

    private void RetireSession(ChatSession session)
    {
        session.StateChanged -= OnSessionStateChanged;
        session.TurnCompleted -= OnSessionTurnCompleted;
        _allSessions.Remove(session);
        if (session.Id is { } id
            && _sessions.TryGetValue(id, out var keyed)
            && ReferenceEquals(keyed, session))
        {
            _sessions.Remove(id);
        }

        // Id + enum + count only — never the title/content (CLAUDE.md privacy logging).
        _logger.LogInformation("Reaped background chat session {ChatId} (state {State}); {Count} live sessions remain",
            session.Id, session.State, _allSessions.Count);

        session.Dispose();
    }

    public async Task<ChatSession?> ActivateAsync(Guid chatId)
    {
        // Live-attach: if this chat is already running in the background, swap to it
        // WITHOUT cancelling or reloading. Its Messages already hold any pending
        // action card, so activating reveals it for free. This is the headline:
        // switching chats no longer kills the in-flight turn.
        if (TryGetLive(chatId) is { } live)
        {
            SetActive(live);
            return live;
        }

        // No live session — hydrate from the store (a previously persisted chat).
        SyncAssistantChat? chat;
        try
        {
            chat = await _chatService.GetAsync(chatId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load chat {ChatId}", chatId);
            return null;
        }

        if (chat is null)
        {
            _logger.LogWarning("Chat {ChatId} not found during resume", chatId);
            return null;
        }

        var session = CreateSession();
        foreach (var dto in chat.Messages)
            session.Messages.Add(AssistantMessageMapper.FromDto(dto));

        session.SetIdentity(chat.Id, chat.CreatedAt, chat.ProviderId, chat.Title, autoTitleApplied: true);
        session.SetWorkingDirectory(chat.WorkingDirectory);
        _sessions[chat.Id] = session;
        SetActive(session);

        _logger.LogInformation("Resumed chat {ChatId} ({MessageCount} messages)", chat.Id, chat.Messages.Count);
        _logger.SensitiveDebug("Resumed chat {ChatId} title: {Title}", chat.Id, chat.Title);

        try
        {
            await _chatService.TouchLastAccessedAsync(chat.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to touch LastAccessedAt for {ChatId}", chat.Id);
        }

        return session;
    }

    /// <summary>
    /// Programmatic entry point for a <see cref="RunShape.Planned"/> agent run (§13.11). In 1.2 this
    /// is reachable only programmatically (tests / debug); the user-facing Chat/Agent lever is 1.3.
    /// </summary>
    internal Task StartPlannedTurnAsync(ChatSession session, string goal) =>
        StartTurnAsync(session, goal, attachment: null, regenerationInstruction: null, planned: true);

    public async Task StartTurnAsync(
        ChatSession session, string userText, ImageAttachment? attachment, string? regenerationInstruction = null,
        bool planned = false)
    {
        // Captured before the Id-assignment block below: a brand-new chat has no Id yet,
        // so this marks the first turn (never persisted) vs. a resumed/continuing chat
        // (already in history). Drives the persist-on-first-message below.
        var isFirstTurn = session.Id is null;

        var atCommands = AtCommandParser.ExtractAllCommands(userText);

        var userMessage = new AssistantMessage(ChatRole.User, userText)
        {
            Attachment = attachment,
        };
        session.Messages.Add(userMessage);

        var assistantMessage = new AssistantMessage(ChatRole.Assistant) { IsStreaming = true };
        session.Messages.Add(assistantMessage);

        // Assign the Id synchronously at first-turn start (before any state change is
        // raised) and key the session into _sessions now. A first-turn chat backgrounded
        // mid-turn would otherwise have Id == null at its terminal/WaitingForTool
        // transition, so the notifier-routing gate in OnSessionStateChanged would drop
        // the background notification permanently (PersistAsync, the only other Id
        // assignment, runs at end-of-turn and raises no new state transition). Keying it
        // in now also makes a first-turn chat resumable/live-attachable while it streams.
        // PersistAsync's own `Id is null` assignment then no-ops and its
        // `_sessions[chatId] = session` is idempotent.
        if (session.Id is null)
        {
            var newId = Guid.NewGuid();
            session.SetIdentity(newId, session.CreatedAt, session.ProviderId, session.Title, session.AutoTitleApplied);
            _sessions[newId] = session;
        }

        // Create the per-turn CTS BEFORE the setup-await window so a Cancel during setup
        // lands on a live CTS instead of being silently lost (open-question C1).
        // RunTurnAsync reuses this CTS; the setup-failure paths release it via DisposeCts.
        session.BeginTurn();

        // Flip to Running synchronously (before any await) so the proxied IsStreaming
        // disables the send button and closes the voice-mode gate immediately —
        // preserving today's synchronous IsStreaming=true at send time.
        session.SetState(ChatState.Running);

        ChatTurnRequest request;
        Persona persona;
        try
        {
            var settings = await _settingsService.GetSettingsAsync();
            var tokenizationEnabled = settings.Privacy.TokenizationEnabled;

            persona = await _personaService.ResolveActiveAsync(
                WindowMode.Assistant, settings.UserOperatingMode ?? UserOperatingMode.Personal);
            assistantMessage.Persona = PersonaAttribution.From(persona);

            var provider = persona.PreferredProviderId.HasValue
                ? await _providerService.GetProviderAsync(persona.PreferredProviderId.Value)
                : null;
            provider ??= await _providerService.GetDefaultProviderForModeAsync(WindowMode.Assistant);
            if (provider is null)
            {
                assistantMessage.Content = _localizationService["Msg_Assistant_NoProviderInline"];
                assistantMessage.IsStreaming = false;
                session.SetState(ChatState.Error);
                session.RaiseRunFailed(new RunFailedEventArgs
                {
                    Kind = RunFailureKind.Generic,
                    Title = _localizationService["Msg_Error"],
                    Message = _localizationService["Msg_Assistant_NoProviderConfigured"],
                });
                await FinalizeFailedSetupAsync(session);
                return;
            }

            if (persona.ReasoningEffort.HasValue)
            {
                provider = provider.Clone();
                provider.ReasoningEffort = persona.ReasoningEffort.Value;
            }

            session.SetProviderId(provider.Id);
            _logger.LogInformation("SendMessage: resolved persona {PersonaId} (ToolScope {ToolScope})", persona.Id, persona.ToolScope);

            var turnSetup = _promptComposer.PrepareTurn(persona, provider, atCommands, tokenizationEnabled);
            // Provider name is a user-named item (CLAUDE.md) — keep it out of the
            // release-surviving log; surface IDs/counts at Info, the name only in DEBUG.
            _logger.LogInformation("SendMessage: provider={ProviderId}, supportsTools={SupportsTools}, toolCount={ToolCount}, atCommandCount={AtCommandCount}",
                provider.Id, turnSetup.SupportsTools, turnSetup.Tools?.Count ?? 0, atCommands.Count);
            _logger.SensitiveDebug("SendMessage provider name: {ProviderName}", provider.Name);

            if (turnSetup.Tools is { Count: > 0 } toolList)
                _logger.LogDebug("Tools being sent to AI: [{ToolNames}]", string.Join(", ", toolList.Select(t => t.Name)));

            // @Files content injection: read the tagged file(s) and inline them into the user turn,
            // so a model that won't call read_file on its own still sees the file (the root cause of
            // the @Files hallucination). Independent of tool support — most valuable on a no-tools turn.
            var injectedFileContext = await BuildInjectedFileContextAsync(
                atCommands, session.WorkingDirectory, turnSetup.SupportsTools, assistantMessage);

            request = new ChatTurnRequest
            {
                UserMessage = userMessage,
                AssistantMessage = assistantMessage,
                Provider = provider,
                TurnSetup = turnSetup,
                AtCommands = atCommands,
                InjectedFileContext = injectedFileContext,
                RegenerationInstruction = regenerationInstruction,
                TokenizationEnabled = tokenizationEnabled,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Turn setup (settings/persona/provider/prompt) threw before RunTurnAsync
            // was dispatched. RunTurnAsync's try/finally — the only place that settles
            // terminal state and creates/disposes the Cts — never runs, so without this
            // guard the session would stay wedged in Running (IsStreaming stuck true,
            // send disabled, Cancel a no-op on the null Cts). Mirror the no-provider
            // branch: settle to Error and surface a handled RunFailed so the active VM
            // restores the composer and the session can be used again.
            _logger.LogError(ex, "Failed to set up AI turn");
            if (string.IsNullOrEmpty(assistantMessage.Content))
                assistantMessage.Content = _localizationService.Format("Msg_Assistant_ResponseFailed", ex.Message);
            assistantMessage.IsStreaming = false;
            session.SetState(ChatState.Error);
            session.RaiseRunFailed(new RunFailedEventArgs
            {
                Kind = RunFailureKind.Generic,
                Title = _localizationService["Msg_Error"],
                Message = _localizationService.Format("Msg_Assistant_ResponseFailed", ex.Message),
            });
            await FinalizeFailedSetupAsync(session);
            return;
        }

        if (planned)
        {
            // The pre-added empty streaming assistant placeholder is for the single-turn path; a Planned
            // run's transcript is [user: goal] + one assistant message per step. Remove it BEFORE the persist
            // below so the stored chat never carries a stray empty assistant message (visible in history if
            // the app crashes mid-run). LiveTurnExecutor.BeginRunAsync also removes it defensively (§13.7).
            session.Messages.Remove(assistantMessage);

            // R1/FK: the AgentRuns FK needs the AssistantChats parent row. AWAIT a persist first —
            // the interactive first-turn persist below is fire-and-forget and not safe before CreateAsync.
            await PersistAsync(session);

            var run = await _agentRunService.CreateAsync(new AgentRunCreateRequest(
                session.Id!.Value, RunShape.Planned, AgentRunTrigger.User, Goal: userText));

            // BeginTurn() above already created session.Cts; the Planned branch does NOT call BeginTurn
            // per step (R13). The orchestrator links the run CTS from session.Cts.Token below, so
            // ChatSession.Cancel() propagates to the run + in-flight step. Constructed on the UI thread
            // so the LiveTurnExecutor captures the UI SynchronizationContext.
            var live = new LiveTurnExecutor(session, IsSessionActive,
                PersonaAttribution.From(persona), request.Provider, request.TurnSetup, request.TokenizationEnabled);

            _agentRunOrchestrator
                .RunAsync(run, live, persona, request.Provider, RunProfile.Interactive, session.Cts!.Token)
                .SafeFireAndForget(_logger);
            return;
        }

        // Persist on the first message so the chat appears in history/flyout immediately —
        // not only once the turn completes. Placed AFTER provider resolution (the
        // no-provider and setup-failure paths returned above via FinalizeFailedSetupAsync),
        // so an unconfigured user still accrues no junk history. PersistAsync snapshots
        // Messages synchronously before RunTurnAsync streams into the assistant placeholder,
        // so there is no concurrent mutation; auto-title still no-ops here (no assistant
        // reply yet). Subsequent turns are already in history, so only the first needs this.
        if (isFirstTurn)
            PersistAsync(session).SafeFireAndForget(_logger);

        // UI-affine fire-and-forget: starts on the UI thread; continuations resume
        // on the captured UI SynchronizationContext (no Task.Run).
        session.RunTurnAsync(request, CancellationToken.None).SafeFireAndForget(_logger);
    }

    /// <summary>
    /// Reads the content of files tagged with an <c>@Files</c> command (those carrying an explicit
    /// path) and renders it for direct injection into the AI-visible user message — so a model that
    /// won't call <c>read_file</c> on its own still sees the file (the root cause of the @Files
    /// hallucination). Deduplicates by path, caps the number of files at <see cref="MaxFilePreviews"/>
    /// (the rest fall back to the file tools), and never throws — an unreadable file is rendered as a
    /// short note via <see cref="FilePromptPreview"/>. Returns null when there is nothing to inject
    /// (no path-tagged files, or no assistant files folder is configured).
    /// </summary>
    private async Task<string?> BuildInjectedFileContextAsync(
        IReadOnlyList<AtCommand> atCommands, string? workingDirectory, bool supportsTools, AssistantMessage assistantMessage)
    {
        if (!_filesToolHandler.IsAvailable)
            return null;

        var paths = atCommands
            .Where(c => c.Domain == AtCommandDomain.Files && !string.IsNullOrWhiteSpace(c.ItemTitle))
            .Select(c => c.ItemTitle!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (paths.Count == 0)
            return null;

        var dropped = 0;
        if (paths.Count > MaxFilePreviews)
        {
            dropped = paths.Count - MaxFilePreviews;
            paths = paths.Take(MaxFilePreviews).ToList();
        }

        // Reads run un-cancelled (CancellationToken.None): they are bounded (≤FilePreviewLines lines,
        // ≤1 MB raw) and run inside StartTurnAsync's setup, whose catch deliberately excludes
        // OperationCanceledException — letting an OCE escape here would wedge the session in Running.
        var previews = new List<FilePromptPreview>(paths.Count);
        foreach (var path in paths)
        {
            var preview = await _filesToolHandler.ReadPromptPreviewAsync(path, workingDirectory, FilePreviewLines, CancellationToken.None);
            previews.Add(preview);

            // Surface the referenced file as an "open file" chip on the answer (the @File scope).
            if (preview is { Found: true, AbsolutePath: { } abs })
                assistantMessage.AddOrUpgradeFileRef(new Pia.Models.FileRef(abs, Pia.Models.FileRefKind.Referenced));
        }

        if (dropped > 0)
            _logger.LogInformation(
                "@Files injected {Count} file(s); {Dropped} additional tagged file(s) left to the file tools", previews.Count, dropped);

        var block = AssistantPromptComposer.BuildFileContextBlock(previews, supportsTools);
        return string.IsNullOrEmpty(block) ? null : block;
    }

    /// <summary>
    /// Settles a turn that failed during setup (no provider / setup exception) before
    /// <see cref="ChatSession.RunTurnAsync"/> ever ran. Releases the per-turn CTS created
    /// by <see cref="ChatSession.BeginTurn"/> (open-question C1) and — only for a session
    /// that is no longer the active one — persists the errored chat so a background Error
    /// toast re-hydrates from the store after a reap instead of dead-linking
    /// (open-question C4). A foreground failure (the common no-provider case) is left
    /// unpersisted, exactly as before, so an unconfigured user accrues no junk history.
    /// </summary>
    private async Task FinalizeFailedSetupAsync(ChatSession session)
    {
        session.DisposeCts();

        if (IsSessionActive(session))
            return;

        // No LLM auto-title for an errored, possibly provider-less chat; DeriveChatTitle
        // still derives a title from the user message inside PersistAsync.
        session.AutoTitleApplied = true;

        // This runs on a failure path whose whole job is to fail gracefully, yet
        // PersistAsync re-enters fallible services (SaveAsync, plus a settings re-read in
        // TryStartAutoTitleAsync). Unlike the normal completion path — where PersistAsync
        // runs fire-and-forget (SafeFireAndForget swallows/logs) — here it is awaited by
        // StartTurnAsync → the send command, so a throw would surface at the command.
        // Swallow + log to match the fire-and-forget contract.
        try
        {
            await PersistAsync(session);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist errored setup turn for {ChatId}", session.Id);
        }
    }

    public async Task PersistAsync(ChatSession session)
    {
        if (session.Messages.Count == 0) return;

        var nowUtc = DateTime.UtcNow;

        if (session.Id is null)
            session.SetIdentity(Guid.NewGuid(), nowUtc, session.ProviderId, session.Title, session.AutoTitleApplied);

        var chatId = session.Id!.Value;
        _sessions[chatId] = session;

        var chat = new SyncAssistantChat
        {
            Id = chatId,
            SchemaVersion = 1,
            Title = DeriveChatTitle(session),
            CreatedAt = session.CreatedAt,
            UpdatedAt = nowUtc,
            LastAccessedAt = nowUtc,
            WindowMode = WindowMode.Assistant.ToString(),
            ProviderId = session.ProviderId,
            WorkingDirectory = session.WorkingDirectory,
            Messages = [.. session.Messages.Select(AssistantMessageMapper.ToDto)],
        };

        try
        {
            await _chatService.SaveAsync(chat);
            session.SetTitle(chat.Title);
            RaiseTitleChanged(session, chat.Title);
            _logger.LogInformation("Persisted assistant chat {ChatId} ({MessageCount} messages)",
                chat.Id, chat.Messages.Count);
            _logger.SensitiveDebug("Chat {ChatId} title: {Title}", chat.Id, chat.Title);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist assistant chat {ChatId}", chat.Id);
        }

        await TryStartAutoTitleAsync(session, chat);
    }

    private async Task TryStartAutoTitleAsync(ChatSession session, SyncAssistantChat chat)
    {
        var settings = await _settingsService.GetSettingsAsync();
        if (!settings.ChatAutoTitleEnabled || session.AutoTitleApplied) return;

        var userCount = chat.Messages.Count(m => m.Role == "user");
        var assistantReply = chat.Messages.FirstOrDefault(m => m.Role == "assistant"
            && !string.IsNullOrWhiteSpace(m.Content));
        if (userCount != 1 || assistantReply is null) return;

        var firstUser = chat.Messages.FirstOrDefault(m => m.Role == "user");
        if (firstUser is null || string.IsNullOrWhiteSpace(firstUser.Content)) return;

        session.AutoTitleApplied = true;
        _logger.LogInformation("Auto-title generation starting for chat {ChatId}", chat.Id);
        RenameChatAsync(session, chat.Id, firstUser.Content, assistantReply.Content).SafeFireAndForget(_logger);
    }

    private async Task RenameChatAsync(ChatSession session, Guid chatId, string firstUserContent, string firstAssistantContent)
    {
        try
        {
            var title = await _chatTitleService.GenerateAsync(firstUserContent, firstAssistantContent);
            if (string.IsNullOrEmpty(title))
                return;

            var existing = await _chatService.GetAsync(chatId);
            if (existing is null)
            {
                _logger.LogWarning("Auto-title: chat {ChatId} disappeared before rename", chatId);
                return;
            }

            existing.Title = title;
            existing.UpdatedAt = DateTime.UtcNow;
            await _chatService.SaveAsync(existing);

            session.SetTitle(title);
            // Forward to the chip only when this session is active (is-active gate
            // replaces the old _currentChatId guard).
            RaiseTitleChanged(session, title);

            _logger.LogInformation("Auto-title applied for chat {ChatId}", chatId);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-title generation failed for chat {ChatId}", chatId);
        }
    }

    private void RaiseTitleChanged(ChatSession session, string? title)
    {
        var isActive = ReferenceEquals(session, ActiveSession);
        SessionTitleChanged?.Invoke(this, new SessionTitleChangedEventArgs
        {
            Session = session,
            Title = title,
            IsActive = isActive,
        });
    }

    private static string? DeriveChatTitle(ChatSession session)
    {
        var firstUser = session.Messages.FirstOrDefault(m => m.IsUser);
        if (firstUser is null || string.IsNullOrWhiteSpace(firstUser.Content)) return null;

        var collapsed = TextFormatting.CollapseWhitespace(firstUser.Content);
        const int max = 40;
        return collapsed.Length <= max ? collapsed : collapsed[..max].TrimEnd() + "…";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // The manager owns session teardown — cancel every session's Cts + pending
        // action cards (a WaitingForTool session at shutdown is otherwise an
        // abandoned TaskCompletionSource).
        foreach (var session in _allSessions)
        {
            session.StateChanged -= OnSessionStateChanged;
            session.TurnCompleted -= OnSessionTurnCompleted;
            session.Dispose();
        }
        _allSessions.Clear();
        _sessions.Clear();
    }
}
