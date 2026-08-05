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
            or AgentRunState.WaitingForInput or AgentRunState.Running
            // Batch 08 G8: a user-paused run needs the SAME ActionRequired card WaitingForInput gets, or a run
            // the user paused from a background chat is invisible forever — the startup sweep never touches
            // Paused (AgentRunService.cs's `State < @Terminal` excludes it by design, W15). Widened in the SAME
            // edit as the arm below, per the method's own doc comment: any state past this filter with no arm
            // of its own publishes a "run finished" item for a run that is still working.
            or AgentRunState.Paused;

    /// <summary>
    /// Which "continue?" body a parked run's Flow card carries. Extracted and internal for the same reason
    /// <see cref="IsPublishableState"/> is: the publish itself sits inside a dispatcher-marshalled handler, and an
    /// unknown reason must fall back to the BUDGET wording, which is what every pause the run loop writes for
    /// itself really is.
    /// </summary>
    internal static string PausedBodyKey(string? reason) => reason switch
    {
        // hermes #16: the ONE body that takes an argument (the tool name) — see PausedBody, which is what the
        // publish calls. The key is still mapped here so the vocabulary lives in one switch.
        AgentRunOrchestrator.ToolApprovalReason => "Flow_Run_ToolApproval",
        AgentRunOrchestrator.ChildrenParkedReason => "Flow_Run_ChildrenParked",
        AgentRunService.ChildrenInterruptedReason => "Flow_Run_ChildrenInterrupted",
        // 18 D5/§4.4: the run Goal and the model's clarification question are SENSITIVE and never reach this
        // item — only the reason token may. The body routes the user to the chat, which is where 18 G3 posts
        // the question itself (SafePostClarificationQuestionAsync). Two tokens, two keys, because the two
        // RESUME differently (18 Q4) even though today's copy for them reads the same shape as every other arm.
        AgentRunOrchestrator.NeedsGoalReason => "Flow_Run_NeedsGoal",
        AgentRunOrchestrator.NeedsInputReason => "Flow_Run_NeedsInput",
        // Batch 08 G2. Telling a user who pressed Pause that the run "stopped at its budget" would send them
        // to raise budgets that were never reached; the card still carries the same ContinueRunAction.
        AgentRunService.UserPausedReason => "Flow_Run_UserPaused",
        // Batch 08 F19: the second of the two readers UserPausedReason's doc names. A Continue that claimed
        // the row and then failed to start re-parks it here, and "Stopped at its budget — continue?" sends
        // the user to Settings instead of back to the button they just pressed.
        HeadlessRunLauncher.ResumeInterruptedReason => "Flow_Run_ResumeInterrupted",
        _ => "Flow_Run_WaitingAtBudget",
    };

    /// <summary>
    /// hermes #16. The rendered "continue?" body for a parked run. Split from <see cref="PausedBodyKey"/>
    /// because the APPROVAL body needs an argument the key cannot carry, and the argument is load-bearing:
    /// Continue on an approval park IS the grant, so a card that does not say which tool it is granting asks
    /// the user to approve something blind.
    /// <para>
    /// The tool NAME is app/plugin-defined and never user content — the same property that already lets this
    /// card key its body on the reason token — unlike the run Goal, which stays out of the Flow item entirely.
    /// An approval envelope whose name did not survive formats an empty one rather than falling through to
    /// the budget wording: a vague prompt is a degrade, "stopped at its budget" would be a lie.
    /// </para>
    /// </summary>
    internal static string PausedBody(ILocalizationService localization, AgentRun run) =>
        PausedBody(localization, run, RunPauseEnvelope.ReadReason(run));

    /// <summary>
    /// 18 G3: overload that takes an already-read <paramref name="reason"/>, for the one caller
    /// (<see cref="HandleRunStateAsync"/>) that also branches the card's Action on that same token — so the
    /// envelope's <c>ExtraJson</c> is parsed once per publish, not twice.
    /// </summary>
    private static string PausedBody(ILocalizationService localization, AgentRun run, string? reason)
    {
        var key = PausedBodyKey(reason);
        return reason == AgentRunOrchestrator.ToolApprovalReason
            ? localization.Format(key, RunPauseEnvelope.ReadApprovalTool(run) ?? string.Empty)
            : localization[key];
    }

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

        // A DELEGATED run is not a run the user started (Batch 07 D7/D17): it lives in its own stub chat the
        // user never opened, and the PARENT's item already represents the whole fan-out. Without this filter a
        // clean 3-way fan-out published four durable Flow items and four toasts for one run started once, three
        // of them pointing at chats that exist only as a delegation vehicle.
        //
        // The WaitingForInput arm below is the sharper reason it is a filter and not a preference: a child that
        // parks at its own halved budget would publish an ActionRequired card carrying a ContinueRunAction on
        // the CHILD run id — a transition nothing supports. A child is only ever re-dispatched by its parent's
        // fan-out, and answering the child's card instead of the parent's resumes it on the child slot pool with
        // nothing linking it back to the parent's step, so the parent then re-runs that same work in-process.
        if (run.ParentRunId is not null)
            return;

        // R18: suppress ONLY the chat the user is actively watching in the foreground — its embedded
        // run-progress panel already reflects the state (incl. the WaitingForInput Continue button). A
        // headless run's chat is never the active session, so it always publishes.
        if (_windowManager.IsInForeground(WindowMode.Assistant)
            && _windowManager.ActiveAssistantChatId == run.ChatId)
            return;

        if (AgentRunStates.IsParked(state))
        {
            // 18 D5/§4.4: a needs-goal/needs-input park has NO answer for "Continue" to carry — every other
            // reason's ContinueRunAction resumes the run out-of-band with nothing typed, which is correct for
            // a budget/approval/children park (there is nothing more to say) but wrong here, because the
            // answer lives in the run's OWN CHAT (18 G3 posts the question there), never on this card. So
            // these two reasons get an action that ROUTES to the run instead of firing a blind resume —
            // "the card only says a run is waiting, and routes there" is §4.4's own wording for exactly this.
            // Every other reason is unchanged.
            //
            // 18 G3 review fix: OpenParkedRunAction, NOT OpenRunAction, even though both navigate to the same
            // place — see that type's doc. Opening the run resolves nothing (it is still WaitingForInput
            // right after the click), so retracting on open — OpenRunAction's behaviour, correct for a
            // terminal card and for ContinueRunAction's actual resume — would delete the only durable trace
            // of a still-parked run the moment the user looks and looks away without answering.
            var reason = RunPauseEnvelope.ReadReason(run);
            var needsAnswerElsewhere = reason == AgentRunOrchestrator.NeedsGoalReason
                || reason == AgentRunOrchestrator.NeedsInputReason;

            _flowService.Publish(new FlowItemDraft
            {
                Severity = FlowSeverity.ActionRequired,
                Source = FlowSource.AgentRun,
                // Generic title/body — the run Goal + pause reason are SENSITIVE, never in the Flow item.
                Title = _localizationService["Flow_Run_Title"],
                // Three reasons reach WaitingForInput and only one is a budget (07 D13/D14). The REASON token is
                // app-owned and never user content — unlike the run Goal, which stays out of the Flow item
                // entirely — so it is safe to key the body on it, and announcing a child's park or a restart as
                // "stopped at its budget" sends the user to raise budgets that were never reached.
                // hermes #16 added a FOURTH reason and it is the first one that names something: an approval
                // park's body carries the tool the Continue button is about to grant. 18 added a FIFTH and
                // SIXTH — see the Action split above; their body still keys off the same token like every
                // other arm, and the model's own question text is NEVER in it (§8.6).
                Body = PausedBody(_localizationService, run, reason),
                DedupKey = runId.ToString(),
                Lifetime = FlowLifetime.Persistent,
                Action = needsAnswerElsewhere
                    ? new OpenParkedRunAction(runId, _localizationService["Flow_Action_OpenRun"])
                    : new ContinueRunAction(runId, _localizationService["Flow_Action_ContinueRun"]),
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
