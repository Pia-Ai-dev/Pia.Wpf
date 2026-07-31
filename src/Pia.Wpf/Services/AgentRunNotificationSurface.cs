using System.Windows;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Models;
using Pia.Models.Flow;
using Pia.Services.Flow;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>
/// Publishes a durable Flow item when a Planned agent run reaches a terminal state while the assistant
/// window is NOT focused (§15.4, R18). Mirrors <see cref="ScheduledJobNotificationSurface"/>'s Flow-publish
/// shape. Subscribes to <see cref="IAgentRunService.RunChanged"/> in the ctor and marshals every handler to
/// the UI thread (G3 — the event may fire off-thread).
/// </summary>
/// <remarks>
/// R18 seam: the only Phase-1-reachable publishing case is an unfocused-window INTERACTIVE Planned run
/// reaching terminal state (no headless Planned producer exists in Phase 1). A foreground run publishes
/// nothing — the embedded run-progress panel already reflects terminal state.
/// <para>
/// R17 deletion-side: a durable OpenRun item must not dangle once its chat (and, by FK CASCADE, its run)
/// is deleted. The surface records which run ids it published per chat id and, on a local chat deletion
/// (<see cref="IAssistantChatService.ChatsChanged"/> with <see cref="AssistantChatChangeKind.Deleted"/>),
/// retracts them. The map is in-memory (same-session); a durable item published in a prior session still
/// self-heals when opened (<c>WindowManagerService.ShowAgentRunAsync</c> → missing run → Retract + toast).
/// Remote deletions raise no event (<c>DeleteFromRemoteAsync</c> uses <c>raiseEvent:false</c>), so only
/// user-initiated deletes flow through here.
/// </para>
/// </remarks>
public sealed class AgentRunNotificationSurface : IAgentRunNotificationSurface
{
    private readonly IAgentRunService _runService;
    private readonly IFlowService _flowService;
    private readonly IWindowManagerService _windowManager;
    private readonly IAssistantChatService _chatService;
    private readonly ILocalizationService _localizationService;
    private readonly ILogger<AgentRunNotificationSurface> _logger;

    // chat id → run ids this surface has published a durable Flow item for (R17 deletion-side).
    private readonly Dictionary<Guid, HashSet<Guid>> _publishedByChat = new();
    // run ids with a live WaitingForInput ("continue?") card — gates the retract so an ordinary
    // step-Running event never issues a spurious Retract (D6).
    private readonly HashSet<Guid> _waitingPublished = new();
    private readonly object _publishedLock = new();

    public AgentRunNotificationSurface(
        IAgentRunService runService,
        IFlowService flowService,
        IWindowManagerService windowManager,
        IAssistantChatService chatService,
        ILocalizationService localizationService,
        ILogger<AgentRunNotificationSurface> logger)
    {
        _runService = runService;
        _flowService = flowService;
        _windowManager = windowManager;
        _chatService = chatService;
        _localizationService = localizationService;
        _logger = logger;
        _runService.RunChanged += OnRunChanged;   // eager subscribe; nothing else references this surface
        _chatService.ChatsChanged += OnChatsChanged; // R17: retract durable OpenRun items on chat deletion
    }

    /// <summary>
    /// Which run states this surface reacts to at all: terminal (publish), <c>WaitingForInput</c> (publish a
    /// "continue?" card), <c>Running</c>/<c>Cancelled</c> (retract a prior card — a resumed or cancelled parked
    /// run). Everything else is ignored, and that includes Batch 07's
    /// <see cref="AgentRunState.WaitingForChildren"/>: a delegating parent is not user-actionable, so it must
    /// raise no toast and no Flow card.
    /// <para>
    /// Extracted rather than left inline so it can be pinned by a test, because widening it is not a harmless
    /// mistake: <see cref="HandleRunStateAsync"/>'s final arm is the TERMINAL publish, so any state that gets
    /// past this filter without an arm of its own publishes a "run finished" item for a run that is still
    /// working.
    /// </para>
    /// </summary>
    internal static bool IsPublishableState(AgentRunState state) =>
        state is AgentRunState.Completed or AgentRunState.Failed or AgentRunState.Cancelled
            or AgentRunState.WaitingForInput or AgentRunState.Running;

    private void OnRunChanged(object? sender, AgentRunChangedEventArgs e)
    {
        if (!IsPublishableState(e.State))
            return;

        // Marshal to the UI thread (G3) before touching window-foreground state / Flow.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            HandleRunStateAsync(e.RunId, e.State).SafeFireAndForget(_logger);
        else
            dispatcher.InvokeAsync(() => HandleRunStateAsync(e.RunId, e.State).SafeFireAndForget(_logger));
    }

    internal async Task HandleRunStateAsync(Guid runId, AgentRunState state)
    {
        // Retract-only transitions: a parked run resumed (→Running) or was cancelled while parked. No
        // publish and no Planned/foreground filter — just drop the WaitingForInput card if we posted one.
        // Gated on _waitingPublished so an ordinary per-step Running event issues no spurious Retract.
        if (state is AgentRunState.Running or AgentRunState.Cancelled)
        {
            RetractWaiting(runId);
            return;
        }

        var run = await _runService.GetAsync(runId);
        if (run is null || run.RunShape != RunShape.Planned)
            return; // Planned-only

        // R18: suppress ONLY the chat the user is actively watching in the foreground — its embedded
        // run-progress panel already reflects the state (incl. the WaitingForInput Continue button). A
        // headless run's chat is never the active session, so it always publishes.
        if (_windowManager.IsInForeground(WindowMode.Assistant)
            && _windowManager.ActiveAssistantChatId == run.ChatId)
            return;

        if (state == AgentRunState.WaitingForInput)
        {
            _flowService.Publish(new FlowItemDraft
            {
                Severity = FlowSeverity.ActionRequired,
                Source = FlowSource.AgentRun,
                // Generic title/body — the run Goal + pause reason are SENSITIVE, never in the Flow item.
                Title = _localizationService["Flow_Run_Title"],
                Body = _localizationService["Flow_Run_WaitingAtBudget"],
                DedupKey = runId.ToString(),
                Lifetime = FlowLifetime.Persistent,
                Action = new ContinueRunAction(runId, _localizationService["Flow_Action_ContinueRun"]),
                RequestDurable = true,
            });

            lock (_publishedLock)
            {
                RecordPublishedForChat(run.ChatId, runId);
                _waitingPublished.Add(runId);
            }
            return;
        }

        // Completed / Failed (terminal) — unchanged publish. The shared DedupKey (run id) reconciles the
        // terminal item onto any prior WaitingForInput card via FlowService dedup (retract-on-terminal).
        var completed = state == AgentRunState.Completed;
        _flowService.Publish(new FlowItemDraft
        {
            Severity = completed ? FlowSeverity.Success : FlowSeverity.Error,
            Source = FlowSource.AgentRun,
            // Generic title/body — the run Goal + failure reason are SENSITIVE, never in the Flow item.
            Title = _localizationService["Flow_Run_Title"],
            Body = _localizationService[completed ? "Flow_Run_Completed" : "Flow_Run_Failed"],
            DedupKey = runId.ToString(),
            Lifetime = FlowLifetime.Persistent,
            Action = new OpenRunAction(runId, _localizationService["Flow_Action_OpenRun"]),
            RequestDurable = true,
        });

        // R17: remember runId→chatId so a later chat deletion retracts this durable item. The terminal
        // item supersedes any WaitingForInput card (shared DedupKey), so drop the waiting flag.
        lock (_publishedLock)
        {
            RecordPublishedForChat(run.ChatId, runId);
            _waitingPublished.Remove(runId);
        }
    }

    // Records runId under its chat for R17 deletion-side retraction. Caller holds _publishedLock.
    private void RecordPublishedForChat(Guid chatId, Guid runId)
    {
        if (!_publishedByChat.TryGetValue(chatId, out var runs))
            _publishedByChat[chatId] = runs = new HashSet<Guid>();
        runs.Add(runId);
    }

    // Retracts a live WaitingForInput card (if any) and clears its bookkeeping from both sets (D6).
    private void RetractWaiting(Guid runId)
    {
        bool wasPublished;
        lock (_publishedLock)
        {
            wasPublished = _waitingPublished.Remove(runId);
            if (wasPublished)
                foreach (var runs in _publishedByChat.Values)
                    runs.Remove(runId);
        }

        if (wasPublished)
            _flowService.Retract(runId.ToString());
    }

    private void OnChatsChanged(object? sender, AssistantChatChangedEventArgs e)
    {
        if (e.Kind != AssistantChatChangeKind.Deleted)
            return;

        // Marshal to the UI thread (G3) before touching Flow. ChatsChanged fires from the local
        // delete choke point (DeleteCoreAsync, raiseEvent:true); remote deletes never raise it.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            HandleChatDeleted(e.Id);
        else
            dispatcher.InvokeAsync(() => HandleChatDeleted(e.Id));
    }

    /// <summary>
    /// R17 deletion-side: retract every durable OpenRun Flow item this surface published for a deleted
    /// chat's runs, so no item dangles after the chat (and its cascaded runs) are gone. Retracting an
    /// already-opened/retracted item is a harmless no-op.
    /// </summary>
    internal void HandleChatDeleted(Guid chatId)
    {
        HashSet<Guid>? runs;
        lock (_publishedLock)
        {
            if (!_publishedByChat.TryGetValue(chatId, out runs))
                return;
            _publishedByChat.Remove(chatId);
        }

        foreach (var runId in runs)
            _flowService.Retract(runId.ToString());
    }
}
