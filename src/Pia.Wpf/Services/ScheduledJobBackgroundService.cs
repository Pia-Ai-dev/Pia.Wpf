using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>
/// Polls <see cref="IScheduledJobService.GetDueJobsAsync"/> every 30 seconds and DISPATCHES each due job —
/// an <see cref="ScheduledJobKind.AgentTask"/> job as a headless Planned agent run via
/// <see cref="IHeadlessRunLauncher"/>, a <see cref="ScheduledJobKind.Research"/> job as a headless background
/// assistant turn (<see cref="IBackgroundAssistantTurnRunner"/>) — persisting each result as an assistant chat.
/// Within the grace period a job runs silently; once it exceeds the grace period the missed-run prompt in
/// <see cref="RunJobAsync"/> asks the user before running.
/// <para>
/// <b>A tick DISPATCHES; it does not wait for the run.</b> This service used to hold a single run lock across
/// <c>await handle.Completion</c>, so one long agent run delayed every other scheduled job on the device for up
/// to its whole wall-clock budget (hermes review #2). Now each leg hands the run off and the tick returns; the
/// run's outcome is booked by a continuation (<see cref="BookkeepAgentRunAsync"/>,
/// <see cref="RunResearchTurnAsync"/>). Concurrency is bounded where each leg's runner actually is: the
/// launcher's own two slots for agent runs, <see cref="_researchSlots"/> for research turns. Both bounds QUEUE
/// when exhausted and are waited INSIDE the dispatched work, never in the tick — a tick that silently dropped a
/// due job would be worse than one that defers it.
/// </para>
/// <para>
/// Not awaiting the run means the schedule can no longer wait for the run either: the two layers that keep one
/// occurrence to one run are <see cref="MoveScheduleOnAsync"/> (the schedule advances at DISPATCH, so the next
/// tick's due query no longer returns the row) and <see cref="IsAlreadyExecutingAsync"/> (a
/// <c>TriggerRef</c>-scoped guard for what advancing cannot see). Each catches cases the other does not; see
/// their own docs.
/// </para>
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
    /// Serializes THIS service's own <see cref="IScheduledJobService"/> calls, and nothing else. It is what
    /// survives of the old run lock: that lock's only load-bearing side effect was that two job-row
    /// read-modify-writes could never overlap on <c>SqliteContext</c>'s single shared connection — which has an
    /// unsynchronized lazy open, and which this service now writes to from several bookkeeping continuations at
    /// once. It also makes the <c>GetAsync</c>-then-<c>UPDATE</c> pair inside
    /// <see cref="IScheduledJobService.MarkRunCompleteAsync"/> / <c>MarkOccurrenceDispatchedAsync</c> atomic
    /// against other scheduler-initiated calls.
    /// <para>
    /// <b>Never held across a run, and never across a dialog.</b> Every acquisition wraps exactly one
    /// <see cref="IScheduledJobService"/> call (see <see cref="LockedAsync{T}"/>), so the longest possible wait
    /// is a few SQL statements. Holding it around the due query plus the dispatch loop would rebuild the old
    /// head-of-line block, and holding it around <c>AskUserToRunMissedAsync</c> would put a modal dialog inside
    /// it. Deliberately does NOT claim to cover the other callers of that service — the settings UI, the
    /// assistant tool handler and sync all reach it independently, exactly as they did before.
    /// </para>
    /// </summary>
    private readonly SemaphoreSlim _bookkeepingLock = new(1, 1);

    /// <summary>
    /// The research leg's concurrency bound, waited INSIDE the dispatched work (mirroring
    /// <c>HeadlessRunLauncher</c>'s own slot pattern) so a queued turn never delays the tick.
    /// <para>
    /// One permit, because that is the concurrency this leg already had: <c>BackgroundAssistantTurnRunner</c>
    /// has no slot cap of its own and never touches <see cref="IHeadlessRunLauncher"/>, so hermes #2's "bounded
    /// only by the launcher slot semaphore" is simply false for it — dropping the old lock without this would
    /// turn N due research jobs into N concurrent provider turns. Widening it to 2 would invent a concurrency
    /// posture no measurement asked for; it is a separate number from the launcher's slots either way.
    /// </para>
    /// </summary>
    private readonly SemaphoreSlim _researchSlots = new(1, 1);

    /// <summary>
    /// Tracks jobs currently being prompted (or already prompted-and-unanswered) so we
    /// don't re-ask on subsequent polling ticks. Cleared on positive/negative answer.
    /// Not persisted: on app restart, missed runs are re-evaluated against the new now.
    /// </summary>
    private readonly HashSet<Guid> _pendingMissedPrompts = new();
    private readonly object _pendingLock = new();

    /// <summary>
    /// The dispatched work whose bookkeeping has not run yet. Completed entries are pruned on each add, so this
    /// is bounded by what is genuinely in flight. Read by <see cref="StopAsync"/> (to say in the log that some
    /// bookkeeping is being abandoned) and by <see cref="WaitForDispatchedRunsAsync"/>.
    /// </summary>
    private readonly List<Task> _dispatches = new();
    private readonly object _dispatchLock = new();

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
    /// Deliberately does NOT drain <see cref="_dispatches"/>. <c>App.OnExit</c> stops this service BEFORE
    /// <c>IHeadlessRunLauncher.StopAsync</c>, so at this point every in-flight run is still executing and a
    /// drain would burn its whole timeout for nothing. The degrade is bounded and self-healing: the schedule
    /// already moved at dispatch, so nothing re-fires, and the startup sweep settles the run row on next launch
    /// — only the job-health columns miss that one outcome.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        int pending;
        lock (_dispatchLock) pending = _dispatches.Count(t => !t.IsCompleted);
        if (pending > 0)
            _logger.LogInformation(
                "Scheduled-job service stopped with {Count} dispatched run(s) still in flight; their outcome bookkeeping is abandoned", pending);
    }

    /// <summary>
    /// Executes a single polling pass. Public for direct test invocation. Returns once every due job has been
    /// DISPATCHED — not once their runs have settled (see <see cref="WaitForDispatchedRunsAsync"/>).
    /// </summary>
    public async Task ExecuteOnceAsync(CancellationToken ct)
    {
        var due = await LockedAsync(() => _jobs.GetDueJobsAsync());
        foreach (var job in due)
        {
            ct.ThrowIfCancellationRequested();
            await RunJobAsync(job, ct);
        }
    }

    /// <inheritdoc />
    public async Task<ScheduledJobRunNowResult> RunNowAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await LockedAsync(() => _jobs.GetAsync(jobId));
        if (job is null)
        {
            _logger.LogWarning("Run-now requested for unknown scheduled job {Id}", jobId);
            return ScheduledJobRunNowResult.NotFound;
        }

        // The owner rule is asked of the service rather than re-derived here, so this cannot drift from the
        // SQL predicate GetDueJobsAsync uses. A manual run must not be able to do what this device's own
        // scheduler is forbidden to do.
        if (!await LockedAsync(() => _jobs.IsOwnedByThisDeviceAsync(jobId)))
        {
            _logger.LogInformation("Run-now refused for job {Id}: another device owns its schedule", jobId);
            return ScheduledJobRunNowResult.NotOwner;
        }

        // The guard's clearest case: a manual fire racing a tick's own run of the same job. Nothing else can
        // catch it — the schedule advancing at dispatch is about the DUE query, and this path never consults
        // it. And unlike the tick's refusal below, this one leaves the schedule alone: a manual fire that was
        // refused must not consume the occurrence the tick is still going to fire.
        if (await IsAlreadyExecutingAsync(job, ct))
        {
            _logger.LogInformation("Run-now refused for job {Id}: a run of it is already executing", jobId);
            return ScheduledJobRunNowResult.AlreadyRunning;
        }

        // ExecuteJobAsync, not RunJobAsync: the latter's grace check asks the user whether a LATE job should
        // still run, which is meaningless for a run they just requested — and would pop a dialog for any job
        // whose NextFireAt is in the past, which is exactly the settled/overdue rows most likely to be run
        // manually.
        _logger.LogInformation("Run-now dispatching scheduled job {Id} ({Kind})", jobId, job.Kind);
        await ExecuteJobAsync(job, ct);
        return ScheduledJobRunNowResult.Dispatched;
    }

    private async Task RunJobAsync(ScheduledJob job, CancellationToken ct)
    {
        // Layer (b), and BEFORE the grace check on purpose: a job whose previous run is still executing must
        // not be asked about as a "missed" run either — that prompt exists for a firing that never happened.
        if (await IsAlreadyExecutingAsync(job, ct))
        {
            // A refusal MUST move the schedule. Refuse-and-do-nothing leaves the row due, so it re-refuses
            // every 30 s and eventually drifts past _gracePeriod into a missed-run prompt for a job that is
            // running right now — the exact symptom this whole change exists to stop.
            _logger.LogInformation("Scheduled job {Id} not dispatched: a run of it is still executing", job.Id);
            await MoveScheduleOnAsync(job);
            return;
        }

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
            await LockedAsync(() => _jobs.AdvanceMissedRunAsync(job.Id));
            _logger.LogInformation("User skipped missed run for job {Id}", job.Id);
            return;
        }

        await ExecuteJobAsync(job, ct);
    }

    /// <summary>
    /// Dispatch by kind (§17.1): an <see cref="ScheduledJobKind.AgentTask"/> job runs as an unattended
    /// headless Planned agent run via the launcher; a <see cref="ScheduledJobKind.Research"/> job keeps
    /// the existing background-turn runner. The missed-run gate above is kind-agnostic, and so is the
    /// duplicate-dispatch guard — both legs stamp <c>TriggerRef = job.Id</c> on the run they create, so one
    /// query answers for both.
    /// </summary>
    private Task ExecuteJobAsync(ScheduledJob job, CancellationToken ct) =>
        job.Kind == ScheduledJobKind.AgentTask ? ExecuteAgentTaskAsync(job, ct) : ExecuteResearchAsync(job, ct);

    /// <summary>
    /// Dispatches a scheduled AgentTask as a headless Planned agent run and RETURNS: everything awaited here is
    /// bounded work (resolve the provider, read settings, create the run row + stub chat + workspace, move the
    /// schedule on), and the run itself is booked by <see cref="BookkeepAgentRunAsync"/> when it settles. The
    /// job's GrantedTools flow to the run's write-consent set (§17.4); the launcher's own slot cap is what
    /// bounds how many of these execute at once, and a third run simply queues on it.
    /// </summary>
    private async Task ExecuteAgentTaskAsync(ScheduledJob job, CancellationToken ct)
    {
        var provider = await _providers.ResolveAsync(job.ProviderId);
        if (provider is null)
        {
            // The one PRE-MODEL failure: nothing ran, so ScheduledJobService is allowed to re-arm a
            // one-off here instead of retiring it (B). Pass its constant, never a local literal — the
            // two legs and the classifier must not drift apart. No schedule-moved-on write on this path:
            // MarkRunFailedAsync owns NextFireAt here, and it is the writer that knows about the re-arm.
            const string reason = ScheduledJobService.NoProviderFailureReason;
            await LockedAsync(() => _jobs.MarkRunFailedAsync(job.Id, reason));
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
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A launch that threw created no run, so MarkRunFailedAsync owns the schedule here too (it
            // recomputes NextFireAt itself) and no dispatch write is wanted.
            await LockedAsync(() => _jobs.MarkRunFailedAsync(job.Id, ex.Message));
            _notifications.NotifyFailure(job, ex.Message);
            _logger.LogWarning(ex, "Scheduled agent job {Id} failed to launch", job.Id);
            return;
        }

        // Layer (a), AFTER the launch on this leg: LaunchAsync has already persisted a non-terminal AgentRuns
        // row carrying TriggerRef = job.Id, so if this write faults the guard above still catches the next
        // tick. Awaited inside the tick — the whole point is that ExecuteOnceAsync cannot return with the row
        // still sitting in the due window.
        await MoveScheduleOnAsync(job);

        TrackDispatch(BookkeepAgentRunAsync(job, handle));
    }

    /// <summary>
    /// The continuation the tick no longer waits for: settle-time bookkeeping for one dispatched agent run.
    /// <c>Completion</c> settles on a terminal state OR a budget park, and never faults.
    /// <para>
    /// Fully failure-isolated, including <see cref="OperationCanceledException"/>: nothing here has a caller to
    /// throw to. The schedule has already moved on, so the worst case of a fault here is one run's outcome
    /// missing from the job's health columns.
    /// </para>
    /// </summary>
    private async Task BookkeepAgentRunAsync(ScheduledJob job, HeadlessRunHandle handle)
    {
        try
        {
            await handle.Completion;

            var run = await _runService.GetAsync(handle.RunId, CancellationToken.None);
            if (run?.State == AgentRunState.Completed)
            {
                await LockedAsync(() => _jobs.MarkRunCompleteAsync(job.Id, handle.ChatId));
                _notifications.NotifySuccess(job, handle.ChatId, job.Name);
                _logger.LogInformation("Scheduled agent job {Id} run completed; chat {ChatId}", job.Id, handle.ChatId);
            }
            else if (run?.State is AgentRunState.WaitingForInput or AgentRunState.Paused)
            {
                // Budget pause is a deliberate park, not a job failure — do NOT MarkRunFailed / toast
                // failure. The Flow WaitingForInput card (from PauseAsync's RunChanged) is the out-of-band
                // resume affordance (guardrail 6). This arm is now log-only, and must STAY an arm: collapsing
                // it into the else below would book a park as a failure — a strike against the 5-strike valve,
                // and a one-off retired outright.
                //
                // The schedule needed no write here either, because MoveScheduleOnAsync already made it at
                // dispatch (F). It used to be made from this arm, which was the whole bug: only
                // MarkRunComplete/MarkRunFailed recomputed NextFireAt, so a park left the job due forever —
                // the next 30 s tick launched a DUPLICATE run of the same goal, and past the grace period the
                // user got a missed-run prompt for a job that had in fact already run.
                _logger.LogInformation("Scheduled agent job {Id} run parked at budget ({State}); awaiting resume",
                    job.Id, run.State);
            }
            else
            {
                var reason = run?.State.ToString() ?? "Unknown";
                await LockedAsync(() => _jobs.MarkRunFailedAsync(job.Id, reason));
                _notifications.NotifyFailure(job, reason);
                _logger.LogWarning("Scheduled agent job {Id} run did not complete: {State}", job.Id, reason);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bookkeeping failed for scheduled agent job {Id}", job.Id);
        }
    }

    /// <summary>
    /// Dispatches a scheduled Research job as a headless background assistant turn and RETURNS. Unlike the
    /// agent leg the runner is synchronous-to-completion, so the whole turn moves into
    /// <see cref="RunResearchTurnAsync"/>.
    /// </summary>
    private async Task ExecuteResearchAsync(ScheduledJob job, CancellationToken ct)
    {
        var provider = await _providers.ResolveAsync(job.ProviderId);
        if (provider is null)
        {
            // Pre-model, exactly as in ExecuteAgentTaskAsync — the shared constant is what earns a
            // one-off one more attempt, so both legs must hand over the same value.
            const string reason = ScheduledJobService.NoProviderFailureReason;
            await LockedAsync(() => _jobs.MarkRunFailedAsync(job.Id, reason));
            _notifications.NotifyFailure(job, reason);
            _logger.LogWarning("Scheduled job {Id} failed: no provider available", job.Id);
            return;
        }

        // Layer (a), and BEFORE the dispatch on this leg — the opposite order to the agent leg, because the
        // research runner creates its AgentRuns row INSIDE RunAsync, i.e. possibly long after this returns if
        // the turn queues on _researchSlots. The TriggerRef guard is therefore blind for that whole window and
        // this write is the only thing that can keep the next tick off the occurrence. So a write that FAULTS
        // skips the occurrence rather than dispatching it: an unbounded re-dispatch loop of provider turns,
        // one per tick, is the worse failure.
        if (!await MoveScheduleOnAsync(job)) return;

        TrackDispatch(RunResearchTurnAsync(job, provider, ct));
    }

    /// <summary>
    /// The continuation the tick no longer waits for: one research turn plus its bookkeeping, serialized
    /// against other research turns by <see cref="_researchSlots"/>. Fully failure-isolated for the same
    /// reason <see cref="BookkeepAgentRunAsync"/> is.
    /// </summary>
    private async Task RunResearchTurnAsync(ScheduledJob job, AiProvider provider, CancellationToken ct)
    {
        try
        {
            // Waited HERE, not in the tick: exhaustion defers this turn without holding up any other due job.
            await _researchSlots.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Scheduled job {Id} research turn abandoned while queued (shutting down)", job.Id);
            return;
        }

        try
        {
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
                _logger.LogInformation("Scheduled job {Id} research turn cancelled", job.Id);
                return;
            }
            catch (Exception ex)
            {
                await LockedAsync(() => _jobs.MarkRunFailedAsync(job.Id, ex.Message));
                _notifications.NotifyFailure(job, ex.Message);
                _logger.LogWarning(ex, "Scheduled job {Id} run threw", job.Id);
                return;
            }

            if (result.Succeeded)
            {
                // LastResultEntryId now references the produced assistant chat.
                await LockedAsync(() => _jobs.MarkRunCompleteAsync(job.Id, result.ChatId));
                _notifications.NotifySuccess(job, result.ChatId, job.Name);
                _logger.LogInformation("Scheduled job {Id} run completed; chat {ChatId}", job.Id, result.ChatId);
            }
            else
            {
                var reason = result.Error ?? "Unknown error";
                await LockedAsync(() => _jobs.MarkRunFailedAsync(job.Id, reason));
                _notifications.NotifyFailure(job, reason);
                _logger.LogWarning("Scheduled job {Id} run failed: {Reason}", job.Id, reason);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bookkeeping failed for scheduled job {Id}", job.Id);
        }
        finally
        {
            _researchSlots.Release();
        }
    }

    /// <summary>
    /// Layer (a), the primary defence against a second run of one occurrence: mark the occurrence spent so it
    /// leaves <c>GetDueJobsAsync</c>'s <c>NextFireAt &lt;= @Now AND Status = 'Active'</c> window immediately,
    /// rather than whenever the run happens to settle. Carries no job-health signal — the run's real outcome is
    /// still what drives ConsecutiveFailures/Status, from the bookkeeping continuation.
    /// <para>
    /// Failure-isolated (guardrail 1): a bookkeeping fault must not abort the tick's remaining due jobs. The
    /// caller decides what a fault means, because the two legs differ — see each call site.
    /// </para>
    /// </summary>
    /// <returns>False if the write faulted.</returns>
    private async Task<bool> MoveScheduleOnAsync(ScheduledJob job)
    {
        try
        {
            await LockedAsync(() => _jobs.MarkOccurrenceDispatchedAsync(job.Id));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to move the schedule on for dispatched job {Id}", job.Id);
            return false;
        }
    }

    /// <summary>
    /// Layer (b), Batch 08 §19 Q4: is a run of this job EXECUTING right now? Defence in depth for what layer
    /// (a) cannot see — a manual <c>RunNowAsync</c> that never consults the due query, and an occurrence coming
    /// due while a resumed run of the same job is executing again.
    /// <para>
    /// Fails OPEN, deliberately: a guard that cannot answer must not be the reason an occurrence is silently
    /// skipped, and layer (a) is the primary bound. That is also the pre-change behaviour, since no guard
    /// existed at all.
    /// </para>
    /// </summary>
    private async Task<bool> IsAlreadyExecutingAsync(ScheduledJob job, CancellationToken ct)
    {
        try
        {
            return await _runService.AnyExecutingRunForTriggerAsync(job.Id, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not check for a live run of scheduled job {Id}; dispatching anyway", job.Id);
            return false;
        }
    }

    private void TrackDispatch(Task dispatch)
    {
        lock (_dispatchLock)
        {
            _dispatches.RemoveAll(t => t.IsCompleted);
            _dispatches.Add(dispatch);
        }
    }

    /// <summary>
    /// Awaits every dispatch started so far, i.e. the point at which the runs' bookkeeping has been written.
    /// The join a TEST needs: <see cref="ExecuteOnceAsync"/> deliberately returns before any of that has
    /// happened, so asserting on <see cref="IScheduledJobService"/> effects straight after a tick would be a
    /// race. Never called in production — <see cref="StopAsync"/> explains why it must not be.
    /// <para>
    /// Loops rather than a single <c>WhenAll</c> so a dispatch added while we wait is also awaited. The tasks
    /// never fault (both continuations catch everything), so this never throws.
    /// </para>
    /// </summary>
    internal async Task WaitForDispatchedRunsAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (_dispatchLock) pending = _dispatches.Where(t => !t.IsCompleted).ToArray();
            if (pending.Length == 0) return;
            await Task.WhenAll(pending);
        }
    }

    /// <summary>
    /// Runs one <see cref="IScheduledJobService"/> call under <see cref="_bookkeepingLock"/>. Takes
    /// <c>CancellationToken.None</c> on purpose: the wait is bounded by a few SQL statements, and a settled
    /// run's outcome should still be written while the app is shutting down.
    /// </summary>
    private async Task<T> LockedAsync<T>(Func<Task<T>> op)
    {
        await _bookkeepingLock.WaitAsync(CancellationToken.None);
        try
        {
            return await op();
        }
        finally
        {
            _bookkeepingLock.Release();
        }
    }

    private async Task LockedAsync(Func<Task> op)
    {
        await _bookkeepingLock.WaitAsync(CancellationToken.None);
        try
        {
            await op();
        }
        finally
        {
            _bookkeepingLock.Release();
        }
    }
}
