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

    /// <summary>
    /// Polls <paramref name="condition"/> until it holds or <paramref name="within"/> elapses, and reports
    /// WHICH happened rather than asserting.
    /// <para>
    /// Meant to be used in BOTH directions in one test, with the SAME bound: "it did not happen within 1 s" is
    /// only evidence if the identical probe is then shown to observe the very same event once it is unblocked.
    /// A negative from a probe that was never proven capable of a positive is indistinguishable from a probe
    /// that is simply too weak, which is how this suite has previously shipped assertions that watched nothing.
    /// </para>
    /// </summary>
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

        // Settled, not just ticked: since T0-2 the ask and everything it decides — including this skip write —
        // happen on a dispatched task, so a bare ExecuteOnceAsync would be a race on jobs.Advanced.
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
        // Since T0-2 the ask is on a dispatched task, so tick 1 can return before it has been raised at all.
        // Waiting for it keeps this a DEDUP test: "no second ask" is only evidence once a first one exists.
        Assert.True(await WaitUntilAsync(() => notifications.AskCount == 1, probe, ct));

        await bg.ExecuteOnceAsync(ct);

        // The same probe, the same bound, now looking for the ask that must NOT come. Its positive control is the
        // line above — the identical probe demonstrably observes an ask when there is one to observe.
        Assert.False(await WaitUntilAsync(() => notifications.AskCount >= 2, probe, ct)); // not 2
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
    /// T0-2, and the inversion of a characterization test that used to assert the defect. hermes #2 moved the RUN
    /// off the tick but not the missed-run DIALOG: <c>ExecuteOnceAsync</c> iterates due jobs sequentially and
    /// <c>RunJobAsync</c> awaited <c>AskUserToRunMissedAsync</c> — a real ContentDialog that resolves only when a
    /// human clicks. <c>PeriodicTimer</c> does not queue elapsed ticks, so while the tick was parked on that
    /// dialog NO tick body ran at all: job B here, every other due job on the device, and every later occurrence
    /// of every job, waited for that click.
    /// <para>
    /// The contract asserted now: the tick RETURNS while the dialog is still outstanding, and the job that needs
    /// no dialog is dispatched and launched during that window. Both halves are STATE facts read after the tick
    /// has been awaited — "the tick finished" and "the ask has not resolved" — rather than a duration or an
    /// <c>IsCompleted</c> peek that would depend on the ask path happening to complete synchronously in these
    /// fakes. On the pre-change tree the first await times out, because the tick cannot finish at all.
    /// </para>
    /// <para>Neutralize: await <c>AskThenRunMissedAsync</c> in <c>RunJobAsync</c> instead of
    /// <c>TrackDispatch</c>-ing it → the 10 s wait on the tick times out.</para>
    /// </summary>
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

    /// <summary>
    /// The gate's mechanism (T0-2). Two late jobs in one tick are now two dispatched asks, and
    /// <c>ContentDialogHost</c> shows ONE dialog: a second concurrent <c>ShowAsync</c> throws, the surface's catch
    /// reports that as "no answer", and the job then sits in <c>_pendingMissedPrompts</c> for the whole session —
    /// never re-asked, occurrence silently lost. So the prompts must QUEUE.
    /// <para>
    /// The recorded PEAK is the fact, not a sample: it survives the second ask having resolved by the time we
    /// look. The two <see cref="WaitUntilAsync"/> probes are the corroboration the peak alone cannot give — the
    /// same probe with the same bound must NOT see a second ask while the first is open, and MUST see it once the
    /// first is answered.
    /// </para>
    /// <para>Neutralize: delete <c>_missedPromptGate</c>'s wait/release from <c>AskThenRunMissedAsync</c> → both
    /// asks enter before either resolves, the peak becomes 2 and the negative probe reds.</para>
    /// </summary>
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

    /// <summary>
    /// The other half of the tick's new shape (T0-2): the dialog no longer gates the DEVICE, but it must still
    /// gate ITS OWN job. Nothing may be dispatched, launched, advanced or marked for the late job while its prompt
    /// is unanswered — the answer is the whole decision, and "run it anyway because we stopped waiting" would be
    /// an unattended run the user never authorised.
    /// <para>
    /// The positive control is at the end: the same job, the same service, once the human says yes, IS launched.
    /// Without it, every assertion here would also pass on a service that dropped late jobs on the floor.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// REVIEW FIX. The dedup entry must survive until the OCCURRENCE IS SPENT, not until the answer arrives — the
    /// gap between those two is a window that exists only because T0-2 moved the ask off the tick, and in it the
    /// row is still inside the due window with nothing in <c>_pendingMissedPrompts</c> to stop a re-ask. On "yes"
    /// that gap spans a provider resolve, a settings read and the whole launch (stub chat, run row, workspace)
    /// before <c>MoveScheduleOnAsync</c> writes, so a 30 s tick lands in it easily.
    /// <para>
    /// Held open here deterministically rather than by timing: tick 2 runs only once <c>LaunchAsync</c> has been
    /// ENTERED, which is provably inside the window. Every other door is open at that moment —
    /// <c>AnyExecutingRunForTriggerAsync</c> is false because no run row exists yet, <c>NextFireAt</c> has not
    /// moved so the job is still due, and <c>lateBy</c> is still past the grace period. The dedup set is the only
    /// thing that can refuse, which is what makes this an observation of it.
    /// </para>
    /// <para>Neutralize: move the clear back out of <c>AskThenRunMissedAsync</c>'s <c>finally</c> to just after the
    /// answer → the second tick raises a second dialog and launches a second run of the same goal, and both
    /// assertions red (<c>AskedJobIds</c> holds the id twice).</para>
    /// </summary>
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

    /// <summary>
    /// The shutdown arm the off-tick ask needs (T0-2). An unanswered <c>AskUserToRunMissedAsync</c> is a
    /// <c>TaskCompletionSource</c> that a click completes and nothing else does, so a tracked dispatch waiting on
    /// it would outlive the app: <c>StopAsync</c> would report it in flight forever, and any drain would hang.
    /// The wait therefore takes the tick's token — in production the <c>BackgroundService</c> stopping token that
    /// <c>base.StopAsync</c> cancels.
    /// <para>
    /// Note what is NOT claimed: the dialog itself is not closed (nothing here can), and the job stays deduped —
    /// abandoning is not answering, so nothing is dispatched and nothing is advanced.
    /// </para>
    /// <para>Neutralize: drop the <c>.WaitAsync(ct)</c> from the ask → the drain eats <see cref="SettleAsync"/>'s
    /// full 30 s and times out.</para>
    /// </summary>
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
        //
        // The serialization half is a RECORDED state fact (FakeRunner.PeakConcurrent), not a sample of RunCount
        // at a moment of our choosing. That distinction is the whole point: nothing awaits between
        // `_researchSlots.WaitAsync` and `runner.RunAsync`, so deleting the permit reds the peak deterministically
        // (job 2 would enter the runner inline, on the tick itself) — whereas a RunCount sample stops being
        // evidence the moment the dispatch is queued instead of inlined, which the neighbouring pool work is
        // about to do. The two probes are corroboration: the same helper with the same bound must NOT see a
        // second entry while turn 1 is held, and MUST see one once it is released.
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

    /// <summary>
    /// T0-1(b). A scheduled run that parked at its budget and was later CONTINUED by a human used to book
    /// nothing at all: the launch path books from a continuation on its handle, and <c>ResumeAsync</c> hands out
    /// no handle — so the job's health columns missed the outcome with no crash involved (the premise
    /// <c>D5PausePremiseTests</c> pins). This is the callback that closes it.
    /// <para>
    /// The booking must carry NO schedule write: that occurrence was spent when it was first dispatched, and a
    /// resume that advanced the schedule again would skip an occurrence of a recurring job — which is exactly
    /// what reusing <c>MarkRunCompleteAsync</c> here would have done, so the three empty lists below are the
    /// load-bearing half of this fact, not decoration.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// All THREE declines <c>BookResumedRunAsync</c> documents, plus the control that proves the probe can see a
    /// booking at all — this suite has shipped negatives from probes that watched nothing, so every leg goes in
    /// one fact with one fake. A null <c>TriggerRef</c> is not a scheduled firing (a user's detached run, or a
    /// child run — 07 D7); a row that is PARKED AGAIN did not settle, and booking it would burn a strike on work
    /// the user can still continue; a row that is EXECUTING belongs to a newer dispatch that re-claimed it while
    /// this one was unwinding, and booking it would call <c>MarkFiringOutcomeAsync(succeeded: false)</c> and toast
    /// a failure for a run that is still going. The raiser cannot tell any of them apart from a real settle, which
    /// is why it fires unconditionally and the decision lives in the handler.
    /// <para>Neutralize the third leg specifically: drop <c>|| AgentRunStates.IsExecuting(run.State)</c> from
    /// <c>BookResumedRunAsync</c> → the <c>executing</c> raise books a false outcome and reds both
    /// <see cref="Assert.Empty{T}(System.Collections.Generic.IEnumerable{T})"/> on <c>Bookings</c> and the
    /// <c>FailureCount</c> below.</para>
    /// </summary>
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

        /// <summary>
        /// Health-only bookings (T0-1). Recorded rather than thrown, and kept apart from
        /// <see cref="Completed"/>/<see cref="Failed"/> because the whole point of the new write is that it does
        /// NOT do what those two do to the schedule — a fact that could not tell them apart could not show that
        /// a resumed run booked its outcome without advancing anything.
        /// </summary>
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

    /// <summary>
    /// Records the most callers that were ever inside an <see cref="Enter"/>/<see cref="Exit"/> pair at the same
    /// time. Shared by <see cref="FakeRunner"/> and <see cref="FakeNotificationSurface"/> (research turns and
    /// missed-run dialogs respectively), which each pin a concurrency-of-1 leg the same way: the peak is the
    /// recorded fact that a sample of a running count cannot give, because an overlap that happened at ANY point
    /// in the test is still in the peak afterwards, whereas a count sample stops being evidence the moment the
    /// second entrant is merely queued rather than concurrent. Interlocked throughout, not <c>++</c>: the whole
    /// point is the case where two callers are inside at once, and a read-then-write would let one overwrite the
    /// other's raise.
    /// </summary>
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

        /// <summary>
        /// The most turns that were ever inside <see cref="RunAsync"/> at the same time — the recorded fact that
        /// pins <c>ScheduledJobBackgroundService._researchSlots</c>. See <see cref="PeakConcurrencyTracker"/>.
        /// </summary>
        public int PeakConcurrent => _tracker.Peak;

        public BackgroundTurnResult Result { get; set; } = new(Guid.NewGuid(), true, null);
        public string? ThrowMessage { get; set; }
        public BackgroundTurnRequest? LastRequest { get; private set; }

        /// <summary>When set, the FIRST turn blocks until it is completed — a long-running research job.</summary>
        public TaskCompletionSource? HoldFirstRun { get; set; }

        public Task<BackgroundTurnResult> RunAsync(BackgroundTurnRequest request, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref _runCount);
            // Entry is recorded HERE, before the hold and before the throw — a count taken after the body would
            // read 1 even for two genuinely simultaneous turns. Deliberately still returns (rather than awaits)
            // below: ThrowMessage must keep throwing SYNCHRONOUSLY, because eight other tests share this fake.
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

        /// <summary>Interlocked since T0-2: the ask runs on a dispatched task, and two late jobs are two of them.</summary>
        public int AskCount => Volatile.Read(ref _askCount);

        /// <summary>
        /// The most asks that were ever outstanding at the same time — the recorded fact that pins
        /// <c>ScheduledJobBackgroundService._missedPromptGate</c>, because the real host has the same shape of
        /// limit (<c>ContentDialogHost</c> shows ONE dialog). See <see cref="PeakConcurrencyTracker"/>.
        /// </summary>
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
