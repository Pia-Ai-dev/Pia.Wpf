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
    private readonly IProviderCapabilityService _providerCapabilityService;
    private readonly IHeadlessRunLauncher _headlessRunLauncher;
    private readonly IWindowManagerService _windowManager;
    private readonly IExecutingRunStore _executingRuns;
    private readonly IAgentTimelineService? _agentTimelineService;
    private readonly SynchronizationContext _syncContext;

    /// <summary>Per-file line cap for <c>@Files</c> content injected directly into the prompt.</summary>
    private const int FilePreviewLines = 100;

    /// <summary>Max distinct <c>@Files</c> files whose content is injected in one turn (others rely on tools).</summary>
    private const int MaxFilePreviews = 5;

    private readonly Dictionary<Guid, ChatSession> _sessions = new();
    private readonly HashSet<ChatSession> _allSessions = new();

    /// <summary>
    /// Runs this manager launched into a live session itself (W2). Their <c>RunChanged</c> events must NOT set
    /// <see cref="ChatSession.ForeignRunActive"/> — the owning session's <c>IsStreaming</c> already blocks
    /// Send, and flagging it would disable the composer for an ordinary interactive agent run.
    /// <para>
    /// Entries are RETIRED by <see cref="OnAgentRunChanged"/> the moment the run stops executing (parked or
    /// terminal), because that is where the live executor hands it back: a resume always runs unattended
    /// through <c>HeadlessRunLauncher</c>, so from then on the run is a foreign writer like any other. A
    /// never-pruned set would exempt exactly the most likely two-writer path in the product — an
    /// interactively launched run that parks at its budget and is resumed with Continue — from the Send lever.
    /// </para>
    /// </summary>
    private readonly HashSet<Guid> _ownRunIds = new();

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
        IAgentRunService agentRunService,
        IProviderCapabilityService providerCapabilityService,
        IHeadlessRunLauncher headlessRunLauncher,
        IWindowManagerService windowManager,
        IExecutingRunStore executingRuns,
        // Batch 03: handed to LiveTurnExecutor so an interactive Planned run records its tool decisions.
        // Trailing and defaulted so the one hand-constructed test site keeps compiling unchanged; the
        // container resolves it because it is registered.
        IAgentTimelineService? agentTimelineService = null)
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
        _providerCapabilityService = providerCapabilityService;
        _headlessRunLauncher = headlessRunLauncher;
        _windowManager = windowManager;
        _executingRuns = executingRuns;
        _agentTimelineService = agentTimelineService;
        _syncContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("ChatSessionManager must be created on the UI thread");

        // W2: track whether a chat's attached run is executing under a FOREIGN (headless) executor, so the
        // composer can refuse to start a second full-chat writer. Unsubscribed in Dispose.
        _agentRunService.RunChanged += OnAgentRunChanged;
    }

    /// <summary>
    /// W2: a run attached to a live session moved state. Planning/Running/Verifying means a foreign executor
    /// is writing this chat; WaitingForInput/Paused/terminal means it is not (and the parked
    /// "continue in chat" path must stay open).
    /// <para>
    /// Off-thread by construction — <c>AgentRunService</c> raises <c>RunChanged</c> from whatever pool thread
    /// completed a step, under no gate of ours — so the flag flip is marshaled to the UI thread (G3), which
    /// is also where the session's own <c>ActiveRunChanged</c> is raised. Bookkeeping: a fault here must never
    /// fail the run (guardrail 1), and the handler owns the marshal so a throwing continuation cannot
    /// propagate back into <c>RecordStepResultAsync</c>'s caller.
    /// </para>
    /// <para>
    /// A2: recomputed from the launch-bracket index (<see cref="IExecutingRunStore"/>) keyed on the CHAT id,
    /// reverse-looked-up from the run id because the event carries no chat id. That is what gates a
    /// <see cref="RunShape.SingleTurn"/> background turn, which no session ever has attached and which was
    /// therefore gated nowhere. UNIONED with the older <see cref="ChatSession.ActiveRunId"/> match, which the
    /// index alone cannot replace: <c>TryBeginResumeAsync</c> raises <c>RunChanged(Running)</c> at its CAS,
    /// before the launcher's post-slot registration, so a resume of a run attached to an already-open chat
    /// would otherwise leave the composer live until the run's next state change.
    /// A run this manager launched itself (<see cref="_ownRunIds"/>) is skipped WHILE IT EXECUTES: that
    /// session's own <c>IsStreaming</c> already blocks Send, and flagging it would be an interactive
    /// regression. Its first non-executing state retires the ownership entry (see below), so a later headless
    /// resume of that same run is flagged like any other foreign writer.
    /// </para>
    /// </summary>
    private void OnAgentRunChanged(object? sender, AgentRunChangedEventArgs e)
    {
        var executing = e.State is AgentRunState.Planning or AgentRunState.Running or AgentRunState.Verifying;
        _syncContext.Post(_ =>
        {
            try
            {
                if (_disposed) return;

                // A2: the chat this run's LAUNCH BRACKET names, captured BEFORE the release below. The event
                // carries no chat id, so this reverse lookup is the only way the handler knows which chat the
                // run belongs to — and it must be read first, or the release would erase the answer.
                var bracketedChatId = _executingRuns.GetChatId(e.RunId);

                // A2: release from HERE as well as from the launcher's finally, whichever runs first (Release
                // is idempotent). Load-bearing: AgentRunService raises RunChanged OUTSIDE its gate and BEFORE
                // the launcher task's finally releases, so a handler landing in that gap would otherwise
                // recompute "still executing" from an entry that is about to vanish, with no further event
                // ever to correct it — and a stale true is unrecoverable, because re-activating takes the
                // live-attach branch (no re-seed) and RestoreActiveRunAsync early-returns once a run is
                // attached. A missing entry is only the original race; a stuck one is a dead composer.
                //
                // Every open window's manager runs this, so several release the same key. That converges: the
                // release only fires when the run is NOT executing, and any other window's recompute then
                // evaluates `false || (executing && ...)` = false — the same answer. One window's release can
                // never leave another window's session stale.
                if (!executing)
                    _executingRuns.Release(e.RunId);

                // Checked HERE, not on the raising thread: _ownRunIds is written by the UI-thread Planned
                // branch, so probing it from a pool thread would be a data race on a plain HashSet.
                var isOwnRun = _ownRunIds.Contains(e.RunId);
                if (isOwnRun)
                {
                    // Ownership lasts exactly as long as the LIVE executor is the one running the run. The
                    // first non-executing state — parked at its step budget (WaitingForInput/Paused) or
                    // terminal — is where this manager hands the run back: EVERY resume path in the app goes
                    // through HeadlessRunLauncher (RunProgressViewModel.Continue, the Flow "continue?" card,
                    // IAgentRunResumeService), i.e. unattended and FOREIGN, writing the same chat from a pool
                    // thread while IsStreaming is false and the composer is live again. So retire the entry
                    // here: the next executing state is then treated as what it is — a second full-chat
                    // writer — and the set stops growing for the lifetime of the process.
                    // Retired only when it STOPS executing; a still-executing own run keeps its exemption.
                    // (No early return any more — the recompute below is keyed on chats, so bailing out here
                    // would also skip every OTHER session's recompute.)
                    if (!executing)
                        _ownRunIds.Remove(e.RunId);
                }

                foreach (var session in _allSessions)
                {
                    // The session that holds THIS run, whether or not it has a chat id yet. An id is NOT a
                    // precondition: StartPlannedTurnAsync attaches a run to a brand-new session, and that
                    // session's id is only assigned when its first turn persists — so a run can absolutely be
                    // attached to an id-less chat, and the pre-A2 handler gated exactly that case by matching
                    // ActiveRunId with no id requirement. Requiring an id here silently dropped it, and the
                    // race between the persist and the event made the loss intermittent.
                    var holdsThisRun = session.ActiveRunId == e.RunId;

                    // Only the sessions this event can actually speak for: the chat the bracket names, or the
                    // session holding this very run. Anything else keeps what it was seeded with — notably
                    // RestoreActiveRunAsync's post-restart backfill for a run that began before this process
                    // and so has no in-process bracket, which a blanket recompute would wrongly clear.
                    var isBracketedChat = session.Id is { } id && id == bracketedChatId;
                    if (!isBracketedChat && !holdsThisRun)
                        continue;

                    var foreign = (session.Id is { } chatId && _executingRuns.IsExecuting(chatId))
                        // Unioned with the pre-A2 rule: a resume's CAS raises RunChanged(Running) BEFORE the
                        // launcher's post-slot Register, so the index is briefly empty for it. This term only
                        // ever asserts a true that THIS event justifies, so it cannot strand a stale one. Own
                        // runs stay exempt while they execute — their session's IsStreaming already blocks
                        // Send, and flagging them would be an interactive regression.
                        || (executing && !isOwnRun && holdsThisRun);

                    session.SetForeignRunActive(foreign);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to apply a run-state change to a live session");
            }
        }, null);
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
        // E2: and every completed step of an in-flight Planned run (persist-only, no terminal settle).
        session.PersistRequested += OnSessionPersistRequested;
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

    /// <summary>
    /// E2: per-step durability for an interactive Planned run. The live executor asks for this after every
    /// completed step, because the interactive transcript otherwise reaches the store only through the
    /// terminal <see cref="OnSessionTurnCompleted"/> — and a run parked at its budget never gets there, so
    /// the stored chat held at most the goal row. Same <see cref="PersistAsync"/> the terminal path uses,
    /// minus the auto-title trigger: leaving that to the terminal persist keeps titling behaviour identical
    /// to before and keeps the rename's read-modify-write off a chat that is still growing. Fire-and-forget
    /// + swallowed (SafeFireAndForget) so a persist fault never fails the step (guardrail 1).
    /// </summary>
    private void OnSessionPersistRequested(object? sender, EventArgs e)
    {
        if (sender is ChatSession session)
            PersistAsync(session, startAutoTitle: false).SafeFireAndForget(_logger);
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

        // R18: publish the active chat id so the terminal-run Flow surface suppresses a notification only
        // for the chat the user is actively watching (a headless run's chat is never active → always notifies).
        _windowManager.SetActiveAssistantChatId(session.Id);

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
        session.PersistRequested -= OnSessionPersistRequested;
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

        // A2 (closes W2c): seed the composer gate SYNCHRONOUSLY from the launch-bracket index, BEFORE
        // SetActive makes the composer live. SetActive raises ActiveChanged, which is the instant the view
        // model attaches and reads ChatSession.ForeignRunActive — so with no await, no lock, no flicker and no
        // dropped Enter press, Send is already refused for a chat a headless run is writing. Covers the
        // RunShape.SingleTurn case too, which is attached to no session and so was gated nowhere.
        session.SetForeignRunActive(_executingRuns.IsExecuting(chat.Id));

        SetActive(session);

        _logger.LogInformation("Resumed chat {ChatId} ({MessageCount} messages)", chat.Id, chat.Messages.Count);
        _logger.SensitiveDebug("Resumed chat {ChatId} title: {Title}", chat.Id, chat.Title);

        // C2: ActiveRunId is runtime-only (stamped once when the run is created), so a chat hydrated after
        // an app restart had no run-progress panel and no Continue button even when its run is still parked
        // — and the Flow WaitingForInput card is suppressed for the foreground active chat, leaving the run
        // durable but unreachable. Re-attach it. Fire-and-forget: an activation must never fail or stall on
        // this lookup. Only the hydrate path needs it — the live-attach branch above returns a session that
        // already carries its run id (SetActiveRun ran when the run was created).
        //
        // W2c is CLOSED (A2): the composer gate no longer waits on this lookup — it was seeded synchronously
        // from the launch-bracket index above, before SetActive. What remains here is the BACKFILL for a run
        // that began before this process started (after an app restart), where no in-process bracket ever
        // fired: only the persisted rows know about it. Still fire-and-forget on a pool thread, because
        // IAgentRunService is a synchronous lock-holding store and an activation must never stall on it.
        RestoreActiveRunAsync(session).SafeFireAndForget(_logger);

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
    /// C2: re-attach the chat's newest NON-terminal <see cref="RunShape.Planned"/> run to a freshly
    /// hydrated session, so the run-progress panel — and with it the Continue command of a run parked at
    /// its budget — comes back after an app restart (it also gives Flow's <c>OpenRun</c> a panel to open in
    /// a later session). Contract:
    /// <list type="bullet">
    /// <item>Failure-isolated (guardrail 1): a lookup fault logs a warning and leaves the chat panel-less
    /// rather than failing the activation.</item>
    /// <item>Off the hot path: <see cref="IAgentRunService"/> is a synchronous lock-holding store, so the
    /// read happens on a pool thread — a live headless run holding that lock must never stall the UI.
    /// Exactly ONE query per activation, and none at all once a session carries a run.</item>
    /// <item>A terminal run (Completed/Failed/Cancelled) is never resurrected, and an already-attached run
    /// is never replaced (re-checked after the await).</item>
    /// </list>
    /// UI-affine on purpose (no <c>ConfigureAwait(false)</c>): the continuation resumes on the
    /// SynchronizationContext the activation was invoked on, so the <c>ActiveRunChanged</c> that
    /// <see cref="ChatSession.SetActiveRun"/> raises reaches the view model on the UI thread (G3).
    /// </summary>
    internal async Task RestoreActiveRunAsync(ChatSession session)
    {
        if (session.Id is not { } chatId || session.ActiveRunId is not null)
            return;

        IReadOnlyList<AgentRun>? runs;
        try
        {
            runs = await Task.Run(() => _agentRunService.GetByChatAsync(chatId));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to look up a resumable run for chat {ChatId}", chatId);
            return;
        }

        var resumable = runs?
            .Where(r => r.RunShape == RunShape.Planned
                        && r.State is AgentRunState.Planning or AgentRunState.Running or AgentRunState.Verifying
                            or AgentRunState.WaitingForInput or AgentRunState.Paused)
            .OrderBy(r => r.CreatedAt)
            .LastOrDefault();
        if (resumable is null || _disposed || session.ActiveRunId is not null)
            return;

        session.SetActiveRun(resumable.Id);

        // W2: a HYDRATED session never executes its own run — this manager did not launch it (the run
        // predates this session, often this process). So a re-attached run that is still EXECUTING is by
        // definition foreign, i.e. a second full-chat writer, and Send must be blocked until it stops.
        // WaitingForInput/Paused deliberately do NOT set it: the parked "continue in chat" path stays open.
        // A2: OR'd with the launch-bracket index, never assigned from the row alone — this backfill runs
        // AFTER the synchronous seed in ActivateAsync, and a bare assignment would clear a live
        // RunShape.SingleTurn bracket on a chat that also happens to carry a parked Planned run.
        session.SetForeignRunActive(
            _executingRuns.IsExecuting(chatId)
            || resumable.State is AgentRunState.Planning or AgentRunState.Running or AgentRunState.Verifying);

        // Ids + enum only — a run Goal is user content (CLAUDE.md privacy logging).
        _logger.LogInformation("Re-attached run {RunId} ({State}) to activated chat {ChatId}",
            resumable.Id, resumable.State, chatId);
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

            // suggest_agent_mode eligibility (R7/§14.3): only an interactive Chat turn (never a Planned
            // dispatch) on a tool-Capable provider may offer the switch. Capability is async + cached, so
            // pre-resolve it here (F2 — keeps PrepareTurn synchronous). Swallowed inside the service on
            // failure (Unknown/Weak) → never throws into the turn, never hard-blocks.
            var providerToolCapable = !planned
                && await _providerCapabilityService.GetPlanningCapabilityAsync(provider, session.Cts!.Token)
                    == PlanningCapability.Capable;

            var turnSetup = _promptComposer.PrepareTurn(persona, provider, atCommands, tokenizationEnabled,
                suggestAgentModeEligible: !planned && providerToolCapable);
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

            // D1 producer for the INTERACTIVE origin. An interactive run holds no standing write grant at
            // all: every write_file goes through an action card the user clicks (write_file is not in the
            // auto-approve allowlist — ChatSession's gate). A resume, however, runs UNATTENDED through
            // HeadlessRunLauncher, and a run with no envelope falls back to the {write_file} resume floor —
            // so parking would ESCALATE this run's authority to card-free writes with nobody watching.
            // Persist the honoured-empty envelope instead: the resume restores "no write grants", which is
            // exactly what the launch had. Bookkeeping, so a serializer fault must not fail the turn
            // (guardrail 1); null degrades to the floor, which is the documented fallback.
            // ONE settings read for all three consumers below — the envelope, the LiveTurnExecutor and the
            // RunProfile (04 D11). Two reads could straddle a settings save and give the persisted envelope
            // and the live executor DIFFERENT policies, i.e. a run whose record disagrees with what it did.
            var settings = await _settingsService.GetSettingsAsync();
            var policy = RunAutonomyPolicy.FromSettings(settings);

            // On a serializer fault, fall back to the exact policy-less EMPTY-grant document rather than to
            // null (04 D12): null makes the resume apply the {write_file} floor, which is WIDER than what this
            // launch granted (nothing), and this batch makes the document richer, i.e. likelier to fault. The
            // fallback deliberately carries no policy either — narrower on fault is the only safe direction.
            string? policyJson;
            try { policyJson = HeadlessRunLauncher.SerializeGrantEnvelope([], AgentRunTrigger.User, policy); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to serialize the interactive run's grant envelope");
                policyJson = HeadlessRunLauncher.InteractiveEmptyEnvelopeJson;
            }

            var run = await _agentRunService.CreateAsync(new AgentRunCreateRequest(
                session.Id!.Value, RunShape.Planned, AgentRunTrigger.User, Goal: userText, PolicyJson: policyJson));

            // W2: this session EXECUTES this run itself (LiveTurnExecutor below), so it is not a foreign
            // writer. Recorded before SetActiveRun so no RunChanged can race in and flag the session; the
            // flag is deliberately left false here — session.IsStreaming already blocks Send.
            _ownRunIds.Add(run.Id);

            // Surface the run id onto the session so the active VM can embed the run-progress panel
            // (§15.1). Raised on the UI thread (this branch runs on it), so the VM handler is safe.
            session.SetActiveRun(run.Id);

            // BeginTurn() above already created session.Cts; the Planned branch does NOT call BeginTurn
            // per step (R13). The orchestrator links the run CTS from session.Cts.Token below, so
            // ChatSession.Cancel() propagates to the run + in-flight step. Constructed on the UI thread
            // so the LiveTurnExecutor captures the UI SynchronizationContext.
            var live = new LiveTurnExecutor(session, IsSessionActive,
                PersonaAttribution.From(persona), request.Provider, request.TurnSetup, request.TokenizationEnabled,
                policy, _agentTimelineService);

            // Budget envelope from user settings (clamped in FromBudget); defaults match RunProfile.Interactive.
            var profile = RunProfile.FromBudget(settings.AgentMaxSteps, settings.AgentMaxReplans, settings.AgentWallClockMinutes);

            _agentRunOrchestrator
                .RunAsync(run, live, persona, request.Provider, profile, session.Cts!.Token)
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

    public Task PersistAsync(ChatSession session) => PersistAsync(session, startAutoTitle: true);

    private async Task PersistAsync(ChatSession session, bool startAutoTitle)
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

        if (startAutoTitle)
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

            // W2: a TITLE-ONLY write. This used to be GetAsync -> mutate Title -> SaveAsync, i.e. a
            // fire-and-forget read-modify-write with a full-chat replace at the end. Started from
            // TryStartAutoTitleAsync, its snapshot is routinely stale by the time it writes, so it could
            // revert message rows a headless step had appended in between — the auto-title path was a second
            // effective writer on the chat row. SetTitleAsync touches Title/UpdatedAt + the FTS row only, so
            // it cannot lose a message no matter who else wrote.
            if (!await _chatService.SetTitleAsync(chatId, title))
            {
                _logger.LogWarning("Auto-title: chat {ChatId} disappeared before rename", chatId);
                return;
            }

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

    /// <summary>
    /// Detach <paramref name="goal"/> as an unattended headless Planned run (no live session). Additive
    /// to <see cref="StartTurnAsync"/> — it never touches the interactive session/CTS/active-run state
    /// (G-6); the launcher runs it on a fresh DI scope with its own CTS.
    /// </summary>
    public Task StartBackgroundRunAsync(string goal) =>
        _headlessRunLauncher.LaunchAsync(new HeadlessRunRequest(goal, AgentRunTrigger.User));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // R18: this window no longer owns an active assistant chat.
        _windowManager.SetActiveAssistantChatId(null);

        // W2: the run store is a singleton and outlives this scoped manager — leaving the handler attached
        // would keep a disposed window's sessions alive and post to a dead SynchronizationContext.
        _agentRunService.RunChanged -= OnAgentRunChanged;

        // The manager owns session teardown — cancel every session's Cts + pending
        // action cards (a WaitingForTool session at shutdown is otherwise an
        // abandoned TaskCompletionSource).
        foreach (var session in _allSessions)
        {
            session.StateChanged -= OnSessionStateChanged;
            session.TurnCompleted -= OnSessionTurnCompleted;
            session.PersistRequested -= OnSessionPersistRequested;
            session.Dispose();
        }
        _allSessions.Clear();
        _sessions.Clear();
    }
}
