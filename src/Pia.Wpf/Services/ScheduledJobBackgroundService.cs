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
public class ScheduledJobBackgroundService : BackgroundService, IScheduledJobRunner
{
    private static readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _gracePeriod = TimeSpan.FromMinutes(15);

    private readonly IScheduledJobService _jobs;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IScheduledResearchProviderResolver _providers;
    private readonly IScheduledJobNotificationSurface _notifications;
    private readonly IHeadlessRunLauncher _launcher;
    private readonly ISettingsService _settingsService;
    private readonly IAgentRunService _runService;
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
        IHeadlessRunLauncher launcher,
        ISettingsService settingsService,
        IAgentRunService runService,
        ILogger<ScheduledJobBackgroundService> logger)
    {
        _jobs = jobs;
        _scopeFactory = scopeFactory;
        _providers = providers;
        _notifications = notifications;
        _launcher = launcher;
        _settingsService = settingsService;
        _runService = runService;
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

    /// <inheritdoc />
    public async Task<ScheduledJobRunNowResult> RunNowAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await _jobs.GetAsync(jobId);
        if (job is null)
        {
            _logger.LogWarning("Run-now requested for unknown scheduled job {Id}", jobId);
            return ScheduledJobRunNowResult.NotFound;
        }

        // The owner rule is asked of the service rather than re-derived here, so this cannot drift from the
        // SQL predicate GetDueJobsAsync uses. A manual run must not be able to do what this device's own
        // scheduler is forbidden to do.
        if (!await _jobs.IsOwnedByThisDeviceAsync(jobId))
        {
            _logger.LogInformation("Run-now refused for job {Id}: another device owns its schedule", jobId);
            return ScheduledJobRunNowResult.NotOwner;
        }

        // ExecuteJobAsync, not RunJobAsync: the latter's grace check asks the user whether a LATE job should
        // still run, which is meaningless for a run they just requested — and would pop a dialog for any job
        // whose NextFireAt is in the past, which is exactly the settled/overdue rows most likely to be run
        // manually. Both branches take _runLock themselves, so this still serialises against the tick.
        _logger.LogInformation("Run-now dispatching scheduled job {Id} ({Kind})", jobId, job.Kind);
        await ExecuteJobAsync(job, ct);
        return ScheduledJobRunNowResult.Dispatched;
    }

    private async Task RunJobAsync(ScheduledJob job, CancellationToken ct)
    {
        var lateBy = DateTime.Now - job.NextFireAt;

        if (lateBy <= _gracePeriod)
        {
            await ExecuteJobAsync(job, ct);
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

        await ExecuteJobAsync(job, ct);
    }

    /// <summary>
    /// Dispatch by kind (§17.1): an <see cref="ScheduledJobKind.AgentTask"/> job runs as an unattended
    /// headless Planned agent run via the launcher; a <see cref="ScheduledJobKind.Research"/> job keeps
    /// the existing background-turn runner. The missed-run gate above is kind-agnostic.
    /// </summary>
    private Task ExecuteJobAsync(ScheduledJob job, CancellationToken ct) =>
        job.Kind == ScheduledJobKind.AgentTask ? ExecuteAgentTaskAsync(job, ct) : ExecuteResearchAsync(job, ct);

    /// <summary>
    /// Runs a scheduled AgentTask as a headless Planned agent run. Mirrors <see cref="ExecuteResearchAsync"/>'s
    /// <c>_runLock</c> serialization + provider-resolve + success/failure bookkeeping, but swaps the runner
    /// for <see cref="IHeadlessRunLauncher"/> and derives success from the terminal run state. The job's
    /// GrantedTools flow to the run's write-consent set (§17.4); the launcher's slot cap still bounds
    /// overall concurrency.
    /// </summary>
    private async Task ExecuteAgentTaskAsync(ScheduledJob job, CancellationToken ct)
    {
        await _runLock.WaitAsync(ct);
        try
        {
            var provider = await _providers.ResolveAsync(job.ProviderId);
            if (provider is null)
            {
                // The one PRE-MODEL failure: nothing ran, so ScheduledJobService is allowed to re-arm a
                // one-off here instead of retiring it (B). Pass its constant, never a local literal — the
                // two legs and the classifier must not drift apart.
                const string reason = ScheduledJobService.NoProviderFailureReason;
                await _jobs.MarkRunFailedAsync(job.Id, reason);
                _notifications.NotifyFailure(job, reason);
                _logger.LogWarning("Scheduled agent job {Id} failed: no provider available", job.Id);
                return;
            }

            var settings = await _settingsService.GetSettingsAsync();
            var budget = RunProfile.FromBudget(
                settings.ScheduledMaxSteps, settings.ScheduledMaxReplans, settings.ScheduledWallClockMinutes);

            HeadlessRunHandle handle;
            try
            {
                handle = await _launcher.LaunchAsync(new HeadlessRunRequest(
                    Goal: job.Query,
                    Trigger: AgentRunTrigger.Schedule,
                    TriggerRef: job.Id,
                    OwnerDeviceId: job.OwnerDeviceId,
                    ProviderId: job.ProviderId,
                    // An agent job with no explicit grant gets the narrow detached-run default
                    // (HeadlessRunRequest.DefaultGrantedWrites = {write_file} — no delete, A1); an explicit
                    // grant list replaces it and may name delete_file if the user asked for that.
                    GrantedWrites: job.GrantedTools.Count > 0 ? job.GrantedTools : null,
                    Budget: budget), ct);

                // Serialized by _runLock; await the run's terminal settle to bookkeep like Research.
                await handle.Completion;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                await _jobs.MarkRunFailedAsync(job.Id, ex.Message);
                _notifications.NotifyFailure(job, ex.Message);
                _logger.LogWarning(ex, "Scheduled agent job {Id} run threw", job.Id);
                return;
            }

            var run = await _runService.GetAsync(handle.RunId, ct);
            if (run?.State == AgentRunState.Completed)
            {
                await _jobs.MarkRunCompleteAsync(job.Id, handle.ChatId);
                _notifications.NotifySuccess(job, handle.ChatId, job.Name);
                _logger.LogInformation("Scheduled agent job {Id} run completed; chat {ChatId}", job.Id, handle.ChatId);
            }
            else if (run?.State is AgentRunState.WaitingForInput or AgentRunState.Paused)
            {
                // Budget pause is a deliberate park, not a job failure — do NOT MarkRunFailed / toast
                // failure. The Flow WaitingForInput card (from PauseAsync's RunChanged) is the out-of-band
                // resume affordance (guardrail 6).
                //
                // But the schedule MUST still advance (F): only MarkRunComplete/MarkRunFailed recompute
                // NextFireAt, so a park used to leave the job due forever — the next 30 s tick launched a
                // DUPLICATE run of the same goal, and past the grace period the user got a missed-run
                // prompt for a job that had in fact already run. AdvanceMissedRunAsync is a pure
                // "NextFireAt = next occurrence" write: it never touches ConsecutiveFailures/Status, so it
                // carries no job-health signal and keeps park-is-not-a-failure intact.
                // Failure-isolated (guardrail 1): a bookkeeping fault here must not abort the tick's
                // remaining due jobs — worst case the job stays due and re-runs, exactly as before.
                try { await _jobs.AdvanceMissedRunAsync(job.Id); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to advance schedule for parked agent job {Id}", job.Id); }

                _logger.LogInformation("Scheduled agent job {Id} run parked at budget ({State}); awaiting resume, schedule advanced",
                    job.Id, run.State);
            }
            else
            {
                var reason = run?.State.ToString() ?? "Unknown";
                await _jobs.MarkRunFailedAsync(job.Id, reason);
                _notifications.NotifyFailure(job, reason);
                _logger.LogWarning("Scheduled agent job {Id} run did not complete: {State}", job.Id, reason);
            }
        }
        finally
        {
            _runLock.Release();
        }
    }

    private async Task ExecuteResearchAsync(ScheduledJob job, CancellationToken ct)
    {
        await _runLock.WaitAsync(ct);
        try
        {
            var provider = await _providers.ResolveAsync(job.ProviderId);
            if (provider is null)
            {
                // Pre-model, exactly as in ExecuteAgentTaskAsync — the shared constant is what earns a
                // one-off one more attempt, so both legs must hand over the same value.
                const string reason = ScheduledJobService.NoProviderFailureReason;
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
