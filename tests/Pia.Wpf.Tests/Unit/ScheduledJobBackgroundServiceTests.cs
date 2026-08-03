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

    /// <summary>
    /// The join <c>ExecuteOnceAsync</c> no longer contains (hermes #2). A tick returns once every due job has
    /// been DISPATCHED; the run's outcome — <c>jobs.Completed</c>/<c>Failed</c>, the notification counters, and
    /// on the Research leg even <c>FakeRunner.RunCount</c> — is written by a continuation afterwards. Any test
    /// that asserts one of those must come through here, or it is a race that passes on a fast machine.
    /// <para>
    /// Bounded: a drain that hangs fails this test in 30 s instead of the whole suite.
    /// </para>
    /// </summary>
    private static Task SettleAsync(ScheduledJobBackgroundService bg, CancellationToken ct) =>
        bg.WaitForDispatchedRunsAsync().WaitAsync(TimeSpan.FromSeconds(30), ct);

    private static async Task TickAndSettleAsync(ScheduledJobBackgroundService bg, CancellationToken ct)
    {
        await bg.ExecuteOnceAsync(ct);
        await SettleAsync(bg, ct);
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
        await SettleAsync(bg, CancellationToken.None);

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

        await TickAndSettleAsync(bg, CancellationToken.None);

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

        await TickAndSettleAsync(bg, CancellationToken.None);

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

        await TickAndSettleAsync(bg, CancellationToken.None);

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

        await TickAndSettleAsync(bg, CancellationToken.None);

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

        await TickAndSettleAsync(bg, CancellationToken.None);

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

        await TickAndSettleAsync(bg, CancellationToken.None);

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
        // Completed terminal run → job marked complete with the run's chat id + a success notification. Written
        // by the bookkeeping continuation now, which is why the assertions come after the drain.
        Assert.Single(jobs.Completed);
        Assert.Equal(chatId, jobs.Completed[0].EntryId);
        Assert.Equal(1, notifications.SuccessCount);
        // …and the schedule had already moved on at dispatch, before any of that was known.
        Assert.Equal([due.Id], jobs.Dispatched);
    }

    [Fact]
    public async Task ExecuteOnceAsync_AgentTaskJob_ParkedAtBudget_MovesTheScheduleOnceAtDispatch_AndDoesNotRelaunch()
    {
        // F: a parked (budget-paused) run is NOT a job failure — but the schedule must still have moved on.
        // Only MarkRunComplete/MarkRunFailed used to recompute NextFireAt, so the job stayed due and the
        // next 30 s tick launched a DUPLICATE run of the same goal (and, past the grace period, prompted
        // the user about a "missed" run that had in fact already fired).
        //
        // What changed with hermes #2: the write is made at DISPATCH (jobs.Dispatched) rather than from the park
        // arm afterwards (jobs.Advanced), because a park is no longer the only outcome that leaves the row
        // untouched — a run nobody awaits leaves it untouched too. The park arm is now log-only, and
        // Assert.Empty(jobs.Advanced) is what pins that: it must not double-write.
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

        await TickAndSettleAsync(bg, CancellationToken.None);

        // THE PREMISE FIRST (see the sibling Paused fact): the park arm is log-only, so all four absences
        // below are only evidence if the continuation that contains that arm actually ran. GetAsync is its
        // first act. Neutralise `TrackDispatch(BookkeepAgentRunAsync(job, handle));` → this reds.
        await runService.Received(1).GetAsync(runId, Arg.Any<CancellationToken>());

        // Park is not a failure: no MarkRunFailed, no failure toast, no success bookkeeping either.
        Assert.Empty(jobs.Failed);
        Assert.Empty(jobs.Completed);
        Assert.Equal(0, notifications.FailureCount);
        Assert.Equal(0, notifications.SuccessCount);
        // …but the schedule moved on exactly once, at dispatch, and the park arm added nothing on top.
        Assert.Equal([due.Id], jobs.Dispatched);
        Assert.Empty(jobs.Advanced);

        // The next tick must NOT relaunch: the job is no longer due, and nothing moves the schedule twice.
        await TickAndSettleAsync(bg, CancellationToken.None);

        await launcher.Received(1).LaunchAsync(Arg.Any<HeadlessRunRequest>(), Arg.Any<CancellationToken>());
        Assert.Single(jobs.Dispatched);
        Assert.Equal(0, notifications.AskCount); // and no missed-run prompt for a run that already fired
    }

    [Fact]
    public async Task ExecuteOnceAsync_AgentTaskJob_Paused_IsAlsoNotAFailure()
    {
        // The park branch covers both non-terminal parked states — and neither of them is a job failure, which
        // is the half that would break if the arm were collapsed into the else.
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

        await TickAndSettleAsync(bg, CancellationToken.None);

        // THE PREMISE, and the reason this is not a test of absences: every assertion above is something the
        // park arm did NOT do, and the park arm is now log-only — so without this line "the arm ran and
        // correctly wrote nothing" is indistinguishable from "the bookkeeping continuation never ran at all".
        // GetAsync is the arm's own first act, so observing it is what makes the three absences evidence.
        // Neutralise `TrackDispatch(BookkeepAgentRunAsync(job, handle));` → this reds and the rest do not.
        await runService.Received(1).GetAsync(runId, Arg.Any<CancellationToken>());

        Assert.Empty(jobs.Failed);
        Assert.Equal([due.Id], jobs.Dispatched);
        Assert.Empty(jobs.Advanced);
    }

    [Fact]
    public async Task ExecuteOnceAsync_AgentJob_ScheduleWriteThrows_DoesNotBreakTheTick()
    {
        // Guardrail 1: moving the schedule on is bookkeeping. If it faults, the tick must keep going — never an
        // aborted tick that strands the remaining due jobs. Per-job fault (not a global switch) because the
        // research leg answers the same fault by SKIPPING its occurrence, which would silently make the
        // second job here prove nothing.
        var jobs = new FakeJobService();
        var parkedJob = NewDueJob();
        parkedJob.Kind = ScheduledJobKind.AgentTask;
        var researchJob = NewDueJob();
        jobs.ThrowOnDispatchAdvanceFor.Add(parkedJob.Id);
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

        await TickAndSettleAsync(bg, CancellationToken.None);

        Assert.Equal(1, runner.RunCount);       // the second due job in the same tick still ran
        Assert.Empty(jobs.Failed);              // and the park still did not count as a failure
        Assert.Equal([researchJob.Id], jobs.Dispatched); // only the job whose write did not fault
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

        await TickAndSettleAsync(bg, CancellationToken.None);

        Assert.Equal(1, runner.RunCount);
        await launcher.DidNotReceive().LaunchAsync(Arg.Any<HeadlessRunRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteOnceAsync_TwoDueAgentJobs_DispatchesTheSecondWithoutWaitingForTheFirstToSettle()
    {
        // THE HEADLINE FACT (hermes review #2). The tick used to hold one run lock across
        // `await handle.Completion`, so one long agent run delayed every other scheduled job on the device for up
        // to its whole 45-minute wall clock. Job 1's Completion is a gate this test never opens before the
        // assertion, so "job 2 launched AND the tick returned" is only possible if nothing waits for job 1.
        // <para>Restoring the inline await reds this as a TimeoutException on the tick, which is why the tick is
        // awaited with a bound instead of bare — a neutralization must fail the test, not hang the suite.</para>
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
                Id = ci.ArgAt<Guid>(0), RunShape = RunShape.Planned, State = AgentRunState.Completed,
            });

        var jobs = new FakeJobService();
        var slow = NewDueJob();
        var quick = NewDueJob();
        slow.Kind = ScheduledJobKind.AgentTask;
        quick.Kind = ScheduledJobKind.AgentTask;
        jobs.SeedDue(slow);
        jobs.SeedDue(quick);

        var bg = new ScheduledJobBackgroundService(
            jobs, new FakeScopeFactory(new FakeServiceProvider()), new FakeProviderResolver(NewProvider()),
            new FakeNotificationSurface(), launcher, NewSettings(), runService,
            NullLogger<ScheduledJobBackgroundService>.Instance);

        await bg.ExecuteOnceAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);

        Assert.Equal(2, Volatile.Read(ref launches));   // both jobs of the tick dispatched …
        Assert.False(gate.Task.IsCompleted);            // … while job 1's run had NOT settled (non-vacuity)
        Assert.Equal(2, jobs.Dispatched.Count);         // and both occurrences were spent at dispatch

        // The bookkeeping is not lost, only deferred: job 1 is booked when its run finally settles.
        gate.SetResult();
        await SettleAsync(bg, ct);
        Assert.Equal(2, jobs.Completed.Count);
    }

    [Fact]
    public async Task ExecuteOnceAsync_RunThatOutlastsTheInterval_TickedTwice_LaunchesExactlyOnce()
    {
        // LAYER (a) ALONE — the duplicate-dispatch defence, isolated. GetDueJobsAsync's predicate is
        // `NextFireAt <= @Now AND Status = 'Active'` and only the bookkeeping methods used to recompute
        // NextFireAt, so the moment the tick stops awaiting the run, a run outlasting the 30 s interval leaves
        // its job STILL DUE and the next tick launches the same goal again. MarkOccurrenceDispatchedAsync is
        // what closes that, and it is awaited inside the tick for exactly this reason.
        // <para>The guard is deliberately BLIND here: an unstubbed
        // <c>Substitute.For&lt;IAgentRunService&gt;()</c> hands back false for
        // <c>AnyExecutingRunForTriggerAsync</c>, so nothing but the schedule write can explain one launch.
        // Neutralising `await MoveScheduleOnAsync(job)` in the agent leg reds this at two launches.</para>
        var ct = TestContext.Current.CancellationToken;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launches = 0;

        var launcher = Substitute.For<IHeadlessRunLauncher>();
        launcher.LaunchAsync(Arg.Any<HeadlessRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Interlocked.Increment(ref launches);
                return new HeadlessRunHandle(Guid.NewGuid(), Guid.NewGuid(), gate.Task);
            });

        var jobs = new FakeJobService();
        var due = NewDueJob();
        due.Kind = ScheduledJobKind.AgentTask;
        jobs.SeedDue(due);

        var notifications = new FakeNotificationSurface();
        var bg = new ScheduledJobBackgroundService(
            jobs, new FakeScopeFactory(new FakeServiceProvider()), new FakeProviderResolver(NewProvider()),
            notifications, launcher, NewSettings(), Substitute.For<IAgentRunService>(),
            NullLogger<ScheduledJobBackgroundService>.Instance);

        await bg.ExecuteOnceAsync(ct);
        await bg.ExecuteOnceAsync(ct);

        Assert.Equal(1, Volatile.Read(ref launches));
        Assert.Equal([due.Id], jobs.Dispatched);
        Assert.False(gate.Task.IsCompleted);      // the run really did outlast both ticks
        Assert.Equal(0, notifications.AskCount);  // and no missed-run prompt for the run in flight

        gate.SetResult();
        await SettleAsync(bg, ct);
    }

    [Fact]
    public async Task ExecuteOnceAsync_ScheduleWriteFaulted_TheTriggerGuardStillStopsTheSecondDispatch()
    {
        // LAYER (b) ALONE — the case layer (a) provably cannot cover, because here layer (a) FAILED: the
        // dispatch-time schedule write faults (guardrail 1 isolates it), so the job is still due on the second
        // tick with its run in flight. The TriggerRef guard is the only thing left, and this is why it is worth
        // having rather than being a redundant second copy of the same defence.
        // <para>The guard answer is modelled, not canned: a run of this job exists and is non-terminal exactly
        // while a launch has happened and its Completion has not settled — which is what the real
        // `State NOT IN (parked, terminal)` query would report while the run executes. Neutralising ONLY the
        // guard call in RunJobAsync reds this at two launches.</para>
        var ct = TestContext.Current.CancellationToken;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launches = 0;

        var launcher = Substitute.For<IHeadlessRunLauncher>();
        launcher.LaunchAsync(Arg.Any<HeadlessRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Interlocked.Increment(ref launches);
                return new HeadlessRunHandle(Guid.NewGuid(), Guid.NewGuid(), gate.Task);
            });

        var jobs = new FakeJobService();
        var due = NewDueJob();
        due.Kind = ScheduledJobKind.AgentTask;
        jobs.SeedDue(due);
        jobs.ThrowOnDispatchAdvanceFor.Add(due.Id);

        var runService = Substitute.For<IAgentRunService>();
        runService.AnyExecutingRunForTriggerAsync(due.Id, Arg.Any<CancellationToken>())
            .Returns(_ => Volatile.Read(ref launches) > 0 && !gate.Task.IsCompleted);

        var notifications = new FakeNotificationSurface();
        var bg = new ScheduledJobBackgroundService(
            jobs, new FakeScopeFactory(new FakeServiceProvider()), new FakeProviderResolver(NewProvider()),
            notifications, launcher, NewSettings(), runService,
            NullLogger<ScheduledJobBackgroundService>.Instance);

        await bg.ExecuteOnceAsync(ct);
        await bg.ExecuteOnceAsync(ct);

        Assert.Equal(1, Volatile.Read(ref launches));
        Assert.Empty(jobs.Dispatched);             // layer (a) genuinely never wrote anything
        Assert.Empty(jobs.Failed);                 // a refusal is not a job-health signal
        Assert.Equal(0, notifications.FailureCount);

        gate.SetResult();
        await SettleAsync(bg, ct);
    }

    [Fact]
    public async Task RunNowAsync_RefusedWhenARunOfTheJobIsAlreadyExecuting_AndDoesNotConsumeTheOccurrence()
    {
        // The guard's second case, and the one nothing else can reach: a manual fire never consults the due
        // query, so the dispatch-time schedule write cannot protect it. The refusal must also leave the schedule
        // ALONE — a manual fire that was refused must not spend the occurrence the tick is still going to fire.
        var ct = TestContext.Current.CancellationToken;
        var jobs = new FakeJobService();
        var job = NewDueJob();
        job.Kind = ScheduledJobKind.AgentTask;
        jobs.SeedDue(job);

        var launcher = Substitute.For<IHeadlessRunLauncher>();
        var runService = Substitute.For<IAgentRunService>();
        runService.AnyExecutingRunForTriggerAsync(job.Id, Arg.Any<CancellationToken>()).Returns(true);

        var bg = new ScheduledJobBackgroundService(
            jobs, new FakeScopeFactory(new FakeServiceProvider()), new FakeProviderResolver(NewProvider()),
            new FakeNotificationSurface(), launcher, NewSettings(), runService,
            NullLogger<ScheduledJobBackgroundService>.Instance);

        Assert.Equal(ScheduledJobRunNowResult.AlreadyRunning, await bg.RunNowAsync(job.Id, ct));

        await launcher.DidNotReceive().LaunchAsync(Arg.Any<HeadlessRunRequest>(), Arg.Any<CancellationToken>());
        Assert.Empty(jobs.Dispatched);
        Assert.Empty(jobs.Failed);
    }

    /// <summary>
    /// REVIEW FIX (hermes #2 Q4). The duplicate-dispatch guard used to be evaluated ONCE, deliberately before
    /// the grace check — and then the tick awaited an UNBOUNDED human dialog and dispatched without re-testing
    /// it. So the yes-answer could launch a second concurrent unattended run of the same goal, with real token
    /// spend, while <c>AnyExecutingRunForTriggerAsync</c> was true: a manual <c>RunNowAsync</c> from Settings (no
    /// longer serialized against the tick since <c>_runLock</c> went) or a user resuming a parked run of this
    /// job is enough to open that window. Layer (a) cannot cover it — the schedule write happens INSIDE the
    /// dispatch that is about to be duplicated.
    /// <para>
    /// The <c>PendingAsk</c> seam this drives had ZERO call sites before the review pass, which is precisely why
    /// no shipped test could see the transition window. Same shape of blind spot as Batch 08's pause: the
    /// decision is keyed on a state that is only briefly wrong, and every existing test observed it settled.
    /// </para>
    /// <para>Neutralize: delete the second <c>RefuseIfAlreadyExecutingAsync</c> call from the yes-path of
    /// <c>RunJobAsync</c> → red on <c>DidNotReceive</c>.</para>
    /// </summary>
    [Fact]
    public async Task ExecuteOnceAsync_MissedRunAnsweredYes_WhileARunOfTheJobStarted_DoesNotDispatchASecond()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobs = new FakeJobService();
        var late = new ScheduledJob
        {
            Name = "T", Query = "q", Recurrence = RecurrenceType.Daily, Kind = ScheduledJobKind.AgentTask,
            TimeOfDay = TimeOnly.MinValue, NextFireAt = DateTime.Now.AddMinutes(-20)
        };
        jobs.SeedDue(late);

        var launcher = Substitute.For<IHeadlessRunLauncher>();
        launcher.LaunchAsync(Arg.Any<HeadlessRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(new HeadlessRunHandle(Guid.NewGuid(), Guid.NewGuid(), Task.CompletedTask));

        // Nothing is executing when the guard is first evaluated; a run appears WHILE the dialog is open.
        var askSeen = 0;
        var runService = Substitute.For<IAgentRunService>();
        runService.AnyExecutingRunForTriggerAsync(late.Id, Arg.Any<CancellationToken>())
            .Returns(_ => Volatile.Read(ref askSeen) > 0);

        var notifications = new FakeNotificationSurface
        {
            PendingAsk = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var bg = new ScheduledJobBackgroundService(
            jobs, new FakeScopeFactory(new FakeServiceProvider()), new FakeProviderResolver(NewProvider()),
            notifications, launcher, NewSettings(), runService,
            NullLogger<ScheduledJobBackgroundService>.Instance);

        var tick = bg.ExecuteOnceAsync(ct);
        Assert.True(SpinWait.SpinUntil(() => notifications.AskCount == 1, 5000), "the dialog never opened");
        Volatile.Write(ref askSeen, 1);       // a manual RunNow / a resumed park starts a run of THIS job …
        notifications.PendingAsk.SetResult(true);  // … and only then does the human answer "yes, run it"
        await tick;
        await SettleAsync(bg, ct);

        await launcher.DidNotReceive().LaunchAsync(Arg.Any<HeadlessRunRequest>(), Arg.Any<CancellationToken>());
        // The refusal spends the occurrence, exactly as the pre-grace door does — one implementation, two doors.
        // Leaving the row due would re-refuse every 30 s and drift back into this same prompt.
        Assert.Equal([late.Id], jobs.Dispatched);
        Assert.Empty(jobs.Failed);   // and a refusal is still not a job-health signal
    }

    /// <summary>
    /// CHARACTERIZATION OF A KNOWN OPEN DEFECT — this test asserts BROKEN behaviour on purpose, and is expected
    /// to red on the day it is fixed. Read the assertions as "this is what currently happens", never as a design
    /// this protects.
    /// <para>
    /// hermes #2 moved the RUN off the tick, but not the missed-run DIALOG. <c>ExecuteOnceAsync</c> iterates due
    /// jobs sequentially and <c>RunJobAsync</c> awaits <c>AskUserToRunMissedAsync</c> — a real ContentDialog that
    /// resolves only when a human clicks. <c>PeriodicTimer</c> does not queue elapsed ticks, so while the tick is
    /// parked on that dialog NO tick body runs at all: job B here, and every other due job on the device, and
    /// every subsequent occurrence of every job, waits for the human. The <c>_bookkeepingLock</c> doc claims
    /// "never held across a dialog" — true of the lock, false of the tick itself.
    /// </para>
    /// <para>
    /// NOT FIXED in the review pass, deliberately. Moving the ask off-tick (TrackDispatch after the dedup add,
    /// which must stay on the tick or <c>DedupesPromptOnSecondTickIfUnanswered</c> becomes a race) changes
    /// <c>ExecuteOnceAsync</c>'s documented contract — "returns once every due job has been DISPATCHED" — forces
    /// <c>SkipsIfDeclined</c> onto the drain, and parks an UNANSWERED dialog in <c>_dispatches</c> forever, which
    /// silently changes what <c>WaitForDispatchedRunsAsync</c> means at shutdown and in <see cref="SettleAsync"/>.
    /// That is an owner decision about the tick's shape, not a review fix. Pre-existing: the batch under review
    /// only ever claimed the RUN no longer holds the tick.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ExecuteOnceAsync_APendingMissedRunDialog_StillBlocksEveryOtherDueJob_KnownOpenDefect()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobs = new FakeJobService();
        var late = new ScheduledJob
        {
            Name = "late", Query = "q", Recurrence = RecurrenceType.Daily, Kind = ScheduledJobKind.AgentTask,
            TimeOfDay = TimeOnly.MinValue, NextFireAt = DateTime.Now.AddMinutes(-20)
        };
        var normal = NewDueJob();
        normal.Kind = ScheduledJobKind.AgentTask;
        jobs.SeedDue(late);      // first, so the tick reaches its dialog before it ever sees `normal`
        jobs.SeedDue(normal);

        var launcher = Substitute.For<IHeadlessRunLauncher>();
        launcher.LaunchAsync(Arg.Any<HeadlessRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(new HeadlessRunHandle(Guid.NewGuid(), Guid.NewGuid(), Task.CompletedTask));

        var notifications = new FakeNotificationSurface
        {
            // Never completed: the human has not answered.
            PendingAsk = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var bg = new ScheduledJobBackgroundService(
            jobs, new FakeScopeFactory(new FakeServiceProvider()), new FakeProviderResolver(NewProvider()),
            notifications, launcher, NewSettings(), Substitute.For<IAgentRunService>(),
            NullLogger<ScheduledJobBackgroundService>.Instance);

        await Assert.ThrowsAsync<TimeoutException>(
            () => bg.ExecuteOnceAsync(ct).WaitAsync(TimeSpan.FromSeconds(5), ct));
        // Neither job got out — not just the late one. `normal` is due, needs no dialog, and is still stuck.
        await launcher.DidNotReceive().LaunchAsync(Arg.Any<HeadlessRunRequest>(), Arg.Any<CancellationToken>());
        Assert.Empty(jobs.Dispatched);
        Assert.Equal(1, notifications.AskCount);   // the tick really is parked on the human, not on something else
    }

    [Fact]
    public async Task ExecuteOnceAsync_AgentTaskJob_FailedRun_StillCountsAsAFailure_AfterTheScheduleMovedOn()
    {
        // "The schedule moved on" and "the run failed" are now two writes at two times, and the second one must
        // not get lost with the head-of-line block: job HEALTH still has to track the real outcome, or the
        // 5-strike valve and the one-off retirement stop working. Neutralising the
        // `TrackDispatch(BookkeepAgentRunAsync(...))` hand-off reds this on jobs.Failed being empty.
        var ct = TestContext.Current.CancellationToken;
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
            .Returns(new AgentRun { Id = runId, RunShape = RunShape.Planned, State = AgentRunState.Failed });

        var notifications = new FakeNotificationSurface();
        var bg = new ScheduledJobBackgroundService(
            jobs, new FakeScopeFactory(new FakeServiceProvider()), new FakeProviderResolver(NewProvider()),
            notifications, launcher, NewSettings(), runService,
            NullLogger<ScheduledJobBackgroundService>.Instance);

        await TickAndSettleAsync(bg, ct);

        Assert.Equal([due.Id], jobs.Dispatched);   // the schedule moved before the outcome was known …
        Assert.Single(jobs.Failed);                // … and the outcome still reached the health columns
        Assert.Equal(due.Id, jobs.Failed[0].JobId);
        Assert.Equal("Failed", jobs.Failed[0].Reason);
        Assert.Equal(1, notifications.FailureCount);
        Assert.Empty(jobs.Advanced);               // a failure is not a park and not a skip
    }

    [Fact]
    public async Task ExecuteOnceAsync_TwoDueResearchJobs_DispatchesBothWithoutWaiting_AndRunsThemOneAtATime()
    {
        // The research leg converted too, and its bound is NOT the launcher's (that runner never touches the
        // launcher). Two facts in one, because they are two halves of the same posture: the tick does not wait
        // for a turn (restoring an inline await reds this as a TimeoutException), and the second turn QUEUES on
        // the leg's single permit rather than running concurrently — the serialization this leg always had, now
        // waited inside the dispatched work instead of in the tick.
        var ct = TestContext.Current.CancellationToken;
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var jobs = new FakeJobService();
        var first = NewDueJob();
        var second = NewDueJob();
        jobs.SeedDue(first);
        jobs.SeedDue(second);

        var runner = new FakeRunner { Result = new BackgroundTurnResult(Guid.NewGuid(), true, null), HoldFirstRun = hold };
        var launcher = Substitute.For<IHeadlessRunLauncher>();
        var bg = new ScheduledJobBackgroundService(
            jobs, new FakeScopeFactory(new FakeServiceProvider().Add<IBackgroundAssistantTurnRunner>(runner)),
            new FakeProviderResolver(NewProvider()), new FakeNotificationSurface(), launcher,
            NewSettings(), Substitute.For<IAgentRunService>(),
            NullLogger<ScheduledJobBackgroundService>.Instance);

        await bg.ExecuteOnceAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (runner.RunCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10, ct);

        Assert.Equal(1, runner.RunCount);         // turn 1 is in flight, turn 2 is queued behind the permit
        Assert.Equal(2, jobs.Dispatched.Count);   // yet BOTH occurrences were already spent at dispatch time
        Assert.Empty(jobs.Completed);

        hold.SetResult();
        await SettleAsync(bg, ct);

        Assert.Equal(2, runner.RunCount);
        Assert.Equal(2, jobs.Completed.Count);
    }

    [Fact]
    public async Task ExecuteOnceAsync_ResearchJob_ScheduleWriteThrows_SkipsThatOccurrenceOnly()
    {
        // The research leg answers a faulted schedule write by SKIPPING the occurrence, the opposite of the
        // agent leg — because its AgentRuns row is created inside the runner, so the TriggerRef guard is blind
        // for as long as the turn sits queued, and dispatching anyway would mean an unbounded re-dispatch loop
        // of real provider turns, one per 30 s tick. The second job proves the skip is scoped to the one
        // occurrence and not a broken tick.
        var ct = TestContext.Current.CancellationToken;
        var jobs = new FakeJobService();
        var skipped = NewDueJob();
        var dispatched = NewDueJob();
        skipped.Query = "skip-me";
        dispatched.Query = "run-me";
        jobs.ThrowOnDispatchAdvanceFor.Add(skipped.Id);
        jobs.SeedDue(skipped);
        jobs.SeedDue(dispatched);

        var runner = new FakeRunner { Result = new BackgroundTurnResult(Guid.NewGuid(), true, null) };
        var bg = new ScheduledJobBackgroundService(
            jobs, new FakeScopeFactory(new FakeServiceProvider().Add<IBackgroundAssistantTurnRunner>(runner)),
            new FakeProviderResolver(NewProvider()), new FakeNotificationSurface(),
            Substitute.For<IHeadlessRunLauncher>(), NewSettings(), Substitute.For<IAgentRunService>(),
            NullLogger<ScheduledJobBackgroundService>.Instance);

        await TickAndSettleAsync(bg, ct);

        Assert.Equal(1, runner.RunCount);
        Assert.Equal("run-me", runner.LastRequest!.Prompt);
        Assert.Equal([dispatched.Id], jobs.Dispatched);
        Assert.Empty(jobs.Failed);   // a skipped occurrence is not a job-health signal either
    }

    private sealed class FakeJobService : IScheduledJobService
    {
        private readonly List<ScheduledJob> _due = new();
        public List<(Guid JobId, Guid EntryId)> Completed { get; } = new();
        public List<(Guid JobId, string Reason)> Failed { get; } = new();
        public List<Guid> Advanced { get; } = new();

        /// <summary>
        /// Jobs whose schedule was moved on at DISPATCH time. Kept separate from <see cref="Advanced"/> even
        /// though the real service makes the identical write, because the two mean different things and a test
        /// that could not tell them apart could not show which layer stopped a duplicate dispatch.
        /// </summary>
        public List<Guid> Dispatched { get; } = new();

        /// <summary>When set, AdvanceMissedRunAsync faults (the bookkeeping degrade path).</summary>
        public bool ThrowOnAdvance { get; set; }

        /// <summary>
        /// Jobs whose MarkOccurrenceDispatchedAsync faults. Per-job rather than a global switch because the two
        /// legs answer a fault differently — the agent leg carries on and leans on the guard, the research leg
        /// skips the occurrence — so a test needs to fault ONE job's write while another's succeeds.
        /// </summary>
        public HashSet<Guid> ThrowOnDispatchAdvanceFor { get; } = new();

        public void SeedDue(ScheduledJob job) => _due.Add(job);

        // Models the real query: only jobs whose NextFireAt has passed come back, so a job whose schedule
        // was advanced is genuinely not due on the following tick.
        //
        // LIMIT, stated so nobody reads a false green off it: this models only the NextFireAt half of the real
        // predicate, never `AND Status = 'Active'`, and MarkOccurrenceDispatchedAsync below moves NextFireAt for
        // EVERY recurrence. A RecurrenceType.Once job leaves the due window in the real service by a Status flip
        // and its NextFireAt never moves, so a Once scenario cannot be measured here at all — that pair lives in
        // ScheduledJobServiceTests, against the real service and the real SQL.
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
            MoveOffOccurrence(id);
            return Task.CompletedTask;
        }

        public Task MarkOccurrenceDispatchedAsync(Guid id)
        {
            if (ThrowOnDispatchAdvanceFor.Contains(id)) throw new InvalidOperationException("dispatch advance boom");

            Dispatched.Add(id);
            MoveOffOccurrence(id);
            return Task.CompletedTask;
        }

        // Mirror the real service, which serves both names from ONE write: NextFireAt moves to the next
        // occurrence and NOTHING else — no failure counter, no Status, no LastFiredAt.
        private void MoveOffOccurrence(Guid id)
        {
            var job = _due.FirstOrDefault(j => j.Id == id);
            if (job is not null) job.NextFireAt = DateTime.Now.AddDays(1);
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
        private int _runCount;

        /// <summary>Volatile because the research leg now runs the turn on a dispatched task, not on the tick.</summary>
        public int RunCount => Volatile.Read(ref _runCount);

        public BackgroundTurnResult Result { get; set; } = new(Guid.NewGuid(), true, null);
        public string? ThrowMessage { get; set; }
        public BackgroundTurnRequest? LastRequest { get; private set; }

        /// <summary>When set, the FIRST turn blocks until it is completed — a long-running research job.</summary>
        public TaskCompletionSource? HoldFirstRun { get; set; }

        public Task<BackgroundTurnResult> RunAsync(BackgroundTurnRequest request, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref _runCount);
            LastRequest = request;
            if (ThrowMessage is not null)
                throw new InvalidOperationException(ThrowMessage);
            if (n == 1 && HoldFirstRun is { } hold)
                return AfterAsync(hold);
            return Task.FromResult(Result);
        }

        private async Task<BackgroundTurnResult> AfterAsync(TaskCompletionSource hold)
        {
            await hold.Task;
            return Result;
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
