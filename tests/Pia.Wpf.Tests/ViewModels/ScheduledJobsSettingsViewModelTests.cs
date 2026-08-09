using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>The facts a XAML parse test cannot reach: an unrecognised status, a refusal reaching the user, and
/// a malformed time refused rather than coerced into a schedule.</summary>
public class ScheduledJobsSettingsViewModelTests
{
    private static ILocalizationService Localizer()
    {
        // Echoes the key back, and formats by appending the argument — enough to assert WHICH message was
        // chosen without pinning any English text, which LocalizationTests owns.
        var loc = Substitute.For<ILocalizationService>();
        loc[Arg.Any<string>()].Returns(ci => (string)ci[0]);
        loc.Format(Arg.Any<string>(), Arg.Any<object[]>())
            .Returns(ci => $"{(string)ci[0]}:{string.Join(",", (object[])ci[1])}");
        return loc;
    }

    private static (ScheduledJobsSettingsViewModel Vm, IScheduledJobService Jobs, IScheduledJobRunner Runner)
        CreateSut(params ScheduledJob[] jobs)
        => CreateSut(runs: null, jobs);

    /// <param name="runs">The run-history source, or null for a row with no history line.</param>
    private static (ScheduledJobsSettingsViewModel Vm, IScheduledJobService Jobs, IScheduledJobRunner Runner)
        CreateSut(IAgentRunService? runs, params ScheduledJob[] jobs)
    {
        var service = Substitute.For<IScheduledJobService>();
        service.GetAllAsync().Returns(jobs);
        service.IsOwnedByThisDeviceAsync(Arg.Any<Guid>()).Returns(true);

        var providers = Substitute.For<IProviderService>();
        providers.GetProvidersAsync().Returns(Array.Empty<AiProvider>());

        var runner = Substitute.For<IScheduledJobRunner>();

        return (new ScheduledJobsSettingsViewModel(service, runner, providers, Localizer(),
            NullLogger<SettingsViewModel>.Instance, runs), service, runner);
    }

    private static ScheduledJob NewJob(ScheduledJobStatus status = ScheduledJobStatus.Active) => new()
    {
        Name = "Nightly digest",
        Query = "summarise today",
        Recurrence = RecurrenceType.Daily,
        TimeOfDay = new TimeOnly(9, 0),
        NextFireAt = DateTime.Now.AddHours(4),
        Status = status,
    };

    /// <summary>The summary counts a CANCELLED firing with the failed ones, since it did not deliver either,
    /// while the detail keeps every state apart.</summary>
    [Fact]
    public async Task TheRunHistoryReachesTheRow_WithCancelledCountedAsNotOk()
    {
        var job = NewJob();
        var runs = Substitute.For<IAgentRunService>();
        var settled = new DateTime(2026, 8, 4, 7, 0, 0, DateTimeKind.Utc);
        runs.GetFiringsForTriggerAsync(job.Id, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(
        [
            new ScheduledFiringOutcome(job.Id, Guid.NewGuid(), Guid.NewGuid(), settled, AgentRunState.Completed),
            new ScheduledFiringOutcome(job.Id, Guid.NewGuid(), Guid.NewGuid(), settled.AddHours(-1), AgentRunState.Failed),
            new ScheduledFiringOutcome(job.Id, Guid.NewGuid(), Guid.NewGuid(), settled.AddHours(-2), AgentRunState.Cancelled),
        ]);
        var (vm, _, _) = CreateSut(runs, job);

        await vm.RefreshAsync();

        var row = Assert.Single(vm.Jobs);
        Assert.True(row.HasRecentRuns);
        Assert.Equal("Settings_ScheduledJobs_RecentRuns:3,1,2", row.RecentRunsSummary);
        Assert.Equal(3, row.RecentRunsDetail.Split(Environment.NewLine).Length);
        Assert.Contains("Settings_ScheduledJobs_RunState_Cancelled", row.RecentRunsDetail);
    }

    /// <summary>
    /// A history read that throws must not cost the jobs list: the row renders without the line. Same rule as
    /// the load's own catch one level up.
    /// </summary>
    [Fact]
    public async Task AFailingHistoryRead_LeavesTheRowIntact()
    {
        var job = NewJob();
        var runs = Substitute.For<IAgentRunService>();
        runs.GetFiringsForTriggerAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ScheduledFiringOutcome>>(_ => throw new InvalidOperationException("db"));
        var (vm, _, _) = CreateSut(runs, job);

        await vm.RefreshAsync();

        var row = Assert.Single(vm.Jobs);
        Assert.Equal("Nightly digest", row.Name);
        Assert.False(row.HasRecentRuns);
        Assert.Empty(row.RecentRunsSummary);
    }

    /// <summary>No run service at all, which is the row every other fact in this file builds.</summary>
    [Fact]
    public async Task WithNoRunService_TheRowCarriesNoHistory()
    {
        var (vm, _, _) = CreateSut(NewJob());

        await vm.RefreshAsync();

        Assert.False(Assert.Single(vm.Jobs).HasRecentRuns);
    }

    [Fact]
    public async Task AnUnrecognisedStatus_RendersAsUnknownAndIsInert()
    {
        // NOT defensive padding: ScheduledJobStatus crosses the sync wire as an int and SyncMapper casts it back
        // with no Enum.IsDefined check, so a newer peer's ordinal really does arrive here.
        var job = NewJob((ScheduledJobStatus)7);
        var (vm, _, _) = CreateSut(job);

        await vm.RefreshAsync();

        var row = Assert.Single(vm.Jobs);
        Assert.False(row.StatusIsKnown);
        Assert.False(row.IsEnabled);
        Assert.False(row.CanRunNow);
        Assert.Equal("Settings_ScheduledJobs_Status_Unknown:7", row.StatusLabel);
    }

    [Fact]
    public async Task TogglingAnUnrecognisedStatus_ChangesNothing_AndSaysWhy()
    {
        var job = NewJob((ScheduledJobStatus)7);
        var (vm, service, _) = CreateSut(job);
        await vm.RefreshAsync();

        await vm.ToggleEnabledCommand.ExecuteAsync(vm.Jobs[0]);

        await service.DidNotReceive().EnableAsync(Arg.Any<Guid>());
        await service.DidNotReceive().DisableAsync(Arg.Any<Guid>());
        Assert.Equal("Settings_ScheduledJobs_UnknownStatusInert", vm.StatusMessage);
    }

    [Fact]
    public async Task AJobOwnedElsewhere_CannotBeRunFromHere()
    {
        // The owner guardrail, seen from the UI: the row says so and the button is off. The service refuses
        // independently — this is the courtesy half, and it must agree with the enforcement half.
        var job = NewJob();
        var (vm, service, _) = CreateSut(job);
        service.IsOwnedByThisDeviceAsync(job.Id).Returns(false);

        await vm.RefreshAsync();

        var row = Assert.Single(vm.Jobs);
        Assert.False(row.OwnedByThisDevice);
        Assert.False(row.CanRunNow);
    }

    [Fact]
    public async Task RunNow_SurfacesTheRefusalReason_NotJustAFailure()
    {
        var job = NewJob();
        var (vm, _, runner) = CreateSut(job);
        runner.RunNowAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(ScheduledJobRunNowResult.NotOwner);
        await vm.RefreshAsync();

        await vm.RunNowCommand.ExecuteAsync(vm.Jobs[0]);

        // The distinction the result enum exists for: "another device owns this" is a correct refusal and
        // must not read as a failure the user should retry.
        Assert.Equal("Settings_ScheduledJobs_RunNotOwner", vm.StatusMessage);
    }

    /// <summary>The three keys must be DISTINCT: <c>AlreadyRunning</c> falling through to the default
    /// <c>NotFound</c> arm tells the user a job that exists and is running no longer exists.</summary>
    [Fact]
    public async Task RunNow_TellsTheTruthAboutADispatchAndAboutARunAlreadyGoing()
    {
        var job = NewJob();

        var (dispatchedVm, _, dispatchedRunner) = CreateSut(job);
        dispatchedRunner.RunNowAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(ScheduledJobRunNowResult.Dispatched);
        await dispatchedVm.RefreshAsync();
        await dispatchedVm.RunNowCommand.ExecuteAsync(dispatchedVm.Jobs[0]);
        Assert.Equal("Settings_ScheduledJobs_RunStarted", dispatchedVm.StatusMessage);

        var (busyVm, _, busyRunner) = CreateSut(job);
        busyRunner.RunNowAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(ScheduledJobRunNowResult.AlreadyRunning);
        await busyVm.RefreshAsync();
        await busyVm.RunNowCommand.ExecuteAsync(busyVm.Jobs[0]);
        Assert.Equal("Settings_ScheduledJobs_RunAlreadyRunning", busyVm.StatusMessage);

        // The one it must not be mistaken for. NotFound is the DEFAULT arm, so "already running" landing there
        // is exactly how this regresses — and it is a different sentence about a different situation.
        var (goneVm, _, goneRunner) = CreateSut(job);
        goneRunner.RunNowAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(ScheduledJobRunNowResult.NotFound);
        await goneVm.RefreshAsync();
        await goneVm.RunNowCommand.ExecuteAsync(goneVm.Jobs[0]);
        Assert.Equal("Settings_ScheduledJobs_RunNotFound", goneVm.StatusMessage);
        Assert.NotEqual(goneVm.StatusMessage, busyVm.StatusMessage);
        Assert.NotEqual(goneVm.StatusMessage, dispatchedVm.StatusMessage);
    }

    [Fact]
    public async Task Save_RefusesAMalformedTime_RatherThanCoercingTheSchedule()
    {
        var (vm, service, _) = CreateSut();
        vm.StartCreateCommand.Execute(null);
        vm.EditName = "n";
        vm.EditQuery = "q";
        vm.EditTimeOfDay = "half nine";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Settings_ScheduledJobs_Validation_Time", vm.StatusMessage);
        await service.DidNotReceive().CreateAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<RecurrenceType>(), Arg.Any<TimeOnly>(), Arg.Any<DayOfWeek?>(), Arg.Any<int?>(),
            Arg.Any<int?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(), Arg.Any<IReadOnlyCollection<string>>(),
            Arg.Any<ScheduledJobKind>());
        Assert.True(vm.IsEditorOpen, "a refused save must leave the editor open with the user's input intact.");
    }

    [Fact]
    public async Task Save_OnlyCarriesASpecificDateForAOneOff()
    {
        // A date on a recurring job would persist a field the recurrence calculator ignores, and it would then
        // reappear if the job were later switched to Once.
        var (vm, service, _) = CreateSut();
        vm.StartCreateCommand.Execute(null);
        vm.EditName = "n";
        vm.EditQuery = "q";
        vm.EditRecurrence = RecurrenceType.Daily;
        vm.EditSpecificDate = DateTime.Now.AddDays(5);

        await vm.SaveCommand.ExecuteAsync(null);

        await service.Received(1).CreateAsync("n", "q", RecurrenceType.Daily, Arg.Any<TimeOnly>(),
            Arg.Any<DayOfWeek?>(), Arg.Any<int?>(), Arg.Any<int?>(),
            specificDate: null,
            providerId: Arg.Any<Guid?>(), grantedTools: Arg.Any<IReadOnlyCollection<string>>(),
            kind: Arg.Any<ScheduledJobKind>(), quietOnSuccess: Arg.Any<bool>());
    }

    /// <summary>The editor is ONE panel for create and edit, so the quiet checkbox has to reach BOTH service
    /// calls or a ticked box silently creates notifying jobs.</summary>
    [Fact]
    public async Task CreatingAQuietJob_PassesTheFlagThrough()
    {
        var (vm, service, _) = CreateSut();
        await vm.RefreshAsync();

        vm.StartCreateCommand.Execute(null);
        vm.EditName = "Monitor";
        vm.EditQuery = "check the feed";
        vm.EditQuietOnSuccess = true;

        await vm.SaveCommand.ExecuteAsync(null);

        await service.Received(1).CreateAsync("Monitor", "check the feed", Arg.Any<RecurrenceType>(),
            Arg.Any<TimeOnly>(), Arg.Any<DayOfWeek?>(), Arg.Any<int?>(), Arg.Any<int?>(),
            Arg.Any<DateTime?>(), Arg.Any<Guid?>(), Arg.Any<IReadOnlyCollection<string>>(),
            Arg.Any<ScheduledJobKind>(), quietOnSuccess: true);
    }

    [Fact]
    public async Task EditingAOneOff_PassesTheNewDateThrough_WhichIsWhatReArmsIt()
    {
        // The UI half of the re-arm: without specificDate reaching UpdateAsync, a settled one-off stays
        // settled no matter what the user types. The service half is pinned in ScheduledJobServiceTests.
        var job = NewJob(ScheduledJobStatus.Completed);
        job.Recurrence = RecurrenceType.Once;
        job.SpecificDate = DateTime.Now.Date.AddDays(-2);
        var (vm, service, _) = CreateSut(job);
        await vm.RefreshAsync();

        vm.StartEditCommand.Execute(vm.Jobs[0]);
        var target = DateTime.Now.Date.AddDays(4);
        vm.EditSpecificDate = target;

        await vm.SaveCommand.ExecuteAsync(null);

        await service.Received(1).UpdateAsync(job.Id, Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<RecurrenceType?>(), Arg.Any<TimeOnly?>(), Arg.Any<DayOfWeek?>(), Arg.Any<int?>(),
            Arg.Any<int?>(), Arg.Any<Guid?>(), Arg.Any<IReadOnlyCollection<string>>(),
            specificDate: target, kind: Arg.Any<ScheduledJobKind?>(),
            // The editor sends this on every save, so the matcher has to name it — NSubstitute matches on
            // the whole argument list.
            quietOnSuccess: Arg.Any<bool?>());
    }

    [Fact]
    public async Task AFailedLoad_SaysSo_RatherThanRenderingAnEmptyList()
    {
        // "You have no scheduled jobs" and "this could not be read" are different claims.
        var service = Substitute.For<IScheduledJobService>();
        service.GetAllAsync().Returns<IReadOnlyList<ScheduledJob>>(_ => throw new InvalidOperationException("db"));
        var providers = Substitute.For<IProviderService>();
        providers.GetProvidersAsync().Returns(Array.Empty<AiProvider>());

        var vm = new ScheduledJobsSettingsViewModel(service, Substitute.For<IScheduledJobRunner>(),
            providers, Localizer(), NullLogger<SettingsViewModel>.Instance);

        await vm.RefreshAsync();

        Assert.Empty(vm.Jobs);
        Assert.False(vm.HasJobs);
        Assert.Equal("Settings_ScheduledJobs_LoadFailed", vm.StatusMessage);
    }
}
