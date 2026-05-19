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
    private readonly ILogger<AssistantChatRetentionService> _logger;

    public AssistantChatRetentionService(
        IAssistantChatService chatService,
        ISettingsService settingsService,
        AssistantChatSyncService syncService,
        ILogger<AssistantChatRetentionService> logger)
    {
        _chatService = chatService;
        _settingsService = settingsService;
        _syncService = syncService;
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

    private async Task RunCleanupAsync(CancellationToken ct)
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
