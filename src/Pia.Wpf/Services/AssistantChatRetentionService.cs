using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pia.Services.Interfaces;

namespace Pia.Services;

public class AssistantChatRetentionService : BackgroundService
{
    private static readonly TimeSpan _initialDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan _interval = TimeSpan.FromHours(24);

    private readonly IAssistantChatService _chatService;
    private readonly ISettingsService _settingsService;
    private readonly AssistantChatSyncService _syncService;
    private readonly IAgentTimelineService _timelineService;
    private readonly IAgentToolExchangeStore _exchangeStore;
    private readonly ILogger<AssistantChatRetentionService> _logger;

    public AssistantChatRetentionService(
        IAssistantChatService chatService,
        ISettingsService settingsService,
        AssistantChatSyncService syncService,
        IAgentTimelineService timelineService,
        IAgentToolExchangeStore exchangeStore,
        ILogger<AssistantChatRetentionService> logger)
    {
        _chatService = chatService;
        _settingsService = settingsService;
        _syncService = syncService;
        _timelineService = timelineService;
        _exchangeStore = exchangeStore;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AssistantChatRetentionService started");

        try
        {
            await Task.Delay(_initialDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await RunCleanupAsync(stoppingToken);

        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunCleanupAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }

    /// <summary>
    /// One retention pass. <c>internal</c> rather than private so the three facts about it can drive it
    /// directly instead of racing a 5-second timer.
    /// </summary>
    internal async Task RunCleanupAsync(CancellationToken ct)
    {
        try
        {
            var settings = await _settingsService.GetSettingsAsync();
            var days = Math.Clamp(settings.ChatHistoryRetentionDays, 1, 365);

            if (!settings.ChatHistoryEnabled)
            {
                // History off does NOT mean nothing accumulates. Nothing else in the codebase gates chat or run
                // persistence on this flag — it is read here and in the settings VM and nowhere else — so runs
                // keep emitting up to 501 timeline rows each, and the chat-eviction cascade that would
                // otherwise reach them deliberately exempts Planned-run chats. Skipping the pass therefore
                // removed the ONLY bound on the audit table for exactly the most privacy-sensitive
                // configuration. So prune HARDER instead: the one-day floor the clamp above allows, rather than
                // DateTime.UtcNow, which would delete a live run's trace out from under the open panel.
                _logger.LogDebug("Assistant chat history disabled; pruning the run timeline to the one-day floor");
                await PruneTimelineAsync(DateTime.UtcNow - TimeSpan.FromDays(1), days: 1, ct);
                return;
            }

            var cutoff = DateTime.UtcNow - TimeSpan.FromDays(days);

            var evicted = await _chatService.EvictOlderThanAsync(cutoff, ct);
            if (evicted.Count > 0)
            {
                foreach (var id in evicted)
                    _syncService.EnqueueDelete(id);

                _logger.LogInformation(
                    "Assistant chat retention evicted {Count} chats older than {Days} days",
                    evicted.Count, days);
            }

            // Batch 03: the per-run audit trail ages out with the chat history it describes, on the SAME
            // cutoff, so it needs no setting of its own. It cannot ride the chat cascade alone: the eviction
            // above deliberately skips chats bearing a Planned run — precisely the runs a timeline is for.
            // Inside this try, because a prune fault must not stop the 24 h timer.
            await PruneTimelineAsync(cutoff, days, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Assistant chat retention cleanup failed");
        }
    }

    /// <summary>The one prune call site, so both retention modes report it the same way.</summary>
    private async Task PruneTimelineAsync(DateTime cutoff, int days, CancellationToken ct)
    {
        var prunedEvents = await _timelineService.PruneOlderThanAsync(cutoff, ct);
        if (prunedEvents > 0)
        {
            _logger.LogInformation(
                "Assistant chat retention pruned {Count} run timeline events older than {Days} days",
                prunedEvents, days);
        }

        // The payload-bearing exchange rows sweep on the SAME cutoff, from here rather than a second call
        // site, so the history-disabled one-day floor above reaches them too.
        var prunedExchanges = await _exchangeStore.PruneAsync(cutoff, ct);
        if (prunedExchanges > 0)
        {
            _logger.LogInformation(
                "Assistant chat retention pruned {Count} run tool-exchange rows older than {Days} days",
                prunedExchanges, days);
        }
    }
}
