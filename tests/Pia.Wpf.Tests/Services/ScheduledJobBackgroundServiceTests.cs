using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

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

    /// <summary>An unstubbed <c>ISettingsService</c> hands back null settings that production could never
    /// produce, and the AgentTask path NREs building the run budget from them.</summary>
    private static ISettingsService NewSettings()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());
        return settings;
    }

    /// <summary>A tick returns once every due job has been DISPATCHED, and the run's outcome is written by a
    /// continuation afterwards — so any test asserting one of those must come through here.</summary>
    private static Task SettleAsync(ScheduledJobBackgroundService bg, CancellationToken ct) =>
        bg.WaitForDispatchedRunsAsync().WaitAsync(TimeSpan.FromSeconds(30), ct);

    private static async Task TickAndSettleAsync(ScheduledJobBackgroundService bg, CancellationToken ct)
    {
        await bg.ExecuteOnceAsync(ct);
        await SettleAsync(bg, ct);
    }

    /// <summary>Reports whether the condition held within the bound. Meant to be used in BOTH directions with
    /// the SAME bound: a negative is only evidence if the identical probe then observes the event.</summary>
    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan within, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + within;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(10, ct);
        }
        return true;
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
        // Only the owner device advances a job, or two machines double-fire it — and a manual button must not
        // be able to do what this device's own scheduler is forbidden to do.
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
        // ScheduledJobService keys its ONE retryable failure off this exact reason value, so BOTH dispatch legs
        // must hand it the same constant; a literal typo'd apart in the AgentTask leg would be invisible.
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

        // Settled, not just ticked: the ask and everything it decides happen on a dispatched task, so a bare
        // ExecuteOnceAsync would be a race on jobs.Advanced.
        await TickAndSettleAsync(bg, CancellationToken.None);

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

    /// <summary>
    /// The discriminating half of the dismissal fix. Skip (false) spends the occurrence; leaving the dialog
    /// unanswered (null) must NOT — while both mapped to false, pressing Escape advanced the schedule and
    /// logged a skip the user never chose.
    /// </summary>
    [Fact]
    public async Task ExecuteOnceAsync_LateBy20Min_AnUnansweredPromptLeavesTheScheduleAlone()
    {
        var jobs = new FakeJobService();
        var late = new ScheduledJob
        {
            Name = "T", Query = "q", Recurrence = RecurrenceType.Daily,
            TimeOfDay = TimeOnly.MinValue, NextFireAt = DateTime.Now.AddMinutes(-20)
        };
        jobs.SeedDue(late);

        var notifications = new FakeNotificationSurface { AskAnswer = null };
        var runner = new FakeRunner();
        var providers = new FakeProviderResolver(NewProvider());

        var sp = new FakeServiceProvider().Add<IBackgroundAssistantTurnRunner>(runner);
        var bg = new ScheduledJobBackgroundService(jobs, new FakeScopeFactory(sp), providers, notifications, Substitute.For<IHeadlessRunLauncher>(), Substitute.For<ISettingsService>(), Substitute.For<IAgentRunService>(), NullLogger<ScheduledJobBackgroundService>.Instance);

        await TickAndSettleAsync(bg, CancellationToken.None);

        Assert.Equal(1, notifications.AskCount);
        Assert.Equal(0, runner.RunCount);
        // The positive control is ExecuteOnceAsync_LateBy20Min_AsksUserAndSkipsIfDeclined, where the same
        // fixture with AskAnswer=false DOES record an advance.
        Assert.Empty(jobs.Advanced);
        Assert.Empty(jobs.Failed);
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

        var ct = TestContext.Current.CancellationToken;
        var probe = TimeSpan.FromSeconds(1);
        await bg.ExecuteOnceAsync(ct);
        // The ask is on a dispatched task, so tick 1 can return before it has been raised at all. Waiting for
        // it keeps this a DEDUP test: "no second ask" is only evidence once a first one exists.
        Assert.True(await WaitUntilAsync(() => notifications.AskCount == 1, probe, ct));

        await bg.ExecuteOnceAsync(ct);

        // The same probe, the same bound, now looking for the ask that must NOT come. Its positive control is the
        // line above — the identical probe demonstrably observes an ask when there is one to observe.
        Assert.False(await WaitUntilAsync(() => notifications.AskCount >= 2, probe, ct)); // not 2
    }

    [Fact]
    public async Task ExecuteOnceAsync_AgentTaskJob_DispatchesToLauncherWithScheduleProvenanceAndScheduledBudget()
    {
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
        // A parked run is NOT a job failure, but the schedule must still have moved on — and exactly once: the
        // write happens at DISPATCH, and the park arm is log-only, which Assert.Empty(jobs.Advanced) pins.
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

        // The premise first: the park arm is log-only, so the four absences below are only evidence if the
        // continuation containing that arm actually ran, and GetAsync is its first act.
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

        // The premise: without this line, "the log-only park arm ran and correctly wrote nothing" is
        // indistinguishable from "the bookkeeping continuation never ran at all".
        await runService.Received(1).GetAsync(runId, Arg.Any<CancellationToken>());

        Assert.Empty(jobs.Failed);
        Assert.Equal([due.Id], jobs.Dispatched);
        Assert.Empty(jobs.Advanced);
    }

    [Fact]
    public async Task ExecuteOnceAsync_AgentJob_ScheduleWriteThrows_DoesNotBreakTheTick()
    {
        // Moving the schedule on is bookkeeping: if it faults the tick must keep going, never strand the
        // remaining due jobs. The fault is per-job because the research leg answers it by skipping instead.
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
        // Job 1's Completion is a gate this test never opens, so "job 2 launched AND the tick returned" is only
        // possible if nothing waits for job 1. The tick is awaited with a bound so a regression fails, not hangs.
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
        // The duplicate-dispatch defence alone: a run outlasting the 30 s interval leaves its job still due, and
        // an unstubbed IAgentRunService reports no executing run, so only the schedule write explains one launch.
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
        // The case the schedule write cannot cover, because here it FAULTED: the job is still due on the second
        // tick with its run in flight, so the TriggerRef guard is the only thing left to refuse.
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
        // A manual fire never consults the due query, so the schedule write cannot protect it — and a refused
        // manual fire must leave the schedule ALONE, not spend the occurrence the tick is still going to fire.
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

    /// <summary>The duplicate-dispatch guard is re-tested after the missed-run dialog, because while that
    /// unbounded human wait is open a manual fire or a resumed park can start a run of the same goal.</summary>
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

    /// <summary>The tick RETURNS while a missed-run dialog is outstanding: <c>PeriodicTimer</c> does not queue
    /// elapsed ticks, so a tick parked on a human click stopped every other due job on the device.</summary>
    [Fact]
    public async Task ExecuteOnceAsync_APendingMissedRunDialog_NoLongerBlocksTheOtherDueJobs()
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
            // Never completed while the tick runs: the human has not answered.
            PendingAsk = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var bg = new ScheduledJobBackgroundService(
            jobs, new FakeScopeFactory(new FakeServiceProvider()), new FakeProviderResolver(NewProvider()),
            notifications, launcher, NewSettings(), Substitute.For<IAgentRunService>(),
            NullLogger<ScheduledJobBackgroundService>.Instance);

        await bg.ExecuteOnceAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);

        Assert.Equal(1, notifications.AskCount);                    // the dialog was raised …
        Assert.False(notifications.PendingAsk.Task.IsCompleted);    // … and is STILL outstanding, unanswered
        Assert.Equal([normal.Id], jobs.Dispatched);                 // yet job B's occurrence was already spent
        await launcher.Received(1).LaunchAsync(
            Arg.Is<HeadlessRunRequest>(r => r.TriggerRef == normal.Id), Arg.Any<CancellationToken>());
        Assert.Empty(jobs.Advanced);   // and nothing was decided for `late`: that is what the dialog gates

        // Answer "skip" so the tracked ask completes and the drain can finish (an ignored dialog stays in
        // _dispatches until the token is cancelled — see the shutdown test below).
        notifications.PendingAsk.SetResult(false);
        await SettleAsync(bg, ct);
        Assert.Equal([late.Id], jobs.Advanced);
    }

    /// <summary><c>ContentDialogHost</c> shows ONE dialog — a second concurrent <c>ShowAsync</c> throws and that
    /// job is then never re-asked, its occurrence silently lost — so two late jobs' prompts must QUEUE.</summary>
    [Fact]
    public async Task ExecuteOnceAsync_TwoLateJobs_NeverOpensTwoDialogsAtOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobs = new FakeJobService();
        // Seeded oldest-first, the order the real GetDueJobsAsync returns (ORDER BY NextFireAt ASC).
        var first = new ScheduledJob
        {
            Name = "late-1", Query = "q", Recurrence = RecurrenceType.Daily, Kind = ScheduledJobKind.AgentTask,
            TimeOfDay = TimeOnly.MinValue, NextFireAt = DateTime.Now.AddMinutes(-25)
        };
        var second = new ScheduledJob
        {
            Name = "late-2", Query = "q", Recurrence = RecurrenceType.Daily, Kind = ScheduledJobKind.AgentTask,
            TimeOfDay = TimeOnly.MinValue, NextFireAt = DateTime.Now.AddMinutes(-20)
        };
        jobs.SeedDue(first);
        jobs.SeedDue(second);

        var launcher = Substitute.For<IHeadlessRunLauncher>();
        var notifications = new FakeNotificationSurface
        {
            PendingAsk = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var bg = new ScheduledJobBackgroundService(
            jobs, new FakeScopeFactory(new FakeServiceProvider()), new FakeProviderResolver(NewProvider()),
            notifications, launcher, NewSettings(), Substitute.For<IAgentRunService>(),
            NullLogger<ScheduledJobBackgroundService>.Instance);

        await bg.ExecuteOnceAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);

        var probe = TimeSpan.FromSeconds(1);
        Assert.True(await WaitUntilAsync(() => notifications.AskCount >= 1, probe, ct));   // premise: one is open
        Assert.False(await WaitUntilAsync(() => notifications.AskCount >= 2, probe, ct));  // the other is queued
        Assert.Equal([first.Id], notifications.AskedJobIds);   // and it is the FIRST due job that got the dialog

        // Answering the open dialog "skip" releases the permit; the queued job's own ask then reads the same
        // already-completed answer, so both are asked and both skip — one at a time.
        notifications.PendingAsk.SetResult(false);
        Assert.True(await WaitUntilAsync(() => notifications.AskCount >= 2, probe, ct));   // the control
        await SettleAsync(bg, ct);

        Assert.Equal(1, notifications.PeakConcurrentAsks);   // the mechanism: never two dialogs at once
        Assert.Equal(2, notifications.AskCount);
        Assert.Equal([first.Id, second.Id], notifications.AskedJobIds);
        Assert.Equal(2, jobs.Advanced.Count);                // and neither late job was silently stranded
        await launcher.DidNotReceive().LaunchAsync(Arg.Any<HeadlessRunRequest>(), Arg.Any<CancellationToken>());
    }

    /// <summary>The dialog no longer gates the DEVICE, but it must still gate ITS OWN job: "run it anyway because
    /// we stopped waiting" would be an unattended run the user never authorised.</summary>
    [Fact]
    public async Task MissedRunDialog_StillGatesItsOwnJob()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobs = new FakeJobService();
        var late = new ScheduledJob
        {
            Name = "late", Query = "q", Recurrence = RecurrenceType.Daily, Kind = ScheduledJobKind.AgentTask,
            TimeOfDay = TimeOnly.MinValue, NextFireAt = DateTime.Now.AddMinutes(-20)
        };
        jobs.SeedDue(late);

        var launcher = Substitute.For<IHeadlessRunLauncher>();
        launcher.LaunchAsync(Arg.Any<HeadlessRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(new HeadlessRunHandle(Guid.NewGuid(), Guid.NewGuid(), Task.CompletedTask));

        var notifications = new FakeNotificationSurface
        {
            PendingAsk = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var bg = new ScheduledJobBackgroundService(
            jobs, new FakeScopeFactory(new FakeServiceProvider()), new FakeProviderResolver(NewProvider()),
            notifications, launcher, NewSettings(), Substitute.For<IAgentRunService>(),
            NullLogger<ScheduledJobBackgroundService>.Instance);

        await bg.ExecuteOnceAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Equal(1, notifications.AskCount);

        // The window the whole item opens: the tick is over, the dialog is not. Probed rather than sampled, so a
        // dispatch that merely had not been scheduled yet cannot pass for a dispatch that was refused.
        Assert.False(await WaitUntilAsync(
            () => jobs.Dispatched.Count > 0 || jobs.Advanced.Count > 0, TimeSpan.FromSeconds(1), ct));
        await launcher.DidNotReceive().LaunchAsync(Arg.Any<HeadlessRunRequest>(), Arg.Any<CancellationToken>());
        Assert.Empty(jobs.Failed);
        Assert.Empty(jobs.Completed);

        notifications.PendingAsk.SetResult(true);
        await SettleAsync(bg, ct);

        await launcher.Received(1).LaunchAsync(
            Arg.Is<HeadlessRunRequest>(r => r.TriggerRef == late.Id), Arg.Any<CancellationToken>());
        Assert.Equal([late.Id], jobs.Dispatched);
    }

    /// <summary>The dedup entry must survive until the OCCURRENCE IS SPENT, not until the answer arrives: on
    /// "yes" the gap between the two spans the whole launch, which a 30 s tick lands in easily.</summary>
    [Fact]
    public async Task ATickWhileAnAnsweredMissedRunIsStillLaunching_DoesNotAskAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobs = new FakeJobService();
        var late = new ScheduledJob
        {
            Name = "late", Query = "q", Recurrence = RecurrenceType.Daily, Kind = ScheduledJobKind.AgentTask,
            TimeOfDay = TimeOnly.MinValue, NextFireAt = DateTime.Now.AddMinutes(-20)
        };
        jobs.SeedDue(late);

        var runId = Guid.NewGuid();
        var launchEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launchGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launcher = Substitute.For<IHeadlessRunLauncher>();
        launcher.LaunchAsync(Arg.Any<HeadlessRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => HeldLaunchAsync());

        async Task<HeadlessRunHandle> HeldLaunchAsync()
        {
            launchEntered.TrySetResult();
            await launchGate.Task;
            return new HeadlessRunHandle(runId, Guid.NewGuid(), Task.CompletedTask);
        }

        // Stubbed Completed so the drain's bookkeeping takes the success arm: an unstubbed GetAsync returns null,
        // which the continuation reads as a failed run and books a strike this test is not about.
        var runs = Substitute.For<IAgentRunService>();
        runs.GetAsync(runId, Arg.Any<CancellationToken>())
            .Returns(new AgentRun { Id = runId, TriggerRef = late.Id, State = AgentRunState.Completed });

        var notifications = new FakeNotificationSurface
        {
            PendingAsk = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var bg = new ScheduledJobBackgroundService(
            jobs, new FakeScopeFactory(new FakeServiceProvider()), new FakeProviderResolver(NewProvider()),
            notifications, launcher, NewSettings(), runs,
            NullLogger<ScheduledJobBackgroundService>.Instance);

        await bg.ExecuteOnceAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.True(await WaitUntilAsync(() => notifications.AskCount == 1, TimeSpan.FromSeconds(5), ct));

        notifications.PendingAsk.SetResult(true);                          // the human says "run it" …
        await launchEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);   // … and the launch is now in flight

        // The window: a tick, with the answered job's occurrence still unspent.
        await bg.ExecuteOnceAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.False(await WaitUntilAsync(() => notifications.AskCount >= 2, TimeSpan.FromSeconds(1), ct));

        launchGate.SetResult();
        await SettleAsync(bg, ct);

        // The mechanism, recorded rather than sampled: one dialog for this job across both ticks, and one run.
        Assert.Equal([late.Id], notifications.AskedJobIds);
        await launcher.Received(1).LaunchAsync(
            Arg.Is<HeadlessRunRequest>(r => r.TriggerRef == late.Id), Arg.Any<CancellationToken>());
        Assert.Equal([late.Id], jobs.Dispatched);
    }

    /// <summary>An unanswered ask is a <c>TaskCompletionSource</c> only a click completes, so the wait takes the
    /// tick's token — otherwise a tracked dispatch would outlive the app and any drain would hang.</summary>
    [Fact]
    public async Task MissedRunAsk_ThatIsNeverAnswered_IsAbandonedAtShutdown_SoTheDrainCompletes()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobs = new FakeJobService();
        var late = new ScheduledJob
        {
            Name = "late", Query = "q", Recurrence = RecurrenceType.Daily, Kind = ScheduledJobKind.AgentTask,
            TimeOfDay = TimeOnly.MinValue, NextFireAt = DateTime.Now.AddMinutes(-20)
        };
        jobs.SeedDue(late);

        var launcher = Substitute.For<IHeadlessRunLauncher>();
        var notifications = new FakeNotificationSurface
        {
            PendingAsk = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var bg = new ScheduledJobBackgroundService(
            jobs, new FakeScopeFactory(new FakeServiceProvider()), new FakeProviderResolver(NewProvider()),
            notifications, launcher, NewSettings(), Substitute.For<IAgentRunService>(),
            NullLogger<ScheduledJobBackgroundService>.Instance);

        // Stands in for the stopping token ExecuteAsync hands the tick.
        using var stopping = new CancellationTokenSource();
        await bg.ExecuteOnceAsync(stopping.Token).WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Equal(1, notifications.AskCount);

        stopping.Cancel();                 // = base.StopAsync cancelling the stopping token
        await SettleAsync(bg, ct);         // completes: the abandoned ask is no longer pending, and did not fault

        Assert.False(notifications.PendingAsk.Task.IsCompleted);   // the human still never answered
        Assert.Empty(jobs.Dispatched);
        Assert.Empty(jobs.Advanced);
        Assert.Empty(jobs.Failed);
        await launcher.DidNotReceive().LaunchAsync(Arg.Any<HeadlessRunRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteOnceAsync_AgentTaskJob_FailedRun_StillCountsAsAFailure_AfterTheScheduleMovedOn()
    {
        // "The schedule moved on" and "the run failed" are two writes at two times, and job HEALTH still has to
        // track the real outcome, or the 5-strike valve and the one-off retirement stop working.
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
        // Two halves of one posture: the tick does not wait for a turn, and the second turn QUEUES on the
        // leg's single permit. The recorded peak is the evidence a RunCount sample cannot give.
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

        var probe = TimeSpan.FromSeconds(1);
        Assert.True(await WaitUntilAsync(() => runner.RunCount >= 1, probe, ct));    // premise: turn 1 is in flight
        Assert.False(await WaitUntilAsync(() => runner.RunCount >= 2, probe, ct));   // turn 2 is behind the permit
        Assert.Equal(2, jobs.Dispatched.Count);   // yet BOTH occurrences were already spent at dispatch time
        Assert.Empty(jobs.Completed);

        hold.SetResult();
        // The control for the negative above: the SAME probe, the same bound, now does observe the entry.
        Assert.True(await WaitUntilAsync(() => runner.RunCount >= 2, probe, ct));
        await SettleAsync(bg, ct);

        Assert.Equal(1, runner.PeakConcurrent);   // the mechanism: the two turns never overlapped, ever
        Assert.Equal(2, runner.RunCount);
        Assert.Equal(2, jobs.Completed.Count);
    }

    [Fact]
    public async Task ExecuteOnceAsync_ResearchJob_ScheduleWriteThrows_SkipsThatOccurrenceOnly()
    {
        // The research leg answers a faulted schedule write by SKIPPING the occurrence: its AgentRuns row is
        // created inside the runner, so the TriggerRef guard is blind while the turn sits queued.
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

    /// <summary>A parked scheduled run that a human resumes books its outcome through a health-only write —
    /// advancing the schedule again would skip an occurrence of a recurring job.</summary>
    [Fact]
    public async Task AResumedScheduledRun_ThatCompletes_BooksTheJobOutcome_WithoutAdvancingTheSchedule()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobs = new FakeJobService();
        var job = NewDueJob();
        job.Kind = ScheduledJobKind.AgentTask;
        jobs.SeedDue(job);

        var runId = Guid.NewGuid();
        var chatId = Guid.NewGuid();
        var runs = Substitute.For<IAgentRunService>();
        runs.GetAsync(runId, Arg.Any<CancellationToken>()).Returns(new AgentRun
        {
            Id = runId, ChatId = chatId, TriggerRef = job.Id, State = AgentRunState.Completed,
        });

        var launcher = Substitute.For<IHeadlessRunLauncher>();
        var notifications = new FakeNotificationSurface();
        var bg = new ScheduledJobBackgroundService(
            jobs, new FakeScopeFactory(new FakeServiceProvider()), new FakeProviderResolver(NewProvider()),
            notifications, launcher, NewSettings(), runs, NullLogger<ScheduledJobBackgroundService>.Instance);

        // No tick at all: the resume is a dispatch of its own, and the service must book it without any
        // occurrence ever coming due.
        launcher.ResumedRunSettled += Raise.EventWith(new ResumedRunSettledEventArgs(runId, chatId));
        await SettleAsync(bg, ct);

        var booking = Assert.Single(jobs.Bookings);
        Assert.Equal(job.Id, booking.JobId);
        Assert.Equal(chatId, booking.EntryId);
        Assert.True(booking.Succeeded);
        Assert.Equal(1, notifications.SuccessCount);
        Assert.Equal(chatId, notifications.LastSuccessChatId);

        // The mechanism: booked through the health-only write, and through NOTHING that moves the schedule.
        Assert.Empty(jobs.Completed);    // MarkRunCompleteAsync recomputes NextFireAt — must not be used here
        Assert.Empty(jobs.Dispatched);
        Assert.Empty(jobs.Advanced);
        Assert.Empty(jobs.Failed);
    }

    /// <summary>The raiser cannot tell a real settle from a null <c>TriggerRef</c>, a re-parked row, or one a
    /// newer dispatch is already executing — so it fires unconditionally and the handler decides.</summary>
    [Fact]
    public async Task AResumedRunThatIsNotASettledFiring_BooksNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobs = new FakeJobService();
        var job = NewDueJob();
        job.Kind = ScheduledJobKind.AgentTask;
        jobs.SeedDue(job);

        var detached = Guid.NewGuid();
        var reparked = Guid.NewGuid();
        var executing = Guid.NewGuid();
        var settled = Guid.NewGuid();
        var chatId = Guid.NewGuid();
        var runs = Substitute.For<IAgentRunService>();
        runs.GetAsync(detached, Arg.Any<CancellationToken>()).Returns(new AgentRun
        {
            Id = detached, ChatId = chatId, TriggerRef = null, State = AgentRunState.Completed,
        });
        runs.GetAsync(reparked, Arg.Any<CancellationToken>()).Returns(new AgentRun
        {
            Id = reparked, ChatId = chatId, TriggerRef = job.Id, State = AgentRunState.WaitingForInput,
        });
        // Running is the plainest state that satisfies AgentRunStates.IsExecuting: not parked, not terminal.
        runs.GetAsync(executing, Arg.Any<CancellationToken>()).Returns(new AgentRun
        {
            Id = executing, ChatId = chatId, TriggerRef = job.Id, State = AgentRunState.Running,
        });
        runs.GetAsync(settled, Arg.Any<CancellationToken>()).Returns(new AgentRun
        {
            Id = settled, ChatId = chatId, TriggerRef = job.Id, State = AgentRunState.Failed,
        });

        var launcher = Substitute.For<IHeadlessRunLauncher>();
        var notifications = new FakeNotificationSurface();
        var bg = new ScheduledJobBackgroundService(
            jobs, new FakeScopeFactory(new FakeServiceProvider()), new FakeProviderResolver(NewProvider()),
            notifications, launcher, NewSettings(), runs, NullLogger<ScheduledJobBackgroundService>.Instance);

        launcher.ResumedRunSettled += Raise.EventWith(new ResumedRunSettledEventArgs(detached, chatId));
        launcher.ResumedRunSettled += Raise.EventWith(new ResumedRunSettledEventArgs(reparked, chatId));
        launcher.ResumedRunSettled += Raise.EventWith(new ResumedRunSettledEventArgs(executing, chatId));
        await SettleAsync(bg, ct);

        Assert.Empty(jobs.Bookings);
        Assert.Equal(0, notifications.SuccessCount);
        Assert.Equal(0, notifications.FailureCount);

        // The control: the SAME service, the same fake, a run that really did settle — so the three negatives
        // above are about the decision and not about a callback that never fires.
        launcher.ResumedRunSettled += Raise.EventWith(new ResumedRunSettledEventArgs(settled, chatId));
        await SettleAsync(bg, ct);

        var booking = Assert.Single(jobs.Bookings);
        Assert.Equal(job.Id, booking.JobId);
        Assert.False(booking.Succeeded);
        Assert.Equal(1, notifications.FailureCount);
        Assert.Empty(jobs.Failed);   // and still not through MarkRunFailedAsync, which would retire the job
    }

    private sealed class FakeJobService : IScheduledJobService
    {
        private readonly List<ScheduledJob> _due = new();
        public List<(Guid JobId, Guid EntryId)> Completed { get; } = new();
        public List<(Guid JobId, string Reason)> Failed { get; } = new();
        public List<Guid> Advanced { get; } = new();

        /// <summary>Jobs whose schedule was moved on at DISPATCH time — kept separate from
        /// <see cref="Advanced"/> so a test can show WHICH write stopped a duplicate dispatch.</summary>
        public List<Guid> Dispatched { get; } = new();

        /// <summary>When set, AdvanceMissedRunAsync faults (the bookkeeping degrade path).</summary>
        public bool ThrowOnAdvance { get; set; }

        /// <summary>Jobs whose <c>MarkOccurrenceDispatchedAsync</c> faults; per-job because a test needs to
        /// fault one job's write while another's succeeds.</summary>
        public HashSet<Guid> ThrowOnDispatchAdvanceFor { get; } = new();

        public void SeedDue(ScheduledJob job) => _due.Add(job);

        // Models only the NextFireAt half of the real predicate, never `AND Status = 'Active'`, so a
        // RecurrenceType.Once job — which leaves the due window by a Status flip — cannot be measured here.
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

        /// <summary>Health-only bookings, kept apart from <see cref="Completed"/>/<see cref="Failed"/> because
        /// this write must NOT touch the schedule the way those two do.</summary>
        public List<(Guid JobId, DateTime FiredAt, Guid? EntryId, bool Succeeded)> Bookings { get; } = new();

        public Task MarkFiringOutcomeAsync(Guid id, DateTime firedAt, Guid? resultEntryId, bool succeeded)
        {
            Bookings.Add((id, firedAt, resultEntryId, succeeded));
            // Deliberately does NOT touch NextFireAt: the real write touches neither the schedule nor Status.
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
            ScheduledJobKind kind = ScheduledJobKind.Research, bool quietOnSuccess = false) => throw new NotImplementedException();

        public Task<IReadOnlyList<ScheduledJob>> GetAllAsync() => throw new NotImplementedException();
        public Task<IReadOnlyList<ScheduledJob>> GetActiveAsync() => throw new NotImplementedException();
        // Run-now looks the job up by id rather than taking it off the due list, so this resolves against the
        // same backing collection — including rows that are NOT due.
        public Task<ScheduledJob?> GetAsync(Guid id) =>
            Task.FromResult(_due.FirstOrDefault(j => j.Id == id));

        public Task UpdateAsync(Guid id, string? name = null, string? query = null,
            RecurrenceType? recurrence = null, TimeOnly? timeOfDay = null, DayOfWeek? dayOfWeek = null,
            int? dayOfMonth = null, int? month = null, Guid? providerId = null,
            IReadOnlyCollection<string>? grantedTools = null,
            DateTime? specificDate = null, ScheduledJobKind? kind = null, bool? quietOnSuccess = null) => throw new NotImplementedException();

        /// <summary>Drives the run-now owner refusal. True by default, which is the ordinary case (a job this
        /// device owns, or a legacy row with a null owner).</summary>
        public bool OwnedByThisDevice { get; set; } = true;

        public Task<bool> IsOwnedByThisDeviceAsync(Guid id) => Task.FromResult(OwnedByThisDevice);

        public Task DeleteAsync(Guid id) => throw new NotImplementedException();
        public Task DisableAsync(Guid id) => throw new NotImplementedException();
        public Task EnableAsync(Guid id) => throw new NotImplementedException();
        public Task<IReadOnlyList<ScheduledJob>> GetModifiedSinceAsync(DateTime since) => throw new NotImplementedException();
        public Task<int> BackfillRecurrenceDaysAsync() => throw new NotImplementedException();
        public Task UpsertFromSyncAsync(ScheduledJob job) => throw new NotImplementedException();
    }

    /// <summary>The most callers ever inside an <see cref="Enter"/>/<see cref="Exit"/> pair at once: an overlap
    /// stays in the peak, whereas a count sample misses one whose second entrant is only queued.</summary>
    private sealed class PeakConcurrencyTracker
    {
        private int _current;
        private int _peak;

        public int Peak => Volatile.Read(ref _peak);

        public void Enter()
        {
            var live = Interlocked.Increment(ref _current);
            var peak = Volatile.Read(ref _peak);
            while (live > peak)
            {
                var seen = Interlocked.CompareExchange(ref _peak, live, peak);
                if (seen == peak) break;
                peak = seen;
            }
        }

        public void Exit() => Interlocked.Decrement(ref _current);
    }

    private sealed class FakeRunner : IBackgroundAssistantTurnRunner
    {
        private int _runCount;
        private readonly PeakConcurrencyTracker _tracker = new();

        /// <summary>Volatile because the research leg now runs the turn on a dispatched task, not on the tick.</summary>
        public int RunCount => Volatile.Read(ref _runCount);

        /// <summary>The most turns ever inside <see cref="RunAsync"/> at once — the recorded fact that pins
        /// <c>ScheduledJobBackgroundService._researchSlots</c>.</summary>
        public int PeakConcurrent => _tracker.Peak;

        public BackgroundTurnResult Result { get; set; } = new(Guid.NewGuid(), true, null);
        public string? ThrowMessage { get; set; }
        public BackgroundTurnRequest? LastRequest { get; private set; }

        /// <summary>When set, the FIRST turn blocks until it is completed — a long-running research job.</summary>
        public TaskCompletionSource? HoldFirstRun { get; set; }

        public Task<BackgroundTurnResult> RunAsync(BackgroundTurnRequest request, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref _runCount);
            // Entry is recorded HERE, before the hold and the throw, and ThrowMessage must keep throwing
            // SYNCHRONOUSLY — hence returning rather than awaiting below.
            _tracker.Enter();
            LastRequest = request;
            if (ThrowMessage is not null)
            {
                _tracker.Exit();
                throw new InvalidOperationException(ThrowMessage);
            }
            if (n == 1 && HoldFirstRun is { } hold)
                return AfterAsync(hold);   // exits when the hold releases, not now
            _tracker.Exit();
            return Task.FromResult(Result);
        }

        private async Task<BackgroundTurnResult> AfterAsync(TaskCompletionSource hold)
        {
            try
            {
                await hold.Task;
                return Result;
            }
            finally
            {
                _tracker.Exit();
            }
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
        private int _askCount;
        private readonly PeakConcurrencyTracker _tracker = new();

        public int SuccessCount { get; private set; }
        public int FailureCount { get; private set; }
        public Guid? LastSuccessChatId { get; private set; }
        public bool? AskAnswer { get; set; } = false;

        /// <summary>Interlocked: the ask runs on a dispatched task, and two late jobs are two of them.</summary>
        public int AskCount => Volatile.Read(ref _askCount);

        /// <summary>The most asks ever outstanding at once — the recorded fact that pins
        /// <c>ScheduledJobBackgroundService._missedPromptGate</c>.</summary>
        public int PeakConcurrentAsks => _tracker.Peak;

        /// <summary>Which jobs were asked about, in order, so a test can tell WHOSE dialog is open.</summary>
        public List<Guid> AskedJobIds { get; } = new();

        public TaskCompletionSource<bool?>? PendingAsk { get; set; }

        public void NotifySuccess(ScheduledJob job, Guid chatId, string chatTitle)
        {
            SuccessCount++;
            LastSuccessChatId = chatId;
        }

        public void NotifyFailure(ScheduledJob job, string reason) => FailureCount++;

        public Task<bool?> AskUserToRunMissedAsync(ScheduledJob job, DateTime scheduledFireAt)
        {
            Interlocked.Increment(ref _askCount);
            _tracker.Enter();
            lock (AskedJobIds) AskedJobIds.Add(job.Id);
            if (PendingAsk is not null) return AfterAsync(PendingAsk.Task);
            _tracker.Exit();
            return Task.FromResult(AskAnswer);
        }

        /// <summary>An ask is outstanding until its task resolves — that is what "a dialog is open" means.</summary>
        private async Task<bool?> AfterAsync(Task<bool?> pending)
        {
            try
            {
                return await pending;
            }
            finally
            {
                _tracker.Exit();
            }
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
