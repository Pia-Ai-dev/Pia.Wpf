using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>
/// Polls <see cref="IScheduledJobService.GetDueJobsAsync"/> every 30 seconds and
/// executes each due research job. This task implements only the silent-execute
/// path; the grace-period / missed-run prompt arrives in Task 12 and will wedge
/// itself between <see cref="RunJobAsync"/> and <see cref="ExecuteResearchAsync"/>.
/// </summary>
public class ScheduledJobBackgroundService : BackgroundService
{
    private static readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _gracePeriod = TimeSpan.FromMinutes(15);

    private readonly IScheduledJobService _jobs;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IResearchHistoryService _history;
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
        IResearchHistoryService history,
        IScheduledResearchProviderResolver providers,
        IScheduledJobNotificationSurface notifications,
        ILogger<ScheduledJobBackgroundService> logger)
    {
        _jobs = jobs;
        _scopeFactory = scopeFactory;
        _history = history;
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
            // Skip this missed run: advance NextFireAt, log only.
            await _jobs.MarkRunFailedAsync(job.Id, "MissedRunSkippedByUser");
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
                var failedId = await PersistFailedEntryAsync(job, reason, provider: null);
                await _jobs.MarkRunFailedAsync(job.Id, reason);
                _notifications.NotifyFailure(job, failedId, reason);
                _logger.LogWarning("Scheduled job {Id} failed: no provider available", job.Id);
                return;
            }

            var session = new ResearchSession(job.Query);

            // Resolve the scoped IResearchService (and any of its scoped transitive deps
            // such as ITokenMapService) from a fresh per-run scope so they are not
            // captured for the singleton's lifetime.
            using var scope = _scopeFactory.CreateScope();
            var research = scope.ServiceProvider.GetRequiredService<IResearchService>();

            try
            {
                await research.ExecuteResearchAsync(session, provider, job.AnswerLength, ct);

                var entry = new ResearchHistoryEntry
                {
                    Id = session.Id,
                    Query = session.Query,
                    SynthesizedResult = session.SynthesizedResult,
                    StepsJson = SerializeSteps(session),
                    ProviderId = provider.Id,
                    ProviderName = provider.Name,
                    Status = session.Status.ToString(),
                    StepCount = session.Steps.Count,
                    CreatedAt = session.CreatedAt,
                    CompletedAt = session.CompletedAt ?? DateTime.Now,
                    ScheduledJobId = job.Id
                };
                await _history.AddEntryAsync(entry);
                await _jobs.MarkRunCompleteAsync(job.Id, entry.Id);
                _notifications.NotifySuccess(job, entry);

                _logger.LogInformation("Scheduled job {Id} run completed; entry {EntryId}", job.Id, entry.Id);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var failedId = await PersistFailedEntryAsync(job, ex.Message, provider);
                await _jobs.MarkRunFailedAsync(job.Id, ex.Message);
                _notifications.NotifyFailure(job, failedId, ex.Message);
                _logger.LogWarning(ex, "Scheduled job {Id} run failed", job.Id);
            }
        }
        finally
        {
            _runLock.Release();
        }
    }

    private async Task<Guid> PersistFailedEntryAsync(ScheduledJob job, string reason, AiProvider? provider)
    {
        var entry = new ResearchHistoryEntry
        {
            Query = job.Query,
            SynthesizedResult = $"Run failed: {reason}",
            StepsJson = "[]",
            ProviderId = provider?.Id ?? Guid.Empty,
            ProviderName = provider?.Name,
            Status = "Failed",
            StepCount = 0,
            CreatedAt = DateTime.Now,
            CompletedAt = DateTime.Now,
            ScheduledJobId = job.Id
        };
        await _history.AddEntryAsync(entry);
        return entry.Id;
    }

    private static string SerializeSteps(ResearchSession session)
    {
        if (session.Steps.Count == 0)
        {
            return "[]";
        }

        var dtos = session.Steps.Select(s => new ResearchStepDto
        {
            StepNumber = s.StepNumber,
            Title = s.Title,
            Content = s.Content,
            Status = s.Status.ToString()
        }).ToList();

        return JsonSerializer.Serialize(dtos);
    }
}
