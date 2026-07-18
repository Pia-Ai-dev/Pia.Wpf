using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>
/// Polls <see cref="IScheduledJobService.GetDueJobsAsync"/> every 30 seconds and runs each
/// due job as a headless background assistant turn (<see cref="IBackgroundAssistantTurnRunner"/>),
/// persisting the result as an assistant chat. Within the grace period a job runs silently;
/// once it exceeds the grace period the missed-run prompt in <see cref="RunJobAsync"/> asks the
/// user before running.
/// </summary>
public class ScheduledJobBackgroundService : BackgroundService
{
    private static readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _gracePeriod = TimeSpan.FromMinutes(15);

    private readonly IScheduledJobService _jobs;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IScheduledResearchProviderResolver _providers;
    private readonly IScheduledJobNotificationSurface _notifications;
    private readonly ILogger<ScheduledJobBackgroundService> _logger;

    /// <summary>
    /// Serializes job execution so two due jobs in the same tick (or a missed-run
    /// dialog overlapping a fresh due job) can never run concurrently.
    /// </summary>
    private readonly SemaphoreSlim _runLock = new(1, 1);

    /// <summary>
    /// Tracks jobs currently being prompted (or already prompted-and-unanswered) so we
    /// don't re-ask on subsequent polling ticks. Cleared on positive/negative answer.
    /// Not persisted: on app restart, missed runs are re-evaluated against the new now.
    /// </summary>
    private readonly HashSet<Guid> _pendingMissedPrompts = new();
    private readonly object _pendingLock = new();

    public ScheduledJobBackgroundService(
        IScheduledJobService jobs,
        IServiceScopeFactory scopeFactory,
        IScheduledResearchProviderResolver providers,
        IScheduledJobNotificationSurface notifications,
        ILogger<ScheduledJobBackgroundService> logger)
    {
        _jobs = jobs;
        _scopeFactory = scopeFactory;
        _providers = providers;
        _notifications = notifications;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ScheduledJobBackgroundService started");
        using var timer = new PeriodicTimer(_checkInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ExecuteOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in scheduled-job tick");
            }
        }
    }

    /// <summary>
    /// Executes a single polling pass. Public for direct test invocation.
    /// </summary>
    public async Task ExecuteOnceAsync(CancellationToken ct)
    {
        var due = await _jobs.GetDueJobsAsync();
        foreach (var job in due)
        {
            ct.ThrowIfCancellationRequested();
            await RunJobAsync(job, ct);
        }
    }

    private async Task RunJobAsync(ScheduledJob job, CancellationToken ct)
    {
        var lateBy = DateTime.Now - job.NextFireAt;

        if (lateBy <= _gracePeriod)
        {
            await ExecuteResearchAsync(job, ct);
            return;
        }

        // Grace exceeded — ask user (once per job, this session)
        lock (_pendingLock)
        {
            if (_pendingMissedPrompts.Contains(job.Id)) return;
            _pendingMissedPrompts.Add(job.Id);
        }

        bool? answer;
        try
        {
            answer = await _notifications.AskUserToRunMissedAsync(job, job.NextFireAt);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Missed-run prompt failed for job {Id}", job.Id);
            // Treat exception as "no answer" — keep in pending set, do not retry this session.
            return;
        }

        if (answer is null)
        {
            // User closed without answering — keep in pending set forever this session.
            return;
        }

        // User answered — clear the dedup so future occurrences can prompt.
        lock (_pendingLock) _pendingMissedPrompts.Remove(job.Id);

        if (answer == false)
        {
            // Skip this missed run: advance NextFireAt without touching failure counter.
            // A user choosing "Skip" is not a job-health signal.
            await _jobs.AdvanceMissedRunAsync(job.Id);
            _logger.LogInformation("User skipped missed run for job {Id}", job.Id);
            return;
        }

        await ExecuteResearchAsync(job, ct);
    }

    private async Task ExecuteResearchAsync(ScheduledJob job, CancellationToken ct)
    {
        await _runLock.WaitAsync(ct);
        try
        {
            var provider = await _providers.ResolveAsync(job.ProviderId);
            if (provider is null)
            {
                const string reason = "NoProvider";
                await _jobs.MarkRunFailedAsync(job.Id, reason);
                _notifications.NotifyFailure(job, reason);
                _logger.LogWarning("Scheduled job {Id} failed: no provider available", job.Id);
                return;
            }

            // Resolve the runner (and its transient AI-client decorator) from a fresh
            // per-run scope so tokenization state isn't captured across runs.
            using var scope = _scopeFactory.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<IBackgroundAssistantTurnRunner>();

            BackgroundTurnResult result;
            try
            {
                result = await runner.RunAsync(new BackgroundTurnRequest
                {
                    Prompt = job.Query,
                    Provider = provider,
                    GrantedWriteTools = job.GrantedTools,
                    Title = job.Name,
                    Trigger = AgentRunTrigger.Schedule,
                    TriggerRef = job.Id,
                    OwnerDeviceId = job.OwnerDeviceId,
                }, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                await _jobs.MarkRunFailedAsync(job.Id, ex.Message);
                _notifications.NotifyFailure(job, ex.Message);
                _logger.LogWarning(ex, "Scheduled job {Id} run threw", job.Id);
                return;
            }

            if (result.Succeeded)
            {
                // LastResultEntryId now references the produced assistant chat.
                await _jobs.MarkRunCompleteAsync(job.Id, result.ChatId);
                _notifications.NotifySuccess(job, result.ChatId, job.Name);
                _logger.LogInformation("Scheduled job {Id} run completed; chat {ChatId}", job.Id, result.ChatId);
            }
            else
            {
                var reason = result.Error ?? "Unknown error";
                await _jobs.MarkRunFailedAsync(job.Id, reason);
                _notifications.NotifyFailure(job, reason);
                _logger.LogWarning("Scheduled job {Id} run failed: {Reason}", job.Id, reason);
            }
        }
        finally
        {
            _runLock.Release();
        }
    }
}
