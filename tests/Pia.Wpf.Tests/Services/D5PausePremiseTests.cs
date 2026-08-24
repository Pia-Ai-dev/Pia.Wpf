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
using ReasoningEffort = Pia.Models.ReasoningEffort;

namespace Pia.Tests.Services;

/// <summary>
/// Measured rather than reasoned: <c>HeadlessRunHandle.Completion</c> settles on a PARK and not only on a terminal
/// state, so a park's bookkeeping is written promptly instead of never, and it is not booked as a job failure.
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
    /// A tick returns once the due jobs are DISPATCHED, and each run's outcome is written by a continuation
    /// afterwards. Bounded, so a fact that never settles fails rather than hanging.
    /// </summary>
    private static Task SettleAsync(ScheduledJobBackgroundService bg, CancellationToken ct) =>
        bg.WaitForDispatchedRunsAsync().WaitAsync(TimeSpan.FromSeconds(30), ct);

    // ------------------------------------------ a park settles the handle

    // A run that PARKS settles its Completion — the await returns instead of hanging — while the run row is still
    // NON-terminal and its step still Pending, i.e. resumable.
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

            // A TimeoutException here means a park does not settle the handle at all.
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

    // Completion is the launcher's dispatch lambda, which catches and always runs its finally, so it settles on ANY
    // exit of RunAsync — return, cancel or throw. Pinned with a planner that throws, the exit nothing else takes.
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

    // ------------------------------------ a park costs the other jobs nothing

    // End to end with the REAL launcher and scheduler: the first due job's run parks at its step cap, the SECOND
    // due job of the same tick completes, and the parked job is booked as neither a success nor a failure.
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

            // Observed through the real service: the park did not fail the job, and both occurrences were spent at
            // dispatch (jobs.Dispatched) rather than from the park arm (jobs.Advanced, unused here).
            Assert.Contains(job1.Id, jobs.Dispatched);
            Assert.Empty(jobs.Advanced);
            Assert.Empty(jobs.Failed);
            // Nor through the health-only door: a park is not a firing outcome, so booking one here would burn a
            // strike on work the user can still continue.
            Assert.DoesNotContain(job1.Id, jobs.Bookings.Select(b => b.JobId));
            Assert.Contains(job2.Id, jobs.Completed.Select(c => c.JobId));
        }
        finally
        {
            await real.StopAsync(CancellationToken.None);
        }
    }

    // A tick that awaited a run inside itself let one long agent run delay every other scheduled job.
    // The gate is never opened, so job 1's run is provably unsettled when job 2 dispatches.
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

    // A manual fire of a DIFFERENT job returns Dispatched while another job's run is still unsettled, so a
    // settings-page button cannot be held hostage by a 45-minute run. Different, because the guard is per trigger.
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

    // The scheduler reads the run row AFTER Completion settles and its parked branch is an else if, so a pause that
    // unwinds the dispatch before the row says Paused lands on MarkRunFailedAsync — a strike and a failure toast.
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

    // ---------------------------------- a user pause of a scheduled job

    // A USER pause must leave the row reading Paused BEFORE the dispatch task returns: the scheduler reads the row
    // right after Completion settles, and a row not parked at that instant is booked as a job failure.
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
            // cannot pass through the Planning hole where the CAS loses and writes nothing.
            Assert.Equal(AgentRunState.Running, (await _runs.GetAsync(runId, ct))!.State);
            Assert.True(await steering.PauseAsync(runId, ct));

            await SettleAsync(bg, ct);

            // Asserted first because it is what a reorder breaks. The Dispatched leg is the positive one, so the
            // Empty legs cannot pass vacuously on a tick that did nothing.
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

    // A user pause can arrive at an arbitrary moment, and a run paused mid-step must neither block nor break the
    // OTHER job dispatched alongside it. Only job 1's step is held, and by GOAL: the two runs run concurrently.
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

    // ---------------------------------- two parked generations of one job

    // The schedule moves off the occurrence at dispatch, so a later occurrence launches a FRESH run while the
    // previous one is still parked: the executing-run guard deliberately does not fire, because a park is not live.
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

    // A passed-in steering store must be the SAME instance in the per-run scope, and a passed-in stream is handed
    // the turn's messages so a held step is picked by GOAL — two scheduled runs of one tick execute concurrently.
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
                ? stream(ci.ArgAt<IList<ChatMessage>>(0), ci.ArgAt<CancellationToken>(7))
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
        // All-false: no fact in this file is about the standing tier, and the runner reads it for every call.
        services.AddSingleton(Substitute.For<IToolPermissionService>());
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
        // The loop needs the SAME registry the launcher registers its sink with, or it can never consume the
        // request; the parameter is trailing-optional, so an unregistered store is silently "no steering".
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
    /// The run's goal as the executor seeds it: the opening User message. Two scheduled runs of one tick execute at
    /// the same time, so a fixture must key on WHICH run it is, never on which call arrived first.
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

    // Records goal + handle per dispatch: the scheduler owns the handles otherwise, and a test cannot read a run id
    // it never saw.
    private sealed class RecordingLauncher : IHeadlessRunLauncher
    {
        private readonly IHeadlessRunLauncher _inner;
        public RecordingLauncher(IHeadlessRunLauncher inner) => _inner = inner;

        public List<(string Goal, HeadlessRunHandle Handle)> Launched { get; } = new();

        // FORWARDED, not re-declared: the scheduler subscribes to whatever it was handed, so a private event here
        // would swallow every raise the real launcher makes.
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
        public List<PiaFailure?> FailedDescriptors { get; } = new();
        public List<Guid> Advanced { get; } = new();

        /// <summary>Jobs whose schedule was moved on at DISPATCH time, kept apart from <see cref="Advanced"/> so a
        /// fact can say WHICH write it observed.</summary>
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

        public Task MarkRunFailedAsync(Guid id, string reason, PiaFailure? failure = null)
        {
            Failed.Add((id, reason));
            FailedDescriptors.Add(failure);
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
            // The real service serves this and AdvanceMissedRunAsync from ONE write; so does this fake. No lock is
            // needed even though bookkeeping runs off the tick — the service holds its own bookkeeping lock.
            Dispatched.Add(id);
            var job = _due.FirstOrDefault(j => j.Id == id);
            if (job is not null) job.NextFireAt = DateTime.Now.AddDays(1);
            return Task.CompletedTask;
        }

        // Health-only bookings, recorded rather than thrown, so a park fact can say a park books NOTHING through any
        // door — this list, Completed and Failed alike.
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
            ScheduledJobKind kind = ScheduledJobKind.Research, bool quietOnSuccess = false,
            Guid? personaId = null, ReasoningEffort? reasoningEffort = null,
            string? blueprintKey = null) => throw new NotImplementedException();

        public Task<IReadOnlyList<ScheduledJob>> GetAllAsync() => throw new NotImplementedException();
        public Task<IReadOnlyList<ScheduledJob>> GetActiveAsync() => throw new NotImplementedException();
        public Task<ScheduledJob?> GetAsync(Guid id) => Task.FromResult(_due.FirstOrDefault(j => j.Id == id));

        public Task UpdateAsync(Guid id, string? name = null, string? query = null,
            RecurrenceType? recurrence = null, TimeOnly? timeOfDay = null, DayOfWeek? dayOfWeek = null,
            int? dayOfMonth = null, int? month = null, Guid? providerId = null,
            IReadOnlyCollection<string>? grantedTools = null,
            DateTime? specificDate = null, ScheduledJobKind? kind = null, bool? quietOnSuccess = null,
            Guid? personaId = null, ReasoningEffort? reasoningEffort = null,
            bool clearReasoningEffort = false) => throw new NotImplementedException();

        public Task<bool> IsOwnedByThisDeviceAsync(Guid id) => Task.FromResult(true);
        public Task<bool> IsOwnedByThisDeviceAsync(ScheduledJob job) => Task.FromResult(true);
        public Task DeleteAsync(Guid id) => throw new NotImplementedException();
        public Task DisableAsync(Guid id) => throw new NotImplementedException();
        public Task EnableAsync(Guid id) => throw new NotImplementedException();
        public Task<IReadOnlyList<ScheduledJob>> GetModifiedSinceAsync(DateTime since) => throw new NotImplementedException();
        public Task<int> BackfillRecurrenceDaysAsync() => throw new NotImplementedException();
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
