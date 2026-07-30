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
    private readonly ILogger<AssistantChatRetentionService> _logger;

    public AssistantChatRetentionService(
        IAssistantChatService chatService,
        ISettingsService settingsService,
        AssistantChatSyncService syncService,
        IAgentTimelineService timelineService,
        ILogger<AssistantChatRetentionService> logger)
    {
        _chatService = chatService;
        _settingsService = settingsService;
        _syncService = syncService;
        _timelineService = timelineService;
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
            if (!settings.ChatHistoryEnabled)
            {
                _logger.LogDebug("Skipping assistant chat retention cleanup; history disabled");
                return;
            }

            var days = Math.Clamp(settings.ChatHistoryRetentionDays, 1, 365);
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
            // Inside this try (a prune fault must not stop the timer) and behind the same ChatHistoryEnabled
            // check (a user who turned history off should not accumulate a growing audit table).
            var prunedEvents = await _timelineService.PruneOlderThanAsync(cutoff, ct);
            if (prunedEvents > 0)
            {
                _logger.LogInformation(
                    "Assistant chat retention pruned {Count} run timeline events older than {Days} days",
                    prunedEvents, days);
            }
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
}
