using System.IO;
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
/// PARK and not only on a terminal state; (ii) because it does, <c>ScheduledJobBackgroundService</c>'s
/// <c>_runLock</c> is released and the next due job runs in the SAME tick; (iii) the park branch already
/// names <c>Paused</c> and already advances the schedule.
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
    /// step cap, and the SECOND due job of the same tick still launches and completes. The head-of-line block
    /// R15 describes is bounded by the park, not by the parked run's eventual resume.
    /// <para>
    /// Mechanism, stated precisely: <c>ExecuteOnceAsync</c> awaits each <c>RunJobAsync</c> in a sequential
    /// foreach (<c>ScheduledJobBackgroundService.cs:92-96</c>) and <c>ExecuteAgentTaskAsync</c> holds
    /// <c>_runLock</c> across <c>await handle.Completion</c> (<c>:230</c>), so "job 2 launched" is evidence that
    /// job 1's dispatch returned AND its lock was released.
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

            var parked = await _runs.GetAsync(recorder.Launched[0].Handle.RunId, ct);
            var completed = await _runs.GetAsync(recorder.Launched[1].Handle.RunId, ct);
            Assert.Equal(AgentRunState.WaitingForInput, parked!.State); // job 1 is STILL parked …
            Assert.Contains("step-cap", parked.ExtraJson ?? string.Empty);
            Assert.Equal(AgentRunState.Completed, completed!.State);    // … while job 2 ran to completion

            // (iii) observed through the real service: the park advanced the schedule and did not fail the job.
            Assert.Contains(job1.Id, jobs.Advanced);
            Assert.Empty(jobs.Failed);
            Assert.Contains(job2.Id, jobs.Completed.Select(c => c.JobId));
        }
        finally
        {
            await real.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// D5(ii)'s CONTROL, and the fact that gives the test above its meaning: the tick really is blocked while a
    /// dispatch's <c>Completion</c> is unsettled. Without this, "job 2 ran" would be consistent with a scheduler
    /// that never serialises at all.
    /// </summary>
    [Fact]
    public async Task UnsettledCompletion_HoldsTheTick_SoTheSecondDueJobWaits()
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

        var tick = bg.ExecuteOnceAsync(ct);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref launches) == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10, ct);

        Assert.Equal(1, Volatile.Read(ref launches)); // job 2 has NOT been dispatched …
        Assert.False(tick.IsCompleted);               // … and the tick is still inside job 1

        gate.SetResult();                             // the park settles job 1's Completion
        await tick.WaitAsync(TimeSpan.FromSeconds(10), ct);

        Assert.Equal(2, Volatile.Read(ref launches)); // only now does job 2 dispatch
    }

    /// <summary>
    /// The lock itself, not the loop. <c>RunNowAsync</c> takes <c>_runLock</c> independently of the tick's
    /// sequential foreach, so a manual fire is the direct probe: it BLOCKS while an in-flight dispatch holds the
    /// lock and returns <c>Dispatched</c> the moment that dispatch's <c>Completion</c> settles — which, by
    /// <see cref="Park_SettlesTheHandleCompletion_AndLeavesTheRunResumable"/>, a park does.
    /// </summary>
    [Fact]
    public async Task RunLock_IsHeldAcrossAnUnsettledCompletion_AndFreedWhenItSettles()
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

        var tick = bg.ExecuteOnceAsync(ct);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref launches) == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10, ct);
        Assert.Equal(1, Volatile.Read(ref launches));

        var manualFire = bg.RunNowAsync(manual.Id, ct);
        await Task.Delay(250, ct);
        Assert.False(manualFire.IsCompleted);          // queued on _runLock, held by the in-flight dispatch
        Assert.Equal(1, Volatile.Read(ref launches));

        gate.SetResult();                              // the park settles the first Completion → lock released
        await tick.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Equal(ScheduledJobRunNowResult.Dispatched, await manualFire.WaitAsync(TimeSpan.FromSeconds(10), ct));
        Assert.Equal(2, Volatile.Read(ref launches));
    }

    /// <summary>
    /// The ORDERING requirement D5 does not state, and the one way (ii) can still bite: the scheduler reads the
    /// run row AFTER <c>Completion</c> settles (<c>ScheduledJobBackgroundService.cs:244</c>), and its
    /// <c>WaitingForInput or Paused</c> branch is an <c>else if</c>. A pause that unwinds the dispatch BEFORE the
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

        Assert.Single(jobs.Failed);
        Assert.Equal("Cancelled", jobs.Failed[0].Reason);
        Assert.Equal(1, notifications.FailureCount);
        Assert.Empty(jobs.Advanced);
    }

    // ------------------------------------------------- D5's new consequence

    /// <summary>
    /// D5's "one consequence that is new": <c>AdvanceMissedRunAsync</c> moves <c>NextFireAt</c> forward, so when
    /// the next occurrence comes due the job launches a FRESH run while the previous one is still parked. Nothing
    /// guards it — <c>AgentRuns.TriggerRef</c> is indexed (<c>SqliteContext.cs:315</c>) and read by nobody — so
    /// two live, independently resumable runs of one job coexist.
    /// </summary>
    [Fact]
    public async Task ParkedScheduledRun_AndTheNextOccurrenceOfTheSameJob_Coexist_WithNoGuard()
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
            await bg.ExecuteOnceAsync(ct);               // generation 1 → parks, schedule advanced
            Assert.Single(recorder.Launched);
            Assert.Contains(job.Id, jobs.Advanced);

            job.NextFireAt = DateTime.Now.AddSeconds(-1); // the advanced occurrence comes due
            await bg.ExecuteOnceAsync(ct);               // generation 2 of the SAME job

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
    private (HeadlessRunLauncher Launcher, StepPlanner Planner, ISettingsService Settings) BuildLauncher(
        AppSettings? appSettings = null)
    {
        var provider = new AiProvider { Id = Guid.NewGuid(), Name = "P", Endpoint = "https://x", ProviderType = AiProviderType.OpenAI };
        var persona = new Persona { Name = "Pia", SystemPrompt = "sys" };
        var planner = new StepPlanner();

        var ai = Substitute.For<IAiClientService>();
        ai.GetChatCompletionWithToolsAsync(
                Arg.Any<IList<ChatMessage>>(), Arg.Any<AiProvider>(), Arg.Any<IList<AITool>?>(),
                Arg.Any<Func<FunctionCallContent, Task<object?>>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Drive());

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
        services.AddTransient<BackgroundAssistantTurnRunner>();
        services.AddTransient<HeadlessTurnExecutor>();
        services.AddTransient<AgentRunOrchestrator>();
        var sp = services.BuildServiceProvider();

        var launcher = new HeadlessRunLauncher(
            sp.GetRequiredService<IServiceScopeFactory>(), _chats, _runs, settings, providers, personas,
            _executing, NullLogger<HeadlessRunLauncher>.Instance, runsBaseDirOverride: _runsBase);
        return (launcher, planner, settings);
    }

    private static async IAsyncEnumerable<ChatStreamItem> Drive()
    {
        await Task.Yield();
        yield return new TextDelta("reply");
        yield return new Finished(null, "test-model");
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
