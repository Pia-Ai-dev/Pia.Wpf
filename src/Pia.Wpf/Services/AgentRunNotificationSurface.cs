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

    private void OnRunChanged(object? sender, AgentRunChangedEventArgs e)
    {
        if (e.State is not (AgentRunState.Completed or AgentRunState.Failed))
            return; // terminal only

        // Marshal to the UI thread (G3) before touching window-foreground state / Flow.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            HandleTerminalAsync(e.RunId, e.State).SafeFireAndForget(_logger);
        else
            dispatcher.InvokeAsync(() => HandleTerminalAsync(e.RunId, e.State).SafeFireAndForget(_logger));
    }

    internal async Task HandleTerminalAsync(Guid runId, AgentRunState state)
    {
        var run = await _runService.GetAsync(runId);
        if (run is null || run.RunShape != RunShape.Planned)
            return; // Planned-only

        // R18: suppress ONLY the chat the user is actively watching in the foreground — its embedded
        // run-progress panel already reflects terminal state. A headless run's chat is never the active
        // session, so it always publishes; this also fixes the interactive background-chat silent-drop.
        if (_windowManager.IsInForeground(WindowMode.Assistant)
            && _windowManager.ActiveAssistantChatId == run.ChatId)
            return;

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

        // R17: remember runId→chatId so a later chat deletion retracts this durable item.
        lock (_publishedLock)
        {
            if (!_publishedByChat.TryGetValue(run.ChatId, out var runs))
                _publishedByChat[run.ChatId] = runs = new HashSet<Guid>();
            runs.Add(runId);
        }
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
