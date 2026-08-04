using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>
/// Batch 08 D5's premise, MEASURED rather than read: (i) <c>HeadlessRunHandle.Completion</c> settles on a
/// PARK and not only on a terminal state; (ii) because it does, a parked run's bookkeeping happens promptly
/// instead of hanging forever; (iii) the park branch names <c>Paused</c> too and is not a job failure.
/// <para>
/// <b>Rewritten for hermes #2.</b> (ii) used to read "…so <c>ScheduledJobBackgroundService</c>'s
/// <c>_runLock</c> is released and the next due job runs in the SAME tick", and two facts here pinned that
/// head-of-line block as a positive property. The scheduler no longer awaits a run inside the tick at all, so
/// the block is gone and those two facts assert its ABSENCE instead — see
/// <see cref="UnsettledCompletion_NoLongerHoldsTheTick_SoEveryDueJobDispatches"/> and
/// <see cref="ManualFire_IsNotQueuedBehindAnUnsettledDispatch"/>. What (i) buys is now the bookkeeping
/// continuation: a park that never settled <c>Completion</c> would leave the job's outcome unwritten forever.
/// Every fact that reads a run row or a job's books therefore drains the dispatches first
/// (<see cref="SettleAsync"/>) — a tick returns before any of that has happened.
/// </para>
/// <para>
/// Uses the BUDGET pause as the stand-in for a user pause — nothing writes <c>AgentRunState.Paused</c> yet.
/// </para>
/// </summary>
public sealed class D5PausePremiseTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteContext _ctx;
    private readonly AgentRunService _runs;
    private readonly AssistantChatService _chats;
    private readonly string _runsBase;
    private readonly ExecutingRunStore _executing = new();

    public D5PausePremiseTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PiaD5_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _ctx = new SqliteContext(Path.Combine(_dir, "history.db"));
        _runs = new AgentRunService(_ctx, NullLogger<AgentRunService>.Instance);
        _chats = new AssistantChatService(_ctx, _runs);
        _runsBase = Path.Combine(_dir, "runs");
        Directory.CreateDirectory(_runsBase);
    }

    public void Dispose()
    {
        _runs.Dispose();
        _ctx.Dispose();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    /// <summary>
    /// The join a tick no longer contains: <c>ExecuteOnceAsync</c> returns once the due jobs have been
    /// DISPATCHED, and each run's outcome — the row's final state, the job's books — is written by a
    /// continuation afterwards. Bounded, so a fact that never settles fails in 30 s rather than hanging.
    /// </summary>
    private static Task SettleAsync(ScheduledJobBackgroundService bg, CancellationToken ct) =>
        bg.WaitForDispatchedRunsAsync().WaitAsync(TimeSpan.FromSeconds(30), ct);

    // ---------------------------------------------------------------- (i)

    /// <summary>
    /// D5(i). A run that PARKS (budget wall-clock) settles its <c>HeadlessRunHandle.Completion</c> — the await
    /// returns instead of hanging — while the run row is still NON-terminal and its step is still Pending, i.e.
    /// resumable. Every existing scheduler park test hands the service a <c>Task.CompletedTask</c> completion,
    /// so this is the first measurement of the claim.
    /// </summary>
    [Fact]
    public async Task Park_SettlesTheHandleCompletion_AndLeavesTheRunResumable()
    {
        var ct = TestContext.Current.CancellationToken;
        var (launcher, planner, _) = BuildLauncher();
        planner.StepsFor = _ => 1;

        try
        {
            var handle = await launcher.LaunchAsync(new HeadlessRunRequest(
                "park me", AgentRunTrigger.Schedule,
                // Verbatim (the launcher does NOT clamp req.Budget): wall-clock is already exceeded on the
                // first drain iteration, so the run parks before dispatching a step.
                Budget: new RunProfile(MaxSteps: 24, MaxReplans: 2, WallClock: TimeSpan.Zero)), ct);

            // THE FACT. A TimeoutException here is the D5 premise being false.
            await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10), ct);
            Assert.True(handle.Completion.IsCompletedSuccessfully);

            var run = await _runs.GetAsync(handle.RunId, ct);
            Assert.NotNull(run);
            Assert.Equal(AgentRunState.WaitingForInput, run!.State);   // parked, NOT terminal
            Assert.Contains("wall-clock", run.ExtraJson ?? string.Empty);
            Assert.Equal(1, planner.PlanCalls);                        // non-vacuity: the run really planned
            Assert.Single(run.Plan);
            Assert.Equal(AgentStepStatus.Pending, run.Plan[0].Status);  // still resumable
        }
        finally
        {
            await launcher.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// D5(i), the STRUCTURAL half — and the reason the premise is safe for D1's cancel-based pause too.
    /// <c>Completion</c> is the launcher's dispatch <c>Task.Run</c> lambda: it catches
    /// <c>OperationCanceledException</c> and <c>Exception</c> and always runs its <c>finally</c>, so it settles on
    /// ANY exit of <c>orchestrator.RunAsync</c> — return, cancel or throw. Pinned with a planner that throws, the
    /// one exit no other test in this file takes.
    /// </summary>
    [Fact]
    public async Task DispatchThatThrows_AlsoSettlesTheHandleCompletion_NeverFaultsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var (launcher, planner, _) = BuildLauncher();
        planner.Throw = () => new InvalidOperationException("planner boom");

        try
        {
            var handle = await launcher.LaunchAsync(
                new HeadlessRunRequest("throw for me", AgentRunTrigger.Schedule), ct);

            await handle.Completion.WaitAsync(TimeSpan.FromSeconds(10), ct);
            Assert.True(handle.Completion.IsCompletedSuccessfully); // the awaiting scheduler never sees a throw

            var run = await _runs.GetAsync(handle.RunId, ct);
            Assert.Equal(AgentRunState.Failed, run!.State);
        }
        finally
        {
            await launcher.StopAsync(CancellationToken.None);
        }
    }

    // --------------------------------------------------------------- (ii)

    /// <summary>
    /// D5(ii), end to end with the REAL launcher and the REAL scheduler: the first due job's run parks at its
    /// step cap, and the SECOND due job of the same tick launches and completes. A parked run costs the fleet
    /// nothing.
    /// <para>
    /// Mechanism, restated for hermes #2: the tick DISPATCHES both jobs (nothing awaits a run any more), and the
    /// drain is what makes the two runs' final states observable. This used to be the file's headline inference —
    /// "job 2 launched ⇒ job 1's dispatch returned and released the lock" — and that inference is now vacuous by
    /// construction, which is precisely why
    /// <see cref="UnsettledCompletion_NoLongerHoldsTheTick_SoEveryDueJobDispatches"/> exists in its inverted
    /// form. What survives here is the end-to-end fact with the real orchestrator: a park settles, the other job
    /// completes, and the parked job is booked as neither a success nor a failure.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ParkedScheduledRun_DoesNotBlockTheNextDueJobOfTheSameTick()
    {
        var ct = TestContext.Current.CancellationToken;
        // MaxSteps = 1 (the clamp floor) is how a run parks through the SCHEDULER's own budget, which is
        // RunProfile.FromBudget-clamped and so cannot take a zero wall clock.
        var (real, planner, settings) = BuildLauncher(new AppSettings { ScheduledMaxSteps = 1 });
        settings.GetSettingsAsync().Returns(new AppSettings { ScheduledMaxSteps = 1 });
        planner.StepsFor = goal => goal == "job-1" ? 2 : 1; // 2 steps → parks at the cap; 1 step → completes

        var recorder = new RecordingLauncher(real);
        var jobs = new FakeJobService();
        var job1 = NewAgentJob("job-1");
        var job2 = NewAgentJob("job-2");
        jobs.SeedDue(job1);
        jobs.SeedDue(job2);

        var bg = new ScheduledJobBackgroundService(
            jobs, new FakeScopeFactory(), new FakeProviderResolver(NewProvider()), new FakeNotificationSurface(),
            recorder, settings, _runs, NullLogger<ScheduledJobBackgroundService>.Instance);

        try
        {
            await bg.ExecuteOnceAsync(ct);

            // Both jobs dispatched in the one tick.
            Assert.Equal(2, recorder.Launched.Count);
            Assert.Equal("job-1", recorder.Launched[0].Goal);
            Assert.Equal("job-2", recorder.Launched[1].Goal);

            await SettleAsync(bg, ct);

            var parked = await _runs.GetAsync(recorder.Launched[0].Handle.RunId, ct);
            var completed = await _runs.GetAsync(recorder.Launched[1].Handle.RunId, ct);
            Assert.Equal(AgentRunState.WaitingForInput, parked!.State); // job 1 is STILL parked …
            Assert.Contains("step-cap", parked.ExtraJson ?? string.Empty);
            Assert.Equal(AgentRunState.Completed, completed!.State);    // … while job 2 ran to completion

            // (iii) observed through the real service: the park did not fail the job, and both occurrences were
            // spent at dispatch (jobs.Dispatched) rather than from the park arm (jobs.Advanced, now unused here).
            Assert.Contains(job1.Id, jobs.Dispatched);
            Assert.Empty(jobs.Advanced);
            Assert.Empty(jobs.Failed);
            // T0-1: nor through the new health-only door. A park is not a firing outcome, so BookkeepAgentRunAsync's
            // park arm must stay log-only — booking one here would burn a strike on work the user can still
            // continue. Scoped claim: nothing in this fixture resumes anything, so this line says nothing about
            // BookResumedRunAsync's own parked/executing declines — those are pinned by
            // ScheduledJobBackgroundServiceTests.AResumedRunThatIsNotASettledFiring_BooksNothing.
            Assert.DoesNotContain(job1.Id, jobs.Bookings.Select(b => b.JobId));
            Assert.Contains(job2.Id, jobs.Completed.Select(c => c.JobId));
        }
        finally
        {
            await real.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// D5(ii)'s control, INVERTED (hermes #2). It used to assert the head-of-line block as a positive property —
    /// job 2 stayed undispatched, and the tick stayed inside job 1, until job 1's <c>Completion</c> settled — and
    /// it was the only evidence in the suite that the scheduler serialised at all. That block is the defect: one
    /// long agent run delayed every other scheduled job on the device for up to its whole wall clock. So the same
    /// fixture now pins its absence, which keeps the measurement rather than deleting it.
    /// <para>
    /// The gate is never opened before the assertion, so job 1's run is provably unsettled when job 2 dispatches
    /// and the tick returns. Restoring <c>await handle.Completion</c> inside the leg reds this as a
    /// TimeoutException on the tick — which is why the tick is awaited with a bound.
    /// </para>
    /// </summary>
    [Fact]
    public async Task UnsettledCompletion_NoLongerHoldsTheTick_SoEveryDueJobDispatches()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launches = 0;
        var launcher = Substitute.For<IHeadlessRunLauncher>();
        launcher.LaunchAsync(Arg.Any<HeadlessRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => new HeadlessRunHandle(Guid.NewGuid(), Guid.NewGuid(),
                Interlocked.Increment(ref launches) == 1 ? gate.Task : Task.CompletedTask));

        var runService = Substitute.For<IAgentRunService>();
        runService.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => new AgentRun
            {
                Id = ci.ArgAt<Guid>(0), RunShape = RunShape.Planned, State = AgentRunState.WaitingForInput,
            });

        var jobs = new FakeJobService();
        jobs.SeedDue(NewAgentJob("job-1"));
        jobs.SeedDue(NewAgentJob("job-2"));

        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());

        var bg = new ScheduledJobBackgroundService(
            jobs, new FakeScopeFactory(), new FakeProviderResolver(NewProvider()), new FakeNotificationSurface(),
            launcher, settings, runService, NullLogger<ScheduledJobBackgroundService>.Instance);

        await bg.ExecuteOnceAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);

        Assert.Equal(2, Volatile.Read(ref launches)); // job 2 dispatched in the same tick …
        Assert.False(gate.Task.IsCompleted);          // … while job 1's run was provably still unsettled

        // And job 1's bookkeeping is deferred, not dropped: it lands when the run finally parks.
        gate.SetResult();
        await SettleAsync(bg, ct);
        Assert.Equal(2, jobs.Dispatched.Count);
        Assert.Empty(jobs.Failed);                    // both runs read WaitingForInput → parks, not failures
    }

    /// <summary>
    /// The lock itself, not the loop — INVERTED with its sibling above (hermes #2). <c>RunNowAsync</c> used to
    /// queue on <c>_runLock</c> independently of the tick's sequential foreach, so a manual fire was the direct
    /// probe of the lock: it blocked while an in-flight dispatch held it. There is no such lock now, and this is
    /// the direct probe of that: the manual fire of a DIFFERENT job returns <c>Dispatched</c> while another job's
    /// run is still unsettled, so a settings-page button cannot be held hostage by a 45-minute run.
    /// <para>
    /// A different job on purpose — the duplicate-run guard is <c>TriggerRef</c>-scoped, and a manual fire of the
    /// SAME job while its run executes is refused; that fact is
    /// <c>ScheduledJobBackgroundServiceTests.RunNowAsync_RefusedWhenARunOfTheJobIsAlreadyExecuting_…</c>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ManualFire_IsNotQueuedBehindAnUnsettledDispatch()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launches = 0;
        var launcher = Substitute.For<IHeadlessRunLauncher>();
        launcher.LaunchAsync(Arg.Any<HeadlessRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => new HeadlessRunHandle(Guid.NewGuid(), Guid.NewGuid(),
                Interlocked.Increment(ref launches) == 1 ? gate.Task : Task.CompletedTask));

        var runService = Substitute.For<IAgentRunService>();
        runService.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => new AgentRun
            {
                Id = ci.ArgAt<Guid>(0), RunShape = RunShape.Planned, State = AgentRunState.WaitingForInput,
            });

        var jobs = new FakeJobService();
        var due = NewAgentJob("job-1");
        var manual = NewAgentJob("job-2");
        manual.NextFireAt = DateTime.Now.AddDays(3); // NOT due — only RunNowAsync can fire it
        jobs.SeedDue(due);
        jobs.SeedDue(manual);

        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());

        var bg = new ScheduledJobBackgroundService(
            jobs, new FakeScopeFactory(), new FakeProviderResolver(NewProvider()), new FakeNotificationSurface(),
            launcher, settings, runService, NullLogger<ScheduledJobBackgroundService>.Instance);

        await bg.ExecuteOnceAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Equal(1, Volatile.Read(ref launches));  // the due job dispatched; its run is unsettled
        Assert.False(gate.Task.IsCompleted);

        // THE CLAIM: the manual fire goes through NOW, with the first run still in flight.
        var manualFire = bg.RunNowAsync(manual.Id, ct);
        Assert.Equal(ScheduledJobRunNowResult.Dispatched, await manualFire.WaitAsync(TimeSpan.FromSeconds(10), ct));
        Assert.Equal(2, Volatile.Read(ref launches));
        Assert.False(gate.Task.IsCompleted);           // non-vacuity: nothing settled to let it through

        gate.SetResult();
        await SettleAsync(bg, ct);
    }

    /// <summary>
    /// The ORDERING requirement D5 does not state, and the one way (ii) can still bite: the scheduler reads the
    /// run row AFTER <c>Completion</c> settles (now in <c>BookkeepAgentRunAsync</c>, which is the same read at a
    /// later moment), and its <c>WaitingForInput or Paused</c> branch is an <c>else if</c>. A pause that unwinds
    /// the dispatch BEFORE the
    /// row says <c>Paused</c> — e.g. D1's cancel reaching the orchestrator's
    /// <c>catch (OperationCanceledException)</c> at <c>AgentRunOrchestrator.cs:378-386</c>, which settles
    /// <c>Cancelled</c> — lands on <c>:271</c> instead: <c>MarkRunFailedAsync</c> + a failure toast. On a
    /// recurring job that is a strike against the 5-strike valve; on a <c>RecurrenceType.Once</c> job it retires
    /// the job on the FIRST strike (<c>ScheduledJobService.cs:340-354</c>).
    /// </summary>
    [Fact]
    public async Task RunNotParkedWhenItsCompletionSettles_IsBookkeptAsAJobFailure()
    {
        var ct = TestContext.Current.CancellationToken;
        var launcher = Substitute.For<IHeadlessRunLauncher>();
        launcher.LaunchAsync(Arg.Any<HeadlessRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(new HeadlessRunHandle(Guid.NewGuid(), Guid.NewGuid(), Task.CompletedTask));

        var runService = Substitute.For<IAgentRunService>();
        runService.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => new AgentRun
            {
                // What a pause implemented as a plain cancel leaves behind.
                Id = ci.ArgAt<Guid>(0), RunShape = RunShape.Planned, State = AgentRunState.Cancelled,
            });

        var jobs = new FakeJobService();
        var job = NewAgentJob("job-1");
        jobs.SeedDue(job);

        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());
        var notifications = new FakeNotificationSurface();

        var bg = new ScheduledJobBackgroundService(
            jobs, new FakeScopeFactory(), new FakeProviderResolver(NewProvider()), notifications,
            launcher, settings, runService, NullLogger<ScheduledJobBackgroundService>.Instance);

        await bg.ExecuteOnceAsync(ct);
        await SettleAsync(bg, ct);

        Assert.Single(jobs.Failed);
        Assert.Equal("Cancelled", jobs.Failed[0].Reason);
        Assert.Equal(1, notifications.FailureCount);
        Assert.Empty(jobs.Advanced);
    }

    // ------------------------------------------ D1 item 6, as a scheduled job

    /// <summary>
    /// Batch 08 G5. <b>§1 D1 item 6 as a test, and the fact that catches a builder who reorders the pause
    /// branch.</b> A USER pause of a scheduled agent run must leave the row reading <c>Paused</c> BEFORE the
    /// dispatch task returns, because <c>ScheduledJobBackgroundService</c> reads the row immediately after
    /// <c>await handle.Completion</c> (in its bookkeeping continuation since hermes #2 — a later moment, the same
    /// read, and the same ordering requirement) and its park branch is an <c>else if</c>: a row that is not yet
    /// <c>Paused</c>/<c>WaitingForInput</c> at that instant lands on <c>MarkRunFailedAsync</c> + a failure toast
    /// + a strike against the 5-strike valve — and a <c>RecurrenceType.Once</c> job is retired on the first
    /// strike.
    /// <para>
    /// <see cref="RunNotParkedWhenItsCompletionSettles_IsBookkeptAsAJobFailure"/> is the other half of this
    /// pair: it drives the same scheduler with a row that is NOT parked when <c>Completion</c> settles and shows
    /// all three failure symptoms. Here the real launcher, the real orchestrator, the real steering service and
    /// the real scheduler produce none of them.
    /// </para>
    /// <para>
    /// MEASURED neutralization, not a claim: deferring the pause branch's CAS so the dispatch returns first
    /// (<c>_ = Task.Run(async () =&gt; { await Task.Delay(500); await SafePauseUser(run.Id); })</c>) reds this on
    /// <c>jobs.Failed == [(job, "Running")]</c> — a real strike with a real failure toast, for a run that is
    /// paused half a second later. The state legs below would still pass on a re-read taken late enough, which
    /// is exactly why the bookkeeping is asserted first.
    /// </para>
    /// </summary>
    [Fact]
    public async Task PausedScheduledRun_AdvancesTheScheduleAndFailsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new RunSteeringStore();
        var steering = new AgentRunSteeringService(_runs, store, NullLogger<AgentRunSteeringService>.Instance);
        var stepEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var (real, planner, settings) = BuildLauncher(
            steering: store, stream: (_, token) => HoldInsideTheStep(stepEntered, token));
        planner.StepsFor = _ => 2; // a REAL plan, so the run reaches the drain loop and a step can be in flight

        var recorder = new RecordingLauncher(real);
        var jobs = new FakeJobService();
        var job = NewAgentJob("job-1");
        jobs.SeedDue(job);
        var notifications = new FakeNotificationSurface();

        var bg = new ScheduledJobBackgroundService(
            jobs, new FakeScopeFactory(), new FakeProviderResolver(NewProvider()), notifications,
            recorder, settings, _runs, NullLogger<ScheduledJobBackgroundService>.Instance);

        try
        {
            await bg.ExecuteOnceAsync(ct);
            await stepEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

            // The recorder appends on the LAUNCHING thread while the dispatch runs on another, so the step can
            // be in flight a hair before the handle is recorded. Wait for it rather than assuming an order.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (recorder.Launched.Count == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(10, ct);
            var runId = Assert.Single(recorder.Launched).Handle.RunId;

            // Running with a step in flight — the only state a user pause is legal from, asserted so this fact
            // cannot pass through the Planning hole where the CAS loses and writes nothing. Reachable here only
            // because the tick returned while the run was still executing.
            Assert.Equal(AgentRunState.Running, (await _runs.GetAsync(runId, ct))!.State);
            Assert.True(await steering.PauseAsync(runId, ct));

            await SettleAsync(bg, ct);

            // THE CLAIM, asserted first because it is what a reorder breaks: the job was not booked as a
            // failure, no failure toast was raised, and the schedule still moved on. The Dispatched leg is the
            // positive one, so the two Empty legs cannot pass vacuously on a tick that did nothing.
            Assert.Empty(jobs.Failed);
            Assert.Equal(0, notifications.FailureCount);
            Assert.Contains(job.Id, jobs.Dispatched);
            Assert.Empty(jobs.Advanced);  // and the park arm no longer writes the schedule itself
            Assert.Empty(jobs.Completed); // nor was it booked as a success

            // And the run really is user-paused and resumable, not merely "not failed".
            var paused = await _runs.GetAsync(runId, ct);
            Assert.Equal(AgentRunState.Paused, paused!.State);
            Assert.Null(paused.CompletedAt);
            Assert.Equal(AgentRunService.UserPausedReason, RunPauseEnvelope.ReadReason(paused));
            Assert.Equal(AgentStepStatus.Pending, paused.Plan[0].Status);
        }
        finally
        {
            await real.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// <b>Batch 08 C2.</b> D5(ii) is pinned for a BUDGET park
    /// (<see cref="ParkedScheduledRun_DoesNotBlockTheNextDueJobOfTheSameTick"/>) and the USER pause is pinned
    /// for a single job (<see cref="PausedScheduledRun_AdvancesTheScheduleAndFailsNothing"/>) — but nothing
    /// combined them, so the leg D5's whole premise rests on was never observed for the pause Batch 08 added.
    /// <para>
    /// It is an UNCOVERED LEG rather than a suspected defect, and the review was explicit about that:
    /// <c>_runLock</c> is released in a <c>finally</c> that does not discriminate pause kind, so the reasoning
    /// says it holds. The reason to pin it anyway is that "the release is in a finally" is an argument about
    /// today's code, while D5's premise — a user can pause a scheduled run without stalling the fleet — is a
    /// claim about behaviour: the head-of-line block is bounded by the PARK, not by the paused run's eventual
    /// resume, and a user pause is the one park that can arrive at an arbitrary moment.
    /// </para>
    /// <para>
    /// Only JOB 1's step is held, and it is held by GOAL rather than by invocation order (hermes #2): the tick
    /// dispatches both jobs without waiting, so the two runs execute concurrently and "invocation 1" is no longer
    /// job 1. Everything else — job 2's step, any verify turn — drives straight through. The inference this fact
    /// used to rest on (job 2 launched ⇒ job 1's lock was released) is gone with the lock; what it now measures is
    /// that a run paused mid-step neither blocks nor breaks the OTHER job dispatched alongside it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AUserPausedScheduledRun_DoesNotBlockTheNextDueJobOfTheSameTick()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new RunSteeringStore();
        var steering = new AgentRunSteeringService(_runs, store, NullLogger<AgentRunSteeringService>.Instance);
        var stepEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var (real, planner, settings) = BuildLauncher(
            steering: store,
            stream: (messages, token) => GoalOf(messages) == "job-1"
                ? HoldInsideTheStep(stepEntered, token)   // job 1's only step, held until the pause fires
                : Drive());                               // job 2's step, and every turn after it
        planner.StepsFor = _ => 1;

        var recorder = new RecordingLauncher(real);
        var jobs = new FakeJobService();
        var job1 = NewAgentJob("job-1");
        var job2 = NewAgentJob("job-2");
        jobs.SeedDue(job1);
        jobs.SeedDue(job2);
        var notifications = new FakeNotificationSurface();

        var bg = new ScheduledJobBackgroundService(
            jobs, new FakeScopeFactory(), new FakeProviderResolver(NewProvider()), notifications,
            recorder, settings, _runs, NullLogger<ScheduledJobBackgroundService>.Instance);

        try
        {
            await bg.ExecuteOnceAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
            await stepEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

            // Both jobs of the one tick dispatched, and job 1's step is in flight while we pause it.
            Assert.Equal(2, recorder.Launched.Count);
            Assert.Equal("job-1", recorder.Launched[0].Goal);
            Assert.Equal("job-2", recorder.Launched[1].Goal);
            var pausedRunId = recorder.Launched[0].Handle.RunId;

            Assert.Equal(AgentRunState.Running, (await _runs.GetAsync(pausedRunId, ct))!.State);
            Assert.True(await steering.PauseAsync(pausedRunId, ct));

            await SettleAsync(bg, ct);

            // THE CLAIM: the run alongside the paused one finished, and the pause cost it nothing.

            var paused = await _runs.GetAsync(pausedRunId, ct);
            Assert.Equal(AgentRunState.Paused, paused!.State);   // job 1 is STILL paused …
            Assert.Null(paused.CompletedAt);
            Assert.Equal(AgentRunService.UserPausedReason, RunPauseEnvelope.ReadReason(paused));
            var completed = await _runs.GetAsync(recorder.Launched[1].Handle.RunId, ct);
            Assert.Equal(AgentRunState.Completed, completed!.State); // … while job 2 ran to completion

            // Bookkeeping, the half a reorder breaks: a user pause is not a strike for job 1, and job 2 is a
            // clean success. Both schedules moved on, at dispatch.
            Assert.Empty(jobs.Failed);
            Assert.Equal(0, notifications.FailureCount);
            Assert.Contains(job1.Id, jobs.Dispatched);
            Assert.Contains(job2.Id, jobs.Dispatched);
            Assert.Empty(jobs.Advanced);
            Assert.Contains(job2.Id, jobs.Completed.Select(c => c.JobId));
        }
        finally
        {
            await real.StopAsync(CancellationToken.None);
        }
    }

    // ------------------------------------------------- D5's new consequence

    /// <summary>
    /// D5's "one consequence that is new", and the half of Batch 08 §19 Q4 that stays OPEN <b>by decision</b>:
    /// the schedule moves off the occurrence at dispatch, so when the next occurrence comes due the job launches a
    /// FRESH run while the previous one is still parked, and two independently resumable runs of one job coexist.
    /// <para>
    /// Q4's guard now exists (<c>AgentRuns.TriggerRef</c> is finally read —
    /// <c>IAgentRunService.AnyExecutingRunForTriggerAsync</c>, seeking <c>IX_AgentRuns_TriggerRef</c> at
    /// <c>SqliteContext.cs:346</c>) and it deliberately does NOT fire here, because a PARK is not executing. The
    /// alternative was measured against its cost rather than its neatness: nothing but a human clicking Continue
    /// leaves <c>WaitingForInput</c>, so a guard that counted a park as live would let one un-resumed budget park
    /// — a routine outcome — silence a daily job forever, with a log warning as the only trace. Two parked
    /// generations are recoverable; a job that stops running is not. What the change DOES bound is the number of
    /// runs per occurrence: one, which is what this fact's second tick shows (a fresh occurrence, not a repeat of
    /// the first).
    /// </para>
    /// </summary>
    [Fact]
    public async Task ParkedScheduledRun_AndTheNextOccurrenceOfTheSameJob_StillCoexist_BecauseAParkIsNotExecuting()
    {
        var ct = TestContext.Current.CancellationToken;
        var (real, planner, settings) = BuildLauncher(new AppSettings { ScheduledMaxSteps = 1 });
        settings.GetSettingsAsync().Returns(new AppSettings { ScheduledMaxSteps = 1 });
        planner.StepsFor = _ => 2; // always parks at the cap

        var recorder = new RecordingLauncher(real);
        var jobs = new FakeJobService();
        var job = NewAgentJob("recurring");
        jobs.SeedDue(job);

        var bg = new ScheduledJobBackgroundService(
            jobs, new FakeScopeFactory(), new FakeProviderResolver(NewProvider()), new FakeNotificationSurface(),
            recorder, settings, _runs, NullLogger<ScheduledJobBackgroundService>.Instance);

        try
        {
            await bg.ExecuteOnceAsync(ct);               // generation 1 → schedule moved on at dispatch
            Assert.Single(recorder.Launched);
            Assert.Contains(job.Id, jobs.Dispatched);
            // Drained BEFORE the second tick on purpose: gen 1 must have reached its PARK, or it would still be
            // executing and the guard would (correctly) refuse gen 2 — which would measure the guard, not this.
            await SettleAsync(bg, ct);
            Assert.Equal(AgentRunState.WaitingForInput,
                (await _runs.GetAsync(recorder.Launched[0].Handle.RunId, ct))!.State);

            job.NextFireAt = DateTime.Now.AddSeconds(-1); // the next occurrence comes due
            await bg.ExecuteOnceAsync(ct);               // generation 2 of the SAME job
            await SettleAsync(bg, ct);

            Assert.Equal(2, recorder.Launched.Count);
            var gen1 = await _runs.GetAsync(recorder.Launched[0].Handle.RunId, ct);
            var gen2 = await _runs.GetAsync(recorder.Launched[1].Handle.RunId, ct);

            Assert.NotEqual(gen1!.Id, gen2!.Id);
            Assert.NotEqual(gen1.ChatId, gen2.ChatId);
            Assert.Equal(job.Id, gen1.TriggerRef);
            Assert.Equal(job.Id, gen2.TriggerRef);
            // BOTH are parked and BOTH are resumable at the same time.
            Assert.Equal(AgentRunState.WaitingForInput, gen1.State);
            Assert.Equal(AgentRunState.WaitingForInput, gen2.State);
            Assert.Empty(jobs.Failed);
        }
        finally
        {
            await real.StopAsync(CancellationToken.None);
        }
    }

    // ------------------------------------------------------------- fixtures

    private static AiProvider NewProvider() => new()
    {
        Id = Guid.NewGuid(), Name = "P", Endpoint = "https://example", TimeoutSeconds = 60,
    };

    private static ScheduledJob NewAgentJob(string query) => new()
    {
        Name = "T",
        Query = query,
        Kind = ScheduledJobKind.AgentTask,
        Recurrence = RecurrenceType.Daily,
        TimeOfDay = TimeOnly.MinValue,
        NextFireAt = DateTime.Now.AddSeconds(-1),
    };

    /// <summary>
    /// Lifted from <c>HeadlessRunLauncherTests.BuildLauncher</c> (the reusable fixture) with two changes: the
    /// planner returns a REAL multi-step plan instead of <c>PlanResult.Fallback</c>, and the settings substitute
    /// is handed back so the scheduler can share it.
    /// </summary>
    /// <param name="steering">Batch 08 G5: the steering registry, registered with the per-run scope as well so
    /// the run's own orchestrator reads the SAME instance the launcher writes its cancel sink into. Omitted ⇒ no
    /// registry anywhere, i.e. the pre-Batch-08 launcher every other fact in this file exercises.</param>
    /// <param name="stream">Batch 08 G5: replaces <see cref="Drive"/> so a fact can hold a run INSIDE a step —
    /// the only state a user pause is legal from — instead of only inside the planner. Handed the turn's messages
    /// and the step's own token. The messages are there because two scheduled runs now execute CONCURRENTLY
    /// (hermes #2): a fixture that discriminated on invocation ORDER would be a race, so it discriminates on
    /// <see cref="GoalOf"/> instead.</param>
    private (HeadlessRunLauncher Launcher, StepPlanner Planner, ISettingsService Settings) BuildLauncher(
        AppSettings? appSettings = null,
        IRunSteeringStore? steering = null,
        Func<IList<ChatMessage>, CancellationToken, IAsyncEnumerable<ChatStreamItem>>? stream = null)
    {
        var provider = new AiProvider { Id = Guid.NewGuid(), Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI };
        var persona = new Persona { Name = "Pia", SystemPrompt = "sys" };
        var planner = new StepPlanner();

        var ai = Substitute.For<IAiClientService>();
        ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<ToolCallHandler?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(ci => stream is not null
                ? stream(ci.ArgAt<IList<ChatMessage>>(0), ci.ArgAt<CancellationToken>(6))
                : Drive());

        var composer = Substitute.For<IAssistantPromptComposer>();
        composer.PrepareTurn(Arg.Any<Persona>(), Arg.Any<AiProvider>(), Arg.Any<IReadOnlyList<AtCommand>>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(new AssistantTurnSetup("system", null, SupportsTools: false, WebSearchActive: false));
        var personas = Substitute.For<IPersonaService>();
        personas.ResolveActiveAsync(Arg.Any<WindowMode>(), Arg.Any<UserOperatingMode>()).Returns(persona);
        var providers = Substitute.For<IProviderService>();
        providers.GetDefaultProviderForModeAsync(Arg.Any<WindowMode>()).Returns(provider);
        var titles = Substitute.For<IChatTitleService>();
        titles.GenerateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(appSettings ?? new AppSettings());

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAiClientService>(ai);
        services.AddSingleton<IPluginService>(Substitute.For<IPluginService>());
        services.AddSingleton<IAssistantPromptComposer>(composer);
        services.AddSingleton<IPersonaService>(personas);
        services.AddSingleton<IProviderService>(providers);
        services.AddSingleton<IChatTitleService>(titles);
        services.AddSingleton<ISettingsService>(settings);
        services.AddSingleton<IAgentRunService>(_runs);
        services.AddSingleton<IAssistantChatService>(_chats);
        services.AddSingleton<IAgentPlanner>(planner);
        services.AddSingleton<IAgentVerifier>(new FakeVerifier());
        services.AddSingleton<Func<ITokenMapService>>(_ => () => Substitute.For<ITokenMapService>());
        services.AddSingleton<IExecutingRunStore>(_executing);
        // Batch 08: the loop needs the SAME registry the launcher registers its sink with, or it can never
        // consume the request the launcher's dispatch made possible — the orchestrator's parameter is
        // trailing-optional, so an unregistered store is silently "no steering", i.e. the pre-Batch-08 loop.
        if (steering is not null) services.AddSingleton(steering);
        services.AddTransient<BackgroundAssistantTurnRunner>();
        services.AddTransient<HeadlessTurnExecutor>();
        services.AddTransient<AgentRunOrchestrator>();
        var sp = services.BuildServiceProvider();

        var launcher = new HeadlessRunLauncher(
            sp.GetRequiredService<IServiceScopeFactory>(), _chats, _runs, settings, providers, personas,
            _executing, NullLogger<HeadlessRunLauncher>.Instance, runsBaseDirOverride: _runsBase,
            steering: steering);
        return (launcher, planner, settings);
    }

    private static async IAsyncEnumerable<ChatStreamItem> Drive()
    {
        await Task.Yield();
        yield return new TextDelta("reply");
        yield return new Finished(null, "test-model");
    }

    /// <summary>
    /// The run's goal as <c>HeadlessTurnExecutor</c> seeds it: the opening User message of a fresh launch. Two
    /// scheduled runs of one tick now execute at the same time, so a fixture that wants to treat them
    /// differently must key on WHICH run it is, never on which call arrived first.
    /// </summary>
    private static string GoalOf(IList<ChatMessage> messages) =>
        messages.FirstOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;

    /// <summary>Signals that a step is in flight, then holds it there until its own token is cancelled — the
    /// state a user pause is legal from, and the one <see cref="Drive"/> passes straight through.</summary>
    private static async IAsyncEnumerable<ChatStreamItem> HoldInsideTheStep(
        TaskCompletionSource entered, [EnumeratorCancellation] CancellationToken ct)
    {
        entered.TrySetResult();
        await Task.Delay(Timeout.Infinite, ct);
#pragma warning disable CS0162 // unreachable: the await above never returns normally, but the iterator needs an exit
        yield break;
#pragma warning restore CS0162
    }

    /// <summary>A planner that emits a real N-step plan (never the single-turn degrade), so a run can PARK.</summary>
    private sealed class StepPlanner : IAgentPlanner
    {
        public Func<string, int> StepsFor { get; set; } = _ => 1;
        public Func<Exception>? Throw { get; set; }
        public int PlanCalls { get; private set; }

        public Task<PlanResult> PlanAsync(string goal, RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct)
        {
            PlanCalls++;
            if (Throw is { } mk) throw mk();

            var steps = Enumerable.Range(0, StepsFor(goal)).Select(i => new AgentStep
            {
                Id = Guid.NewGuid(),
                Ordinal = i,
                Title = "S" + i,
                Intent = "do it",
                Status = AgentStepStatus.Pending,
            }).ToList();
            return Task.FromResult(new PlanResult(steps, FallBackToSingleTurn: false));
        }

        public Task<PlanResult> ReplanAsync(RunContext ctx, string? failure, Persona persona, AiProvider provider, CancellationToken ct)
            => Task.FromResult(PlanResult.Fallback);
    }

    /// <summary>
    /// Pass-through decorator over the REAL launcher, recording goal + handle per dispatch. The scheduler owns
    /// the handles otherwise, and a test cannot read a run id it never saw.
    /// </summary>
    private sealed class RecordingLauncher : IHeadlessRunLauncher
    {
        private readonly IHeadlessRunLauncher _inner;
        public RecordingLauncher(IHeadlessRunLauncher inner) => _inner = inner;

        public List<(string Goal, HeadlessRunHandle Handle)> Launched { get; } = new();

        /// <summary>
        /// FORWARDED to the inner launcher, not re-declared: this is a decorator over the REAL launcher, and the
        /// scheduler subscribes to whatever it was handed. A private event here would swallow every raise the
        /// real ResumeAsync makes and the resume-booking facts would silently observe nothing.
        /// </summary>
        public event EventHandler<ResumedRunSettledEventArgs>? ResumedRunSettled
        {
            add => _inner.ResumedRunSettled += value;
            remove => _inner.ResumedRunSettled -= value;
        }

        public async Task<HeadlessRunHandle> LaunchAsync(HeadlessRunRequest req, CancellationToken ct = default)
        {
            var handle = await _inner.LaunchAsync(req, ct);
            Launched.Add((req.Goal, handle));
            return handle;
        }

        public Task<HeadlessRunHandle> LaunchChildAsync(HeadlessRunRequest req, Guid parentRunId,
            string? parentPolicyJson, string? parentWorkspaceRoot, Guid? personaId = null, CancellationToken ct = default)
            => _inner.LaunchChildAsync(req, parentRunId, parentPolicyJson, parentWorkspaceRoot, personaId, ct);

        public Task CancelAsync(Guid runId) => _inner.CancelAsync(runId);
        public Task StopAsync(CancellationToken ct) => _inner.StopAsync(ct);
        public Task RunStartupSweepAsync(CancellationToken ct) => _inner.RunStartupSweepAsync(ct);
    }

    /// <summary>Copy of <c>ScheduledJobBackgroundServiceTests.FakeJobService</c> (it is private there).</summary>
    private sealed class FakeJobService : IScheduledJobService
    {
        private readonly List<ScheduledJob> _due = new();
        public List<(Guid JobId, Guid EntryId)> Completed { get; } = new();
        public List<(Guid JobId, string Reason)> Failed { get; } = new();
        public List<Guid> Advanced { get; } = new();

        /// <summary>Jobs whose schedule was moved on at DISPATCH time (hermes #2), kept apart from
        /// <see cref="Advanced"/> so a fact can say WHICH write it observed.</summary>
        public List<Guid> Dispatched { get; } = new();

        public void SeedDue(ScheduledJob job) => _due.Add(job);

        public Task<IReadOnlyList<ScheduledJob>> GetDueJobsAsync()
            => Task.FromResult<IReadOnlyList<ScheduledJob>>(_due.Where(j => j.NextFireAt <= DateTime.Now).ToList());

        public Task MarkRunCompleteAsync(Guid id, Guid resultEntryId)
        {
            Completed.Add((id, resultEntryId));
            var job = _due.FirstOrDefault(j => j.Id == id);
            if (job is not null) job.NextFireAt = DateTime.Now.AddDays(1);
            return Task.CompletedTask;
        }

        public Task MarkRunFailedAsync(Guid id, string reason)
        {
            Failed.Add((id, reason));
            var job = _due.FirstOrDefault(j => j.Id == id);
            if (job is not null) job.NextFireAt = DateTime.Now.AddDays(1);
            return Task.CompletedTask;
        }

        public Task AdvanceMissedRunAsync(Guid id)
        {
            Advanced.Add(id);
            var job = _due.FirstOrDefault(j => j.Id == id);
            if (job is not null) job.NextFireAt = DateTime.Now.AddDays(1);
            return Task.CompletedTask;
        }

        public Task MarkOccurrenceDispatchedAsync(Guid id)
        {
            // The real service serves this and AdvanceMissedRunAsync from ONE write; so does this fake. No lock
            // needed on these lists even though bookkeeping now runs off the tick: the service takes every
            // IScheduledJobService call under its own bookkeeping lock.
            Dispatched.Add(id);
            var job = _due.FirstOrDefault(j => j.Id == id);
            if (job is not null) job.NextFireAt = DateTime.Now.AddDays(1);
            return Task.CompletedTask;
        }

        /// <summary>
        /// T0-1: health-only bookings, recorded rather than thrown so a park fact here can say that a park books
        /// NOTHING through any door — this list, <see cref="Completed"/> and <see cref="Failed"/> alike. The
        /// resume-side booking itself is pinned in <c>ScheduledJobBackgroundServiceTests</c>.
        /// </summary>
        public List<(Guid JobId, Guid? EntryId, bool Succeeded)> Bookings { get; } = new();

        public Task MarkFiringOutcomeAsync(Guid id, DateTime firedAt, Guid? resultEntryId, bool succeeded)
        {
            Bookings.Add((id, resultEntryId, succeeded));
            return Task.CompletedTask;
        }

        public Task<ScheduledJob> CreateAsync(string name, string query, RecurrenceType recurrence,
            TimeOnly timeOfDay, DayOfWeek? dayOfWeek = null, int? dayOfMonth = null, int? month = null,
            DateTime? specificDate = null, Guid? providerId = null,
            IReadOnlyCollection<string>? grantedTools = null,
            ScheduledJobKind kind = ScheduledJobKind.Research) => throw new NotImplementedException();

        public Task<IReadOnlyList<ScheduledJob>> GetAllAsync() => throw new NotImplementedException();
        public Task<IReadOnlyList<ScheduledJob>> GetActiveAsync() => throw new NotImplementedException();
        public Task<ScheduledJob?> GetAsync(Guid id) => Task.FromResult(_due.FirstOrDefault(j => j.Id == id));

        public Task UpdateAsync(Guid id, string? name = null, string? query = null,
            RecurrenceType? recurrence = null, TimeOnly? timeOfDay = null, DayOfWeek? dayOfWeek = null,
            int? dayOfMonth = null, int? month = null, Guid? providerId = null,
            IReadOnlyCollection<string>? grantedTools = null,
            DateTime? specificDate = null, ScheduledJobKind? kind = null) => throw new NotImplementedException();

        public Task<bool> IsOwnedByThisDeviceAsync(Guid id) => Task.FromResult(true);
        public Task DeleteAsync(Guid id) => throw new NotImplementedException();
        public Task DisableAsync(Guid id) => throw new NotImplementedException();
        public Task EnableAsync(Guid id) => throw new NotImplementedException();
        public Task<IReadOnlyList<ScheduledJob>> GetModifiedSinceAsync(DateTime since) => throw new NotImplementedException();
        public Task UpsertFromSyncAsync(ScheduledJob job) => throw new NotImplementedException();
    }

    private sealed class FakeProviderResolver : IScheduledResearchProviderResolver
    {
        private readonly AiProvider? _provider;
        public FakeProviderResolver(AiProvider? provider) => _provider = provider;
        public Task<AiProvider?> ResolveAsync(Guid? pinnedProviderId) => Task.FromResult(_provider);
    }

    private sealed class FakeNotificationSurface : IScheduledJobNotificationSurface
    {
        public int SuccessCount { get; private set; }
        public int FailureCount { get; private set; }

        public void NotifySuccess(ScheduledJob job, Guid chatId, string chatTitle) => SuccessCount++;
        public void NotifyFailure(ScheduledJob job, string reason) => FailureCount++;
        public Task<bool?> AskUserToRunMissedAsync(ScheduledJob job, DateTime scheduledFireAt)
            => Task.FromResult<bool?>(false);
    }

    /// <summary>Only the research leg resolves a scope; every job here is an AgentTask.</summary>
    private sealed class FakeScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope();

        private sealed class Scope : IServiceScope, IServiceProvider
        {
            public IServiceProvider ServiceProvider => this;
            public object? GetService(Type serviceType) => null;
            public void Dispose() { }
        }
    }
}
