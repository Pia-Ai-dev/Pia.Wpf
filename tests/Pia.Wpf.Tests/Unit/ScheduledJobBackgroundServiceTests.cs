using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Wpf.Tests.Unit;

public class ScheduledJobBackgroundServiceTests
{
    private static ScheduledJob NewDueJob() => new()
    {
        Name = "T",
        Query = "q",
        Recurrence = RecurrenceType.Daily,
        TimeOfDay = TimeOnly.MinValue,
        NextFireAt = DateTime.Now.AddSeconds(-1)
    };

    private static AiProvider NewProvider() => new()
    {
        Id = Guid.NewGuid(),
        Name = "P",
        Endpoint = "https://example",
        TimeoutSeconds = 60
    };

    /// <summary>
    /// Any test whose job reaches <c>ExecuteAgentTaskAsync</c> MUST use this rather than a bare
    /// <c>Substitute.For&lt;ISettingsService&gt;()</c>. <c>ISettingsService.GetSettingsAsync</c> returns a
    /// non-nullable <c>Task&lt;AppSettings&gt;</c>, so production always has settings — but an unstubbed
    /// NSubstitute double hands back a completed task wrapping <c>null</c>, and the AgentTask path
    /// dereferences it immediately to build the run budget. The result is a NullReferenceException from
    /// production code that cannot happen in production, which masks whatever the test meant to assert.
    /// The defaults (24 steps / 2 replans / 45 min) are what RunProfile.FromBudget clamps against.
    /// </summary>
    private static ISettingsService NewSettings()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());
        return settings;
    }

    [Fact]
    public async Task RunNowAsync_DispatchesAJobThatIsNotDue()
    {
        // The whole point of the manual surface: a job whose NextFireAt is in the FUTURE never appears in the
        // due query, so nothing else in this service would ever run it.
        var jobs = new FakeJobService();
        var notDue = NewDueJob();
        notDue.NextFireAt = DateTime.Now.AddDays(3);
        jobs.SeedDue(notDue);

        var runner = new FakeRunner { Result = new BackgroundTurnResult(Guid.NewGuid(), true, null) };
        var bg = new ScheduledJobBackgroundService(
            jobs, new FakeScopeFactory(new FakeServiceProvider().Add<IBackgroundAssistantTurnRunner>(runner)),
            new FakeProviderResolver(NewProvider()), new FakeNotificationSurface(),
            Substitute.For<IHeadlessRunLauncher>(), NewSettings(), Substitute.For<IAgentRunService>(),
            NullLogger<ScheduledJobBackgroundService>.Instance);

        // A tick alone proves the premise: this job is not due, so nothing runs.
        await bg.ExecuteOnceAsync(CancellationToken.None);
        Assert.Equal(0, runner.RunCount);

        var result = await bg.RunNowAsync(notDue.Id, CancellationToken.None);

        Assert.Equal(ScheduledJobRunNowResult.Dispatched, result);
        Assert.Equal(1, runner.RunCount);
    }

    [Fact]
    public async Task RunNowAsync_RefusesAJobOwnedByAnotherDevice_AndRunsNothing()
    {
        // The guardrail 09 inherits: only the owner device advances a job, or two machines double-fire it.
        // A manual button must not be able to do what this device's own scheduler is forbidden to do.
        var jobs = new FakeJobService { OwnedByThisDevice = false };
        var job = NewDueJob();
        jobs.SeedDue(job);

        var runner = new FakeRunner { Result = new BackgroundTurnResult(Guid.NewGuid(), true, null) };
        var bg = new ScheduledJobBackgroundService(
            jobs, new FakeScopeFactory(new FakeServiceProvider().Add<IBackgroundAssistantTurnRunner>(runner)),
            new FakeProviderResolver(NewProvider()), new FakeNotificationSurface(),
            Substitute.For<IHeadlessRunLauncher>(), NewSettings(), Substitute.For<IAgentRunService>(),
            NullLogger<ScheduledJobBackgroundService>.Instance);

        var result = await bg.RunNowAsync(job.Id, CancellationToken.None);

        Assert.Equal(ScheduledJobRunNowResult.NotOwner, result);
        Assert.Equal(0, runner.RunCount);
        Assert.Empty(jobs.Completed);
    }

    [Fact]
    public async Task RunNowAsync_ReportsNotFound_ForAnIdThatIsGone()
    {
        // Deleted underneath an open list. Distinguished from NotOwner so the UI can say which happened.
        var jobs = new FakeJobService();
        var bg = new ScheduledJobBackgroundService(
            jobs, new FakeScopeFactory(new FakeServiceProvider()), new FakeProviderResolver(NewProvider()),
            new FakeNotificationSurface(), Substitute.For<IHeadlessRunLauncher>(), NewSettings(),
            Substitute.For<IAgentRunService>(), NullLogger<ScheduledJobBackgroundService>.Instance);

        Assert.Equal(ScheduledJobRunNowResult.NotFound,
            await bg.RunNowAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteOnceAsync_Success_MarksCompleteWithChatIdAndNotifies()
    {
        var jobs = new FakeJobService();
        var due = NewDueJob();
        jobs.SeedDue(due);

        var chatId = Guid.NewGuid();
        var runner = new FakeRunner { Result = new BackgroundTurnResult(chatId, true, null) };
        var scopeFactory = new FakeScopeFactory(new FakeServiceProvider().Add<IBackgroundAssistantTurnRunner>(runner));
        var providers = new FakeProviderResolver(NewProvider());
        var notifications = new FakeNotificationSurface();

        var bg = new ScheduledJobBackgroundService(
            jobs, scopeFactory, providers, notifications,
            Substitute.For<IHeadlessRunLauncher>(), Substitute.For<ISettingsService>(), Substitute.For<IAgentRunService>(),
            NullLogger<ScheduledJobBackgroundService>.Instance);

        await bg.ExecuteOnceAsync(CancellationToken.None);

        Assert.Equal(1, runner.RunCount);
        Assert.Single(jobs.Completed);
        Assert.Equal(due.Id, jobs.Completed[0].JobId);
        Assert.Equal(chatId, jobs.Completed[0].EntryId);
        Assert.Equal(1, notifications.SuccessCount);
        Assert.Equal(chatId, notifications.LastSuccessChatId);
        Assert.Equal(0, notifications.FailureCount);
    }

    [Fact]
    public async Task ExecuteOnceAsync_Success_PassesScheduleProvenanceToRunner()
    {
        // The scheduled path must wire Trigger=Schedule, TriggerRef=job.Id, and OwnerDeviceId=
        // job.OwnerDeviceId into the BackgroundTurnRequest handed to the runner.
        var jobs = new FakeJobService();
        var due = NewDueJob();
        due.OwnerDeviceId = Guid.NewGuid();
        jobs.SeedDue(due);

        var runner = new FakeRunner { Result = new BackgroundTurnResult(Guid.NewGuid(), true, null) };
        var scopeFactory = new FakeScopeFactory(new FakeServiceProvider().Add<IBackgroundAssistantTurnRunner>(runner));
        var providers = new FakeProviderResolver(NewProvider());
        var notifications = new FakeNotificationSurface();

        var bg = new ScheduledJobBackgroundService(
            jobs, scopeFactory, providers, notifications,
            Substitute.For<IHeadlessRunLauncher>(), Substitute.For<ISettingsService>(), Substitute.For<IAgentRunService>(),
            NullLogger<ScheduledJobBackgroundService>.Instance);

        await bg.ExecuteOnceAsync(CancellationToken.None);

        Assert.NotNull(runner.LastRequest);
        Assert.Equal(AgentRunTrigger.Schedule, runner.LastRequest!.Trigger);
        Assert.Equal(due.Id, runner.LastRequest.TriggerRef);
        Assert.Equal(due.OwnerDeviceId, runner.LastRequest.OwnerDeviceId);
    }

    [Fact]
    public async Task ExecuteOnceAsync_RunnerReturnsFailure_MarksFailedAndNotifies()
    {
        var jobs = new FakeJobService();
        var due = NewDueJob();
        jobs.SeedDue(due);

        var runner = new FakeRunner { Result = new BackgroundTurnResult(Guid.NewGuid(), false, "test failure") };
        var scopeFactory = new FakeScopeFactory(new FakeServiceProvider().Add<IBackgroundAssistantTurnRunner>(runner));
        var providers = new FakeProviderResolver(NewProvider());
        var notifications = new FakeNotificationSurface();

        var bg = new ScheduledJobBackgroundService(
            jobs, scopeFactory, providers, notifications,
            Substitute.For<IHeadlessRunLauncher>(), Substitute.For<ISettingsService>(), Substitute.For<IAgentRunService>(),
            NullLogger<ScheduledJobBackgroundService>.Instance);

        await bg.ExecuteOnceAsync(CancellationToken.None);

        Assert.Single(jobs.Failed);
        Assert.Equal(due.Id, jobs.Failed[0].JobId);
        Assert.Equal("test failure", jobs.Failed[0].Reason);
        Assert.Empty(jobs.Completed);
        Assert.Equal(0, notifications.SuccessCount);
        Assert.Equal(1, notifications.FailureCount);
    }

    [Fact]
    public async Task ExecuteOnceAsync_RunnerThrows_MarksFailedAndNotifies()
    {
        var jobs = new FakeJobService();
        var due = NewDueJob();
        jobs.SeedDue(due);

        var runner = new FakeRunner { ThrowMessage = "boom" };
        var scopeFactory = new FakeScopeFactory(new FakeServiceProvider().Add<IBackgroundAssistantTurnRunner>(runner));
        var providers = new FakeProviderResolver(NewProvider());
        var notifications = new FakeNotificationSurface();

        var bg = new ScheduledJobBackgroundService(
            jobs, scopeFactory, providers, notifications,
            Substitute.For<IHeadlessRunLauncher>(), Substitute.For<ISettingsService>(), Substitute.For<IAgentRunService>(),
            NullLogger<ScheduledJobBackgroundService>.Instance);

        await bg.ExecuteOnceAsync(CancellationToken.None);

        Assert.Single(jobs.Failed);
        Assert.Equal("boom", jobs.Failed[0].Reason);
        Assert.Equal(1, notifications.FailureCount);
    }

    [Fact]
    public async Task ExecuteOnceAsync_NoProvider_MarksFailedAndDoesNotRun()
    {
        var jobs = new FakeJobService();
        var due = NewDueJob();
        jobs.SeedDue(due);

        var runner = new FakeRunner();
        var scopeFactory = new FakeScopeFactory(new FakeServiceProvider().Add<IBackgroundAssistantTurnRunner>(runner));
        var providers = new FakeProviderResolver(null);
        var notifications = new FakeNotificationSurface();

        var bg = new ScheduledJobBackgroundService(
            jobs, scopeFactory, providers, notifications,
            Substitute.For<IHeadlessRunLauncher>(), Substitute.For<ISettingsService>(), Substitute.For<IAgentRunService>(),
            NullLogger<ScheduledJobBackgroundService>.Instance);

        await bg.ExecuteOnceAsync(CancellationToken.None);

        Assert.Single(jobs.Failed);
        Assert.Equal("NoProvider", jobs.Failed[0].Reason);
        Assert.Equal(0, runner.RunCount);
        Assert.Equal(1, notifications.FailureCount);
    }

    [Fact]
    public async Task ExecuteOnceAsync_AgentTaskJob_NoProvider_PassesTheSharedPreModelReasonAndDoesNotLaunch()
    {
        // PASSES BEFORE AND AFTER this change (the reason's VALUE is unchanged) — a parity guard, not a
        // regression test. ScheduledJobService keys the ONE retryable failure off this exact reason value, so
        // BOTH dispatch legs must hand it the same constant; a literal typo'd apart in the AgentTask leg would
        // silently downgrade agent one-offs back to dying on the first blip, and no assertion in
        // ScheduledJobServiceTests could see it. FakeJobService deliberately does not model the retry — the
        // re-arm behaviour itself is pinned against the real service.
        var jobs = new FakeJobService();
        var due = NewDueJob();
        due.Kind = ScheduledJobKind.AgentTask;
        jobs.SeedDue(due);

        var launcher = Substitute.For<IHeadlessRunLauncher>();
        var notifications = new FakeNotificationSurface();

        var bg = new ScheduledJobBackgroundService(
            jobs, new FakeScopeFactory(new FakeServiceProvider()), new FakeProviderResolver(null), notifications,
            launcher, NewSettings(), Substitute.For<IAgentRunService>(),
            NullLogger<ScheduledJobBackgroundService>.Instance);

        await bg.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Single(jobs.Failed);
        Assert.Equal(ScheduledJobService.NoProviderFailureReason, jobs.Failed[0].Reason);
        await launcher.DidNotReceive().LaunchAsync(Arg.Any<HeadlessRunRequest>(), Arg.Any<CancellationToken>());
        Assert.Equal(1, notifications.FailureCount);
    }

    [Fact]
    public async Task ExecuteOnceAsync_LateBy20Min_AsksUserAndSkipsIfDeclined()
    {
        var jobs = new FakeJobService();
        var late = new ScheduledJob
        {
            Name = "T", Query = "q", Recurrence = RecurrenceType.Daily,
            TimeOfDay = TimeOnly.MinValue, NextFireAt = DateTime.Now.AddMinutes(-20)
        };
        jobs.SeedDue(late);

        var notifications = new FakeNotificationSurface { AskAnswer = false };
        var runner = new FakeRunner();
        var providers = new FakeProviderResolver(NewProvider());

        var sp = new FakeServiceProvider().Add<IBackgroundAssistantTurnRunner>(runner);
        var bg = new ScheduledJobBackgroundService(jobs, new FakeScopeFactory(sp), providers, notifications, Substitute.For<IHeadlessRunLauncher>(), Substitute.For<ISettingsService>(), Substitute.For<IAgentRunService>(), NullLogger<ScheduledJobBackgroundService>.Instance);

        await bg.ExecuteOnceAsync(CancellationToken.None);

        Assert.Equal(0, runner.RunCount);
        Assert.Equal(1, notifications.AskCount);
        // Skip should advance NextFireAt without incrementing failure counter.
        Assert.Single(jobs.Advanced);
        Assert.Equal(late.Id, jobs.Advanced[0]);
        Assert.Empty(jobs.Failed);
    }

    [Fact]
    public async Task ExecuteOnceAsync_LateBy20Min_RunsIfAccepted()
    {
        var jobs = new FakeJobService();
        var late = new ScheduledJob
        {
            Name = "T", Query = "q", Recurrence = RecurrenceType.Daily,
            TimeOfDay = TimeOnly.MinValue, NextFireAt = DateTime.Now.AddMinutes(-20)
        };
        jobs.SeedDue(late);

        var notifications = new FakeNotificationSurface { AskAnswer = true };
        var runner = new FakeRunner { Result = new BackgroundTurnResult(Guid.NewGuid(), true, null) };
        var providers = new FakeProviderResolver(NewProvider());

        var sp = new FakeServiceProvider().Add<IBackgroundAssistantTurnRunner>(runner);
        var bg = new ScheduledJobBackgroundService(jobs, new FakeScopeFactory(sp), providers, notifications, Substitute.For<IHeadlessRunLauncher>(), Substitute.For<ISettingsService>(), Substitute.For<IAgentRunService>(), NullLogger<ScheduledJobBackgroundService>.Instance);

        await bg.ExecuteOnceAsync(CancellationToken.None);

        Assert.Equal(1, runner.RunCount);
        Assert.Single(jobs.Completed);
    }

    [Fact]
    public async Task ExecuteOnceAsync_LateBy20Min_DedupesPromptOnSecondTickIfUnanswered()
    {
        var jobs = new FakeJobService();
        var late = new ScheduledJob
        {
            Name = "T", Query = "q", Recurrence = RecurrenceType.Daily,
            TimeOfDay = TimeOnly.MinValue, NextFireAt = DateTime.Now.AddMinutes(-20)
        };
        jobs.SeedDue(late);

        var notifications = new FakeNotificationSurface
        {
            // Simulate "user closed without answering" — return null.
            AskAnswer = null
        };
        var runner = new FakeRunner();
        var providers = new FakeProviderResolver(NewProvider());

        var sp = new FakeServiceProvider().Add<IBackgroundAssistantTurnRunner>(runner);
        var bg = new ScheduledJobBackgroundService(jobs, new FakeScopeFactory(sp), providers, notifications, Substitute.For<IHeadlessRunLauncher>(), Substitute.For<ISettingsService>(), Substitute.For<IAgentRunService>(), NullLogger<ScheduledJobBackgroundService>.Instance);

        await bg.ExecuteOnceAsync(CancellationToken.None);
        await bg.ExecuteOnceAsync(CancellationToken.None);

        Assert.Equal(1, notifications.AskCount); // not 2
    }

    [Fact]
    public async Task ExecuteOnceAsync_AgentTaskJob_DispatchesToLauncherWithScheduleProvenanceAndScheduledBudget()
    {
        // §17.1-2 / §17.7: an AgentTask job runs as an unattended headless Planned run via the launcher —
        // NOT the research runner — carrying Schedule provenance, the job's write grants, and the
        // scheduled budget (RunProfile.Scheduled = 45 min) from settings.
        var jobs = new FakeJobService();
        var due = NewDueJob();
        due.Kind = ScheduledJobKind.AgentTask;
        due.ProviderId = Guid.NewGuid();
        due.GrantedTools = new List<string> { "write_file" };
        jobs.SeedDue(due);

        var providers = new FakeProviderResolver(NewProvider());
        var notifications = new FakeNotificationSurface();

        // Seed a NON-default wall-clock (50, not the 45 shared by AppSettings' default and RunProfile.Scheduled)
        // so the assertion proves the setting actually flows through to the budget, not a hardcoded constant.
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings { ScheduledWallClockMinutes = 50 });

        var runId = Guid.NewGuid();
        var chatId = Guid.NewGuid();
        HeadlessRunRequest? captured = null;
        var launcher = Substitute.For<IHeadlessRunLauncher>();
        launcher.LaunchAsync(Arg.Do<HeadlessRunRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(new HeadlessRunHandle(runId, chatId, Task.CompletedTask));

        var runService = Substitute.For<IAgentRunService>();
        runService.GetAsync(runId, Arg.Any<CancellationToken>())
            .Returns(new AgentRun { Id = runId, ChatId = chatId, RunShape = RunShape.Planned, State = AgentRunState.Completed });

        var bg = new ScheduledJobBackgroundService(
            jobs, new FakeScopeFactory(new FakeServiceProvider()), providers, notifications,
            launcher, settings, runService,
            NullLogger<ScheduledJobBackgroundService>.Instance);

        await bg.ExecuteOnceAsync(CancellationToken.None);

        await launcher.Received(1).LaunchAsync(Arg.Any<HeadlessRunRequest>(), Arg.Any<CancellationToken>());
        Assert.NotNull(captured);
        Assert.Equal(due.Query, captured!.Goal);
        Assert.Equal(AgentRunTrigger.Schedule, captured.Trigger);
        Assert.Equal(due.Id, captured.TriggerRef);
        Assert.Equal(due.OwnerDeviceId, captured.OwnerDeviceId);
        Assert.Equal(due.ProviderId, captured.ProviderId);
        Assert.Equal(due.GrantedTools, captured.GrantedWrites);
        Assert.NotNull(captured.Budget);
        Assert.Equal(50, captured.Budget!.WallClock.TotalMinutes);
        // Completed terminal run → job marked complete with the run's chat id + a success notification.
        Assert.Single(jobs.Completed);
        Assert.Equal(chatId, jobs.Completed[0].EntryId);
        Assert.Equal(1, notifications.SuccessCount);
    }

    [Fact]
    public async Task ExecuteOnceAsync_AgentTaskJob_ParkedAtBudget_AdvancesScheduleOnce_AndDoesNotRelaunch()
    {
        // F: a parked (budget-paused) run is NOT a job failure — but the schedule must still advance.
        // Only MarkRunComplete/MarkRunFailed used to recompute NextFireAt, so the job stayed due and the
        // next 30 s tick launched a DUPLICATE run of the same goal (and, past the grace period, prompted
        // the user about a "missed" run that had in fact already fired).
        var jobs = new FakeJobService();
        var due = NewDueJob();
        due.Kind = ScheduledJobKind.AgentTask;
        jobs.SeedDue(due);

        var runId = Guid.NewGuid();
        var chatId = Guid.NewGuid();
        var launcher = Substitute.For<IHeadlessRunLauncher>();
        launcher.LaunchAsync(Arg.Any<HeadlessRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(new HeadlessRunHandle(runId, chatId, Task.CompletedTask));

        var runService = Substitute.For<IAgentRunService>();
        runService.GetAsync(runId, Arg.Any<CancellationToken>())
            .Returns(new AgentRun
            {
                Id = runId, ChatId = chatId, RunShape = RunShape.Planned,
                State = AgentRunState.WaitingForInput,
            });

        var notifications = new FakeNotificationSurface();
        var bg = new ScheduledJobBackgroundService(
            jobs, new FakeScopeFactory(new FakeServiceProvider()), new FakeProviderResolver(NewProvider()),
            notifications, launcher, NewSettings(), runService,
            NullLogger<ScheduledJobBackgroundService>.Instance);

        await bg.ExecuteOnceAsync(CancellationToken.None);

        // Park is not a failure: no MarkRunFailed, no failure toast, no success bookkeeping either.
        Assert.Empty(jobs.Failed);
        Assert.Empty(jobs.Completed);
        Assert.Equal(0, notifications.FailureCount);
        Assert.Equal(0, notifications.SuccessCount);
        // …but the schedule advanced exactly once.
        Assert.Single(jobs.Advanced);
        Assert.Equal(due.Id, jobs.Advanced[0]);

        // The next tick must NOT relaunch: the job is no longer due, and nothing advances twice.
        await bg.ExecuteOnceAsync(CancellationToken.None);

        await launcher.Received(1).LaunchAsync(Arg.Any<HeadlessRunRequest>(), Arg.Any<CancellationToken>());
        Assert.Single(jobs.Advanced);
        Assert.Equal(0, notifications.AskCount); // and no missed-run prompt for a run that already fired
    }

    [Fact]
    public async Task ExecuteOnceAsync_AgentTaskJob_Paused_AlsoAdvancesSchedule()
    {
        // The park branch covers both non-terminal parked states.
        var jobs = new FakeJobService();
        var due = NewDueJob();
        due.Kind = ScheduledJobKind.AgentTask;
        jobs.SeedDue(due);

        var runId = Guid.NewGuid();
        var launcher = Substitute.For<IHeadlessRunLauncher>();
        launcher.LaunchAsync(Arg.Any<HeadlessRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(new HeadlessRunHandle(runId, Guid.NewGuid(), Task.CompletedTask));

        var runService = Substitute.For<IAgentRunService>();
        runService.GetAsync(runId, Arg.Any<CancellationToken>())
            .Returns(new AgentRun { Id = runId, RunShape = RunShape.Planned, State = AgentRunState.Paused });

        var bg = new ScheduledJobBackgroundService(
            jobs, new FakeScopeFactory(new FakeServiceProvider()), new FakeProviderResolver(NewProvider()),
            new FakeNotificationSurface(), launcher, NewSettings(), runService,
            NullLogger<ScheduledJobBackgroundService>.Instance);

        await bg.ExecuteOnceAsync(CancellationToken.None);

        Assert.Single(jobs.Advanced);
        Assert.Empty(jobs.Failed);
    }

    [Fact]
    public async Task ExecuteOnceAsync_ParkedJob_AdvanceThrows_DoesNotBreakTheTick()
    {
        // Guardrail 1: the schedule advance is bookkeeping. If it faults, the tick must keep going (the
        // job simply stays due and re-runs, exactly as it did before this fix) — never an aborted tick.
        var jobs = new FakeJobService { ThrowOnAdvance = true };
        var parkedJob = NewDueJob();
        parkedJob.Kind = ScheduledJobKind.AgentTask;
        var researchJob = NewDueJob();
        jobs.SeedDue(parkedJob);
        jobs.SeedDue(researchJob);

        var runId = Guid.NewGuid();
        var launcher = Substitute.For<IHeadlessRunLauncher>();
        launcher.LaunchAsync(Arg.Any<HeadlessRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(new HeadlessRunHandle(runId, Guid.NewGuid(), Task.CompletedTask));

        var runService = Substitute.For<IAgentRunService>();
        runService.GetAsync(runId, Arg.Any<CancellationToken>())
            .Returns(new AgentRun { Id = runId, RunShape = RunShape.Planned, State = AgentRunState.WaitingForInput });

        var runner = new FakeRunner { Result = new BackgroundTurnResult(Guid.NewGuid(), true, null) };
        var bg = new ScheduledJobBackgroundService(
            jobs, new FakeScopeFactory(new FakeServiceProvider().Add<IBackgroundAssistantTurnRunner>(runner)),
            new FakeProviderResolver(NewProvider()), new FakeNotificationSurface(), launcher,
            NewSettings(), runService,
            NullLogger<ScheduledJobBackgroundService>.Instance);

        await bg.ExecuteOnceAsync(CancellationToken.None);

        Assert.Equal(1, runner.RunCount);       // the second due job in the same tick still ran
        Assert.Empty(jobs.Failed);              // and the park still did not count as a failure
    }

    [Fact]
    public async Task ExecuteOnceAsync_ResearchJob_DoesNotDispatchToLauncher()
    {
        // Guard the dispatch fork the other way: a Research job stays on the runner path.
        var jobs = new FakeJobService();
        jobs.SeedDue(NewDueJob()); // Kind defaults to Research

        var runner = new FakeRunner { Result = new BackgroundTurnResult(Guid.NewGuid(), true, null) };
        var scopeFactory = new FakeScopeFactory(new FakeServiceProvider().Add<IBackgroundAssistantTurnRunner>(runner));
        var providers = new FakeProviderResolver(NewProvider());
        var notifications = new FakeNotificationSurface();
        var launcher = Substitute.For<IHeadlessRunLauncher>();

        var bg = new ScheduledJobBackgroundService(
            jobs, scopeFactory, providers, notifications,
            launcher, Substitute.For<ISettingsService>(), Substitute.For<IAgentRunService>(),
            NullLogger<ScheduledJobBackgroundService>.Instance);

        await bg.ExecuteOnceAsync(CancellationToken.None);

        Assert.Equal(1, runner.RunCount);
        await launcher.DidNotReceive().LaunchAsync(Arg.Any<HeadlessRunRequest>(), Arg.Any<CancellationToken>());
    }

    private sealed class FakeJobService : IScheduledJobService
    {
        private readonly List<ScheduledJob> _due = new();
        public List<(Guid JobId, Guid EntryId)> Completed { get; } = new();
        public List<(Guid JobId, string Reason)> Failed { get; } = new();
        public List<Guid> Advanced { get; } = new();

        /// <summary>When set, AdvanceMissedRunAsync faults (the bookkeeping degrade path).</summary>
        public bool ThrowOnAdvance { get; set; }

        public void SeedDue(ScheduledJob job) => _due.Add(job);

        // Models the real query: only jobs whose NextFireAt has passed come back, so a job whose schedule
        // was advanced is genuinely not due on the following tick.
        public Task<IReadOnlyList<ScheduledJob>> GetDueJobsAsync()
            => Task.FromResult<IReadOnlyList<ScheduledJob>>(
                _due.Where(j => j.NextFireAt <= DateTime.Now).ToList());

        public Task MarkRunCompleteAsync(Guid id, Guid resultEntryId)
        {
            Completed.Add((id, resultEntryId));
            return Task.CompletedTask;
        }

        public Task MarkRunFailedAsync(Guid id, string reason)
        {
            Failed.Add((id, reason));
            return Task.CompletedTask;
        }

        public Task AdvanceMissedRunAsync(Guid id)
        {
            if (ThrowOnAdvance) throw new InvalidOperationException("advance boom");

            Advanced.Add(id);
            // Mirror the real service: NextFireAt moves to the next occurrence and NOTHING else — no
            // failure counter, no Status, no LastFiredAt.
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
        // Run-now looks the job up by id rather than taking it off the due list, so this resolves against the
        // same backing collection the due query reads — including rows that are NOT due, which is most of the
        // point of firing one manually.
        public Task<ScheduledJob?> GetAsync(Guid id) =>
            Task.FromResult(_due.FirstOrDefault(j => j.Id == id));

        public Task UpdateAsync(Guid id, string? name = null, string? query = null,
            RecurrenceType? recurrence = null, TimeOnly? timeOfDay = null, DayOfWeek? dayOfWeek = null,
            int? dayOfMonth = null, int? month = null, Guid? providerId = null,
            IReadOnlyCollection<string>? grantedTools = null,
            DateTime? specificDate = null, ScheduledJobKind? kind = null) => throw new NotImplementedException();

        /// <summary>Drives the run-now owner refusal. True by default, which is the ordinary case (a job this
        /// device owns, or a legacy row with a null owner).</summary>
        public bool OwnedByThisDevice { get; set; } = true;

        public Task<bool> IsOwnedByThisDeviceAsync(Guid id) => Task.FromResult(OwnedByThisDevice);

        public Task DeleteAsync(Guid id) => throw new NotImplementedException();
        public Task DisableAsync(Guid id) => throw new NotImplementedException();
        public Task EnableAsync(Guid id) => throw new NotImplementedException();
        public Task<IReadOnlyList<ScheduledJob>> GetModifiedSinceAsync(DateTime since) => throw new NotImplementedException();
        public Task UpsertFromSyncAsync(ScheduledJob job) => throw new NotImplementedException();
    }

    private sealed class FakeRunner : IBackgroundAssistantTurnRunner
    {
        public int RunCount { get; private set; }
        public BackgroundTurnResult Result { get; set; } = new(Guid.NewGuid(), true, null);
        public string? ThrowMessage { get; set; }
        public BackgroundTurnRequest? LastRequest { get; private set; }

        public Task<BackgroundTurnResult> RunAsync(BackgroundTurnRequest request, CancellationToken ct)
        {
            RunCount++;
            LastRequest = request;
            if (ThrowMessage is not null)
                throw new InvalidOperationException(ThrowMessage);
            return Task.FromResult(Result);
        }
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
        public Guid? LastSuccessChatId { get; private set; }
        public bool? AskAnswer { get; set; } = false;
        public int AskCount { get; private set; }
        public TaskCompletionSource<bool?>? PendingAsk { get; set; }

        public void NotifySuccess(ScheduledJob job, Guid chatId, string chatTitle)
        {
            SuccessCount++;
            LastSuccessChatId = chatId;
        }

        public void NotifyFailure(ScheduledJob job, string reason) => FailureCount++;

        public Task<bool?> AskUserToRunMissedAsync(ScheduledJob job, DateTime scheduledFireAt)
        {
            AskCount++;
            if (PendingAsk is not null) return PendingAsk.Task;
            return Task.FromResult(AskAnswer);
        }
    }

    private sealed class FakeScopeFactory : IServiceScopeFactory
    {
        private readonly IServiceProvider _sp;
        public FakeScopeFactory(IServiceProvider sp) => _sp = sp;
        public IServiceScope CreateScope() => new FakeScope(_sp);

        private sealed class FakeScope : IServiceScope
        {
            public FakeScope(IServiceProvider sp) => ServiceProvider = sp;
            public IServiceProvider ServiceProvider { get; }
            public void Dispose() { }
        }
    }

    private sealed class FakeServiceProvider : IServiceProvider
    {
        private readonly Dictionary<Type, object> _services = new();

        public FakeServiceProvider Add<T>(T instance) where T : notnull
        {
            _services[typeof(T)] = instance;
            return this;
        }

        public object? GetService(Type serviceType) =>
            _services.TryGetValue(serviceType, out var svc) ? svc : null;
    }
}
