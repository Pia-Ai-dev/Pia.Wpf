using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>
/// Batch 09's settings surface. The facts here are the ones a XAML parse test cannot reach: what the VM does
/// with a status this build does not recognise, whether a refusal reaches the user, and whether a malformed
/// time is refused instead of silently coerced into a schedule.
/// </summary>
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
    {
        var service = Substitute.For<IScheduledJobService>();
        service.GetAllAsync().Returns(jobs);
        service.IsOwnedByThisDeviceAsync(Arg.Any<Guid>()).Returns(true);

        var providers = Substitute.For<IProviderService>();
        providers.GetProvidersAsync().Returns(Array.Empty<AiProvider>());

        var runner = Substitute.For<IScheduledJobRunner>();

        return (new ScheduledJobsSettingsViewModel(service, runner, providers, Localizer(),
            NullLogger<SettingsViewModel>.Instance), service, runner);
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

    [Fact]
    public async Task AnUnrecognisedStatus_RendersAsUnknownAndIsInert()
    {
        // NOT defensive padding: ScheduledJobStatus crosses the sync wire as an int and SyncMapper casts it
        // back with no Enum.IsDefined check, so a newer peer's ordinal really does arrive here. The enum's own
        // doc requires any UI to tolerate it. The thing that must never happen is coercing it to Active.
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

    /// <summary>
    /// hermes #2 Q4's UI half. <c>RunNow_SurfacesTheRefusalReason_NotJustAFailure</c> above pins ONE arm
    /// (<c>NotOwner</c>); the two this round added or changed were unpinned, and the switch has no
    /// exhaustiveness guard, so both could be deleted with the suite green:
    /// <list type="bullet">
    /// <item><c>AlreadyRunning</c> falls through to <c>_ => RunNotFound</c> — the user who pressed Run now on
    /// a job whose run is executing is told the job "no longer exists", which is the wrong refusal about a job
    /// that both exists and is running.</item>
    /// <item><c>Dispatched</c> now says "started", not "finished": the method returns as soon as the run has
    /// been handed off, so the old key claimed an outcome that was still 45 minutes away.</item>
    /// </list>
    /// The three keys are asserted as DISTINCT as well as correct — a refusal that reads as some other
    /// refusal is the failure here, and each key's translated text is <c>LocalizationTests</c>' business.
    /// <para>Neutralize: delete the <c>AlreadyRunning</c> arm (or restore the <c>RunFinished</c> key) from
    /// <c>ScheduledJobsSettingsViewModel.RunNowAsync</c> → red.</para>
    /// </summary>
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
            kind: Arg.Any<ScheduledJobKind>());
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
            // T2-18: the editor sends this on every save (its checkbox is part of the same form), so the
            // matcher has to name it — NSubstitute matches on the whole argument list.
            quietOnSuccess: Arg.Any<bool?>());
    }

    [Fact]
    public async Task AFailedLoad_SaysSo_RatherThanRenderingAnEmptyList()
    {
        // "You have no scheduled jobs" and "this could not be read" are different claims — the same
        // distinction Batch 03's trace panel draws.
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
