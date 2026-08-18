using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Xunit;

namespace Pia.Tests.ViewModels;

/// <summary>The facts a XAML parse test cannot reach: an unrecognised status, a refusal reaching the user, a
/// malformed time refused rather than coerced, and the recurrence day actually leaving the editor.</summary>
public class RoutinesViewModelTests
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

    private sealed record Sut(
        RoutinesViewModel Vm,
        IScheduledJobService Jobs,
        IScheduledJobRunner Runner,
        IAgentRunService Runs,
        IDialogService Dialogs,
        IWindowManagerService Windows);

    private static Sut CreateSut(params ScheduledJob[] jobs) => CreateSut(runs: null, jobs);

    /// <param name="runs">The run-history source, or null for one that reports no firings.</param>
    private static Sut CreateSut(IAgentRunService? runs, params ScheduledJob[] jobs)
    {
        var service = Substitute.For<IScheduledJobService>();
        service.GetAllAsync().Returns(jobs);
        service.IsOwnedByThisDeviceAsync(Arg.Any<ScheduledJob>()).Returns(true);

        var providers = Substitute.For<IProviderService>();
        providers.GetProvidersAsync().Returns(Array.Empty<AiProvider>());

        var runner = Substitute.For<IScheduledJobRunner>();

        // Only stubbed when this method owns the substitute: an Arg.Any setup applied afterwards would override
        // the per-job history a caller had already configured.
        if (runs is null)
        {
            runs = Substitute.For<IAgentRunService>();
            runs.GetFiringsForTriggerAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(Array.Empty<ScheduledFiringOutcome>());
        }

        var dialogs = Substitute.For<IDialogService>();
        dialogs.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        var windows = Substitute.For<IWindowManagerService>();

        var vm = new RoutinesViewModel(service, runner, providers, runs, dialogs, windows, Localizer(),
            NullLogger<RoutinesViewModel>.Instance);

        return new Sut(vm, service, runner, runs, dialogs, windows);
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

    /// <summary>Nothing else loads this view: the list and the provider ComboBox bind collections only
    /// <c>RefreshAsync</c> fills, so without the navigation hook the view renders "no routines yet" forever —
    /// a correct binding with no data behind it, which no parse test can see.</summary>
    [Fact]
    public async Task NavigatingToTheView_LoadsTheJobs()
    {
        var sut = CreateSut(NewJob());

        await sut.Vm.OnNavigatedToAsync(null);

        await sut.Jobs.Received(1).GetAllAsync();
        Assert.True(sut.Vm.HasJobs);
        Assert.NotEmpty(sut.Vm.ProviderChoices);
    }

    /// <summary>The summary counts a CANCELLED firing with the failed ones, since it did not deliver either,
    /// while the detail list keeps every state apart.</summary>
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
        var sut = CreateSut(runs, job);

        await sut.Vm.RefreshAsync();

        var row = Assert.Single(sut.Vm.Jobs);
        Assert.True(row.HasRecentRuns);
        Assert.Equal("Settings_ScheduledJobs_RecentRuns:3,1,2", row.RecentRunsSummary);
        Assert.Equal(3, row.RecentRuns.Count);
        Assert.Contains(row.RecentRuns, r => r.StateLabel == "Settings_ScheduledJobs_RunState_Cancelled");
        Assert.Single(row.RecentRuns, r => r.Succeeded);
    }

    /// <summary>A history read that throws must not cost the jobs list: the row renders without the line.</summary>
    [Fact]
    public async Task AFailingHistoryRead_LeavesTheRowIntact()
    {
        var job = NewJob();
        var runs = Substitute.For<IAgentRunService>();
        runs.GetFiringsForTriggerAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ScheduledFiringOutcome>>(_ => throw new InvalidOperationException("db"));
        var sut = CreateSut(runs, job);

        await sut.Vm.RefreshAsync();

        var row = Assert.Single(sut.Vm.Jobs);
        Assert.Equal("Nightly digest", row.Name);
        Assert.False(row.HasRecentRuns);
        Assert.Empty(row.RecentRunsSummary);
    }

    /// <summary>A firing that failed produced no chat, so the detail row must not offer to open one.</summary>
    [Fact]
    public async Task AFiringWithNoChat_OffersNoLink()
    {
        var job = NewJob();
        var runs = Substitute.For<IAgentRunService>();
        var settled = new DateTime(2026, 8, 4, 7, 0, 0, DateTimeKind.Utc);
        runs.GetFiringsForTriggerAsync(job.Id, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(
        [
            new ScheduledFiringOutcome(job.Id, Guid.NewGuid(), Guid.NewGuid(), settled, AgentRunState.Completed),
            new ScheduledFiringOutcome(job.Id, Guid.NewGuid(), Guid.Empty, settled.AddHours(-1), AgentRunState.Failed),
        ]);
        var sut = CreateSut(runs, job);

        await sut.Vm.RefreshAsync();

        var row = Assert.Single(sut.Vm.Jobs);
        Assert.True(row.RecentRuns[0].HasChat);
        Assert.False(row.RecentRuns[1].HasChat);
    }

    [Fact]
    public async Task AnUnrecognisedStatus_RendersAsUnknownAndIsInert()
    {
        // NOT defensive padding: ScheduledJobStatus crosses the sync wire as an int and SyncMapper casts it back
        // with no Enum.IsDefined check, so a newer peer's ordinal really does arrive here.
        var sut = CreateSut(NewJob((ScheduledJobStatus)7));

        await sut.Vm.RefreshAsync();

        var row = Assert.Single(sut.Vm.Jobs);
        Assert.False(row.StatusIsKnown);
        Assert.False(row.IsEnabled);
        Assert.False(row.CanRunNow);
        Assert.Equal("Settings_ScheduledJobs_Status_Unknown:7", row.StatusLabel);
    }

    [Fact]
    public async Task TogglingAnUnrecognisedStatus_ChangesNothing_AndSaysWhy()
    {
        var sut = CreateSut(NewJob((ScheduledJobStatus)7));
        await sut.Vm.RefreshAsync();
        sut.Vm.SelectedJob = sut.Vm.Jobs[0];

        await sut.Vm.ToggleEnabledCommand.ExecuteAsync(null);

        await sut.Jobs.DidNotReceive().EnableAsync(Arg.Any<Guid>());
        await sut.Jobs.DidNotReceive().DisableAsync(Arg.Any<Guid>());
        Assert.Equal("Settings_ScheduledJobs_UnknownStatusInert", sut.Vm.StatusMessage);
    }

    [Fact]
    public async Task AJobOwnedElsewhere_CannotBeRunFromHere()
    {
        // The owner guardrail, seen from the UI: the row says so and the button is off. The service refuses
        // independently — this is the courtesy half, and it must agree with the enforcement half.
        var job = NewJob();
        var sut = CreateSut(job);
        sut.Jobs.IsOwnedByThisDeviceAsync(job).Returns(false);

        await sut.Vm.RefreshAsync();

        var row = Assert.Single(sut.Vm.Jobs);
        Assert.False(row.OwnedByThisDevice);
        Assert.False(row.CanRunNow);
    }

    [Fact]
    public async Task RunNow_SurfacesTheRefusalReason_NotJustAFailure()
    {
        var job = NewJob();
        var sut = CreateSut(job);
        sut.Runner.RunNowAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(ScheduledJobRunNowResult.NotOwner);
        await sut.Vm.RefreshAsync();
        sut.Vm.SelectedJob = sut.Vm.Jobs[0];

        await sut.Vm.RunNowCommand.ExecuteAsync(null);

        // The distinction the result enum exists for: "another device owns this" is a correct refusal and must
        // not read as a failure the user should retry.
        Assert.Equal("Settings_ScheduledJobs_RunNotOwner", sut.Vm.StatusMessage);
    }

    /// <summary>The three keys must be DISTINCT: <c>AlreadyRunning</c> falling through to the default
    /// <c>NotFound</c> arm tells the user a job that exists and is running no longer exists.</summary>
    [Fact]
    public async Task RunNow_TellsTheTruthAboutADispatchAndAboutARunAlreadyGoing()
    {
        var job = NewJob();

        async Task<string?> MessageFor(ScheduledJobRunNowResult result)
        {
            var sut = CreateSut(job);
            sut.Runner.RunNowAsync(job.Id, Arg.Any<CancellationToken>()).Returns(result);
            await sut.Vm.RefreshAsync();
            sut.Vm.SelectedJob = sut.Vm.Jobs[0];
            await sut.Vm.RunNowCommand.ExecuteAsync(null);
            return sut.Vm.StatusMessage;
        }

        var dispatched = await MessageFor(ScheduledJobRunNowResult.Dispatched);
        var busy = await MessageFor(ScheduledJobRunNowResult.AlreadyRunning);
        // NotFound is the DEFAULT arm, so "already running" landing there is exactly how this regresses — and
        // it is a different sentence about a different situation.
        var gone = await MessageFor(ScheduledJobRunNowResult.NotFound);

        Assert.Equal("Settings_ScheduledJobs_RunStarted", dispatched);
        Assert.Equal("Settings_ScheduledJobs_RunAlreadyRunning", busy);
        Assert.Equal("Settings_ScheduledJobs_RunNotFound", gone);
        Assert.NotEqual(gone, busy);
        Assert.NotEqual(gone, dispatched);
    }

    [Fact]
    public async Task Save_RefusesAMalformedTime_RatherThanCoercingTheSchedule()
    {
        var sut = CreateSut();
        sut.Vm.StartCreateCommand.Execute(null);
        sut.Vm.EditName = "n";
        sut.Vm.EditQuery = "q";
        sut.Vm.EditTimeOfDay = "half nine";

        await sut.Vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Settings_ScheduledJobs_Validation_Time", sut.Vm.StatusMessage);
        await sut.Jobs.DidNotReceive().CreateAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<RecurrenceType>(), Arg.Any<TimeOnly>(), Arg.Any<DayOfWeek?>(), Arg.Any<int?>(),
            Arg.Any<int?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(), Arg.Any<IReadOnlyCollection<string>>(),
            Arg.Any<ScheduledJobKind>());
        Assert.True(sut.Vm.IsEditorOpen, "a refused save must leave the editor open with the user's input intact.");
    }

    [Fact]
    public async Task Save_OnlyCarriesASpecificDateForAOneOff()
    {
        // A date on a recurring job would persist a field the recurrence calculator ignores, and it would then
        // reappear if the job were later switched to Once.
        var sut = CreateSut();
        sut.Vm.StartCreateCommand.Execute(null);
        sut.Vm.EditName = "n";
        sut.Vm.EditQuery = "q";
        sut.Vm.EditRecurrence = RecurrenceType.Daily;
        sut.Vm.EditSpecificDate = DateTime.Now.AddDays(5);

        await sut.Vm.SaveCommand.ExecuteAsync(null);

        await sut.Jobs.Received(1).CreateAsync("n", "q", RecurrenceType.Daily, Arg.Any<TimeOnly>(),
            Arg.Any<DayOfWeek?>(), Arg.Any<int?>(), Arg.Any<int?>(),
            specificDate: null,
            providerId: Arg.Any<Guid?>(), grantedTools: Arg.Any<IReadOnlyCollection<string>>(),
            kind: Arg.Any<ScheduledJobKind>(), quietOnSuccess: Arg.Any<bool>());
    }

    /// <summary>
    /// The whole point of the day pickers. A Weekly job saved without a DayOfWeek leaves
    /// <c>RecurrenceCalculator</c> substituting today's weekday on every recompute, so one late run relocates
    /// the job permanently.
    /// </summary>
    [Fact]
    public async Task Save_CarriesTheChosenWeekday_ForAWeeklyJob()
    {
        var sut = CreateSut();
        sut.Vm.StartCreateCommand.Execute(null);
        sut.Vm.EditName = "n";
        sut.Vm.EditQuery = "q";
        sut.Vm.EditRecurrence = RecurrenceType.Weekly;
        sut.Vm.EditDayOfWeek = DayOfWeek.Thursday;

        await sut.Vm.SaveCommand.ExecuteAsync(null);

        await sut.Jobs.Received(1).CreateAsync("n", "q", RecurrenceType.Weekly, Arg.Any<TimeOnly>(),
            dayOfWeek: DayOfWeek.Thursday, dayOfMonth: null, month: null,
            specificDate: null, providerId: Arg.Any<Guid?>(),
            grantedTools: Arg.Any<IReadOnlyCollection<string>>(),
            kind: Arg.Any<ScheduledJobKind>(), quietOnSuccess: Arg.Any<bool>());
    }

    /// <summary>Each recurrence carries only the fields it reads: a Yearly job needs both month and day, a
    /// Monthly one needs no month, and neither wants a weekday.</summary>
    [Theory]
    [InlineData(RecurrenceType.Monthly, null, 14, null)]
    [InlineData(RecurrenceType.Yearly, null, 14, 6)]
    public async Task Save_CarriesOnlyTheRecurrenceFieldsThatRecurrenceReads(
        RecurrenceType recurrence, DayOfWeek? expectedDayOfWeek, int? expectedDayOfMonth, int? expectedMonth)
    {
        var sut = CreateSut();
        sut.Vm.StartCreateCommand.Execute(null);
        sut.Vm.EditName = "n";
        sut.Vm.EditQuery = "q";
        sut.Vm.EditRecurrence = recurrence;
        sut.Vm.EditDayOfWeek = DayOfWeek.Thursday;
        sut.Vm.EditDayOfMonth = 14;
        sut.Vm.EditMonth = 6;

        await sut.Vm.SaveCommand.ExecuteAsync(null);

        await sut.Jobs.Received(1).CreateAsync("n", "q", recurrence, Arg.Any<TimeOnly>(),
            dayOfWeek: expectedDayOfWeek, dayOfMonth: expectedDayOfMonth, month: expectedMonth,
            specificDate: null, providerId: Arg.Any<Guid?>(),
            grantedTools: Arg.Any<IReadOnlyCollection<string>>(),
            kind: Arg.Any<ScheduledJobKind>(), quietOnSuccess: Arg.Any<bool>());
    }

    /// <summary>A job created before the pickers existed has no stored day, and NextFireAt is the only record of
    /// the day it actually fires on — so that, not today, is what the editor must offer.</summary>
    [Fact]
    public async Task EditingAJobThatPredatesTheDayPickers_OffersTheDayItCurrentlyFiresOn()
    {
        var job = NewJob();
        job.Recurrence = RecurrenceType.Weekly;
        job.DayOfWeek = null;
        // 31 days is not a multiple of 7, so this weekday cannot coincide with today's.
        job.NextFireAt = DateTime.Now.Date.AddDays(-31).AddHours(9);
        var sut = CreateSut(job);
        await sut.Vm.RefreshAsync();
        sut.Vm.SelectedJob = sut.Vm.Jobs[0];

        sut.Vm.StartEditCommand.Execute(null);

        Assert.Equal(job.NextFireAt.DayOfWeek, sut.Vm.EditDayOfWeek);
        Assert.NotEqual(DateTime.Now.DayOfWeek, sut.Vm.EditDayOfWeek);
    }

    /// <summary>The editor is ONE panel for create and edit, so the quiet checkbox has to reach BOTH service
    /// calls or a ticked box silently creates notifying jobs.</summary>
    [Fact]
    public async Task CreatingAQuietJob_PassesTheFlagThrough()
    {
        var sut = CreateSut();
        await sut.Vm.RefreshAsync();

        sut.Vm.StartCreateCommand.Execute(null);
        sut.Vm.EditName = "Monitor";
        sut.Vm.EditQuery = "check the feed";
        sut.Vm.EditQuietOnSuccess = true;

        await sut.Vm.SaveCommand.ExecuteAsync(null);

        await sut.Jobs.Received(1).CreateAsync("Monitor", "check the feed", Arg.Any<RecurrenceType>(),
            Arg.Any<TimeOnly>(), Arg.Any<DayOfWeek?>(), Arg.Any<int?>(), Arg.Any<int?>(),
            Arg.Any<DateTime?>(), Arg.Any<Guid?>(), Arg.Any<IReadOnlyCollection<string>>(),
            Arg.Any<ScheduledJobKind>(), quietOnSuccess: true);
    }

    [Fact]
    public async Task EditingAOneOff_PassesTheNewDateThrough_WhichIsWhatReArmsIt()
    {
        // The UI half of the re-arm: without specificDate reaching UpdateAsync, a settled one-off stays settled
        // no matter what the user types. The service half is pinned in ScheduledJobServiceTests.
        var job = NewJob(ScheduledJobStatus.Completed);
        job.Recurrence = RecurrenceType.Once;
        job.SpecificDate = DateTime.Now.Date.AddDays(-2);
        var sut = CreateSut(job);
        await sut.Vm.RefreshAsync();
        sut.Vm.SelectedJob = sut.Vm.Jobs[0];

        sut.Vm.StartEditCommand.Execute(null);
        var target = DateTime.Now.Date.AddDays(4);
        sut.Vm.EditSpecificDate = target;

        await sut.Vm.SaveCommand.ExecuteAsync(null);

        await sut.Jobs.Received(1).UpdateAsync(job.Id, Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<RecurrenceType?>(), Arg.Any<TimeOnly?>(), Arg.Any<DayOfWeek?>(), Arg.Any<int?>(),
            Arg.Any<int?>(), Arg.Any<Guid?>(), Arg.Any<IReadOnlyCollection<string>>(),
            specificDate: target, kind: Arg.Any<ScheduledJobKind?>(),
            // The editor sends this on every save, so the matcher has to name it — NSubstitute matches on the
            // whole argument list.
            quietOnSuccess: Arg.Any<bool?>());
    }

    /// <summary>
    /// A save that throws must leave the editor exactly as the user left it. Clearing <c>EditingJobId</c> turns
    /// the retry into a duplicate CREATE, and the refresh that used to follow re-resolved the selection to a
    /// fresh row instance, which cancelled the editor and discarded the input.
    /// </summary>
    [Fact]
    public async Task AFailedSave_KeepsTheEditorOpen_WithItsEditingIdAndTheTypedInput()
    {
        var job = NewJob();
        var sut = CreateSut(job);
        sut.Jobs.When(x => x.UpdateAsync(job.Id, Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<RecurrenceType?>(), Arg.Any<TimeOnly?>(), Arg.Any<DayOfWeek?>(), Arg.Any<int?>(),
                Arg.Any<int?>(), Arg.Any<Guid?>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<DateTime?>(),
                Arg.Any<ScheduledJobKind?>(), Arg.Any<bool?>()))
            .Do(_ => throw new InvalidOperationException("db"));
        await sut.Vm.RefreshAsync();
        sut.Vm.SelectedJob = sut.Vm.Jobs[0];
        sut.Vm.StartEditCommand.Execute(null);
        sut.Vm.EditName = "Renamed";

        await sut.Vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Settings_ScheduledJobs_SaveFailed", sut.Vm.StatusMessage);
        Assert.True(sut.Vm.IsEditorOpen);
        Assert.Equal(job.Id, sut.Vm.EditingJobId);
        Assert.Equal("Renamed", sut.Vm.EditName);
    }

    /// <summary>Delete is irreversible and had no gate at all in the settings surface it replaces.</summary>
    [Fact]
    public async Task Delete_AsksFirst_AndKeepsTheJobWhenTheUserDeclines()
    {
        var job = NewJob();
        var sut = CreateSut(job);
        sut.Dialogs.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(false);
        await sut.Vm.RefreshAsync();
        sut.Vm.SelectedJob = sut.Vm.Jobs[0];

        await sut.Vm.DeleteCommand.ExecuteAsync(null);

        await sut.Dialogs.Received(1).ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Any<string>());
        await sut.Jobs.DidNotReceive().DeleteAsync(Arg.Any<Guid>());
        Assert.NotNull(sut.Vm.SelectedJob);
    }

    [Fact]
    public async Task Delete_RemovesTheJob_OnceConfirmed()
    {
        var job = NewJob();
        var sut = CreateSut(job);
        await sut.Vm.RefreshAsync();
        sut.Vm.SelectedJob = sut.Vm.Jobs[0];

        await sut.Vm.DeleteCommand.ExecuteAsync(null);

        await sut.Jobs.Received(1).DeleteAsync(job.Id);
    }

    /// <summary>Selection drives the whole right pane, so the commands behind it must be off without one — this
    /// is the gate that replaces the settings surface's clickable-but-silently-dead buttons.</summary>
    [Fact]
    public async Task WithNothingSelected_TheJobCommandsAreOff()
    {
        var sut = CreateSut(NewJob());
        await sut.Vm.RefreshAsync();
        sut.Vm.SelectedJob = null;

        Assert.False(sut.Vm.StartEditCommand.CanExecute(null));
        Assert.False(sut.Vm.DeleteCommand.CanExecute(null));
        Assert.False(sut.Vm.RunNowCommand.CanExecute(null));
        Assert.False(sut.Vm.ToggleEnabledCommand.CanExecute(null));

        sut.Vm.SelectedJob = sut.Vm.Jobs[0];

        Assert.True(sut.Vm.StartEditCommand.CanExecute(null));
        Assert.True(sut.Vm.DeleteCommand.CanExecute(null));
    }

    [Fact]
    public async Task SelectingADifferentJob_ClosesAnEditorOpenOnThePreviousOne()
    {
        // Otherwise the next save writes this job's fields onto the previously edited job's id.
        var first = NewJob();
        var second = NewJob();
        var sut = CreateSut(first, second);
        await sut.Vm.RefreshAsync();
        sut.Vm.SelectedJob = sut.Vm.Jobs[0];
        sut.Vm.StartEditCommand.Execute(null);
        Assert.True(sut.Vm.IsEditorOpen);

        sut.Vm.SelectedJob = sut.Vm.Jobs[1];

        Assert.False(sut.Vm.IsEditorOpen);
        Assert.Null(sut.Vm.EditingJobId);
    }

    [Fact]
    public async Task OpeningTheChatOfAFiring_RoutesToTheAssistantWindow()
    {
        var chatId = Guid.NewGuid();
        var sut = CreateSut();

        sut.Vm.OpenRunChatCommand.Execute(new RoutineRunRow
        {
            SettledAt = DateTime.Now,
            Succeeded = true,
            StateLabel = "Completed",
            ChatId = chatId,
        });

        sut.Windows.Received(1).ShowAssistantChat(chatId);
        await Task.CompletedTask;
    }

    /// <summary>Queues instead of running, which is what the VM's marshal does whenever the context captured at
    /// construction is not the one the caller is on — the ordering the running app actually has.</summary>
    private sealed class DeferringContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _pending = new();

        public override void Post(SendOrPostCallback d, object? state) => _pending.Enqueue((d, state));

        public void Drain()
        {
            while (_pending.Count > 0)
            {
                var (callback, state) = _pending.Dequeue();
                callback(state);
            }
        }
    }

    /// <summary>Under the default inline marshal every save looks right. Deferred — which is what the app does —
    /// a save that picked its row out of <c>Jobs</c> after awaiting the refresh read the rows from before it, so
    /// a new routine went unselected and the pane kept showing whichever one was selected before.</summary>
    [Fact]
    public async Task SavingANewRoutine_SelectsIt_WhenTheRebuildIsDeferred()
    {
        var existing = NewJob();
        var created = NewJob();
        var stored = new List<ScheduledJob> { existing };

        var jobs = Substitute.For<IScheduledJobService>();
        jobs.GetAllAsync().Returns(_ => stored.ToArray());
        jobs.IsOwnedByThisDeviceAsync(Arg.Any<ScheduledJob>()).Returns(true);
        jobs.CreateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RecurrenceType>(), Arg.Any<TimeOnly>(),
                Arg.Any<DayOfWeek?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(),
                Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<ScheduledJobKind>(), Arg.Any<bool>())
            .Returns(_ => { stored.Add(created); return created; });

        var providers = Substitute.For<IProviderService>();
        providers.GetProvidersAsync().Returns(Array.Empty<AiProvider>());
        var runs = Substitute.For<IAgentRunService>();
        runs.GetFiringsForTriggerAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ScheduledFiringOutcome>());

        // Installed only for the constructor: the VM captures this instance, and the comparison against
        // SynchronizationContext.Current then fails for every later call, exactly as it does under WPF.
        var context = new DeferringContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        RoutinesViewModel vm;
        try
        {
            vm = new RoutinesViewModel(jobs, Substitute.For<IScheduledJobRunner>(), providers, runs,
                Substitute.For<IDialogService>(), Substitute.For<IWindowManagerService>(), Localizer(),
                NullLogger<RoutinesViewModel>.Instance);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        await vm.RefreshAsync();
        context.Drain();
        vm.SelectedJob = Assert.Single(vm.Jobs);

        vm.StartCreateCommand.Execute(null);
        vm.EditName = "Morning briefing";
        vm.EditQuery = "what happened overnight";
        await vm.SaveCommand.ExecuteAsync(null);
        context.Drain();

        Assert.Equal(created.Id, vm.SelectedJob?.Id);
    }

    [Fact]
    public async Task AFailedLoad_SaysSo_RatherThanRenderingAnEmptyList()
    {
        // "You have no routines" and "this could not be read" are different claims.
        var sut = CreateSut();
        sut.Jobs.GetAllAsync().Returns<IReadOnlyList<ScheduledJob>>(_ => throw new InvalidOperationException("db"));

        await sut.Vm.RefreshAsync();

        Assert.Empty(sut.Vm.Jobs);
        Assert.False(sut.Vm.HasJobs);
        Assert.Equal("Settings_ScheduledJobs_LoadFailed", sut.Vm.StatusMessage);
    }
}
