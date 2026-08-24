using System.Collections;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Pia.Models;
using Pia.Resources.Strings;
using Pia.Services;
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
        IProviderService Providers,
        IPersonaService Personas,
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

        // Must be stubbed: an unstubbed Task-returning member hands back null, and RefreshAsync awaits it.
        var personas = Substitute.For<IPersonaService>();
        personas.GetPersonasAsync().Returns(Array.Empty<Persona>());

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

        var vm = new RoutinesViewModel(service, runner, providers, personas, runs, dialogs, windows, Localizer(),
            NullLogger<RoutinesViewModel>.Instance);

        return new Sut(vm, service, runner, providers, personas, runs, dialogs, windows);
    }

    private static Persona NewPersona(string name) => new()
    {
        Name = name,
        SystemPrompt = "be brief",
    };

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
            Arg.Any<ScheduledJobKind>(), Arg.Any<bool>(), Arg.Any<Guid?>(), Arg.Any<ReasoningEffort?>(),
            Arg.Any<string?>());
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
            kind: Arg.Any<ScheduledJobKind>(), quietOnSuccess: Arg.Any<bool>(),
            personaId: Arg.Any<Guid?>(), reasoningEffort: Arg.Any<ReasoningEffort?>());
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
            kind: Arg.Any<ScheduledJobKind>(), quietOnSuccess: Arg.Any<bool>(),
            personaId: Arg.Any<Guid?>(), reasoningEffort: Arg.Any<ReasoningEffort?>());
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
            kind: Arg.Any<ScheduledJobKind>(), quietOnSuccess: Arg.Any<bool>(),
            personaId: Arg.Any<Guid?>(), reasoningEffort: Arg.Any<ReasoningEffort?>());
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
            Arg.Any<ScheduledJobKind>(), quietOnSuccess: true,
            personaId: Arg.Any<Guid?>(), reasoningEffort: Arg.Any<ReasoningEffort?>());
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
            // The editor sends these on every save, so the matcher has to name them — NSubstitute matches on
            // the whole argument list.
            quietOnSuccess: Arg.Any<bool?>(), personaId: Arg.Any<Guid?>(),
            reasoningEffort: Arg.Any<ReasoningEffort?>(), clearReasoningEffort: Arg.Any<bool>());
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
                Arg.Any<ScheduledJobKind?>(), Arg.Any<bool?>(), Arg.Any<Guid?>(),
                Arg.Any<ReasoningEffort?>(), Arg.Any<bool>()))
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
                Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<ScheduledJobKind>(), Arg.Any<bool>(),
                Arg.Any<Guid?>(), Arg.Any<ReasoningEffort?>(), Arg.Any<string?>())
            .Returns(_ => { stored.Add(created); return created; });

        var providers = Substitute.For<IProviderService>();
        providers.GetProvidersAsync().Returns(Array.Empty<AiProvider>());
        var personas = Substitute.For<IPersonaService>();
        personas.GetPersonasAsync().Returns(Array.Empty<Persona>());
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
            vm = new RoutinesViewModel(jobs, Substitute.For<IScheduledJobRunner>(), providers, personas, runs,
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

    /// <summary>The card text is resolved once where the localizer is, so the internal catalog never has to reach
    /// a binding — and the id is what a UI script addresses the card by.</summary>
    [Fact]
    public void TheBlueprintCards_CarryResolvedTextAndAnAddressableId()
    {
        var sut = CreateSut();

        Assert.Equal(RoutineBlueprintCatalog.All.Count, sut.Vm.Blueprints.Count);
        Assert.True(sut.Vm.HasBlueprints);

        var card = Assert.Single(sut.Vm.Blueprints, c => c.Key == RoutineBlueprintCatalog.TopicDigest);
        var blueprint = RoutineBlueprintCatalog.Find(RoutineBlueprintCatalog.TopicDigest)!;
        Assert.Equal(blueprint.TitleKey, card.Title);
        Assert.Equal(blueprint.DescriptionKey, card.Description);
        Assert.Equal($"Settings_ScheduledJobs_Kind_{blueprint.Kind}", card.KindLabel);
        Assert.Equal($"Settings_ScheduledJobs_Recurrence_{blueprint.Recurrence}", card.RecurrenceLabel);
        Assert.Equal("08:00", card.TimeLabel);
        Assert.Equal("Routines_Blueprint_topic-digest", card.AutomationId);
    }

    /// <summary>The whole point of the feature: the editor opens carrying the blueprint instead of a blank box.</summary>
    [Fact]
    public async Task PickingABlueprint_PrefillsTheEditorAndOpensIt()
    {
        var sut = CreateSut();
        await sut.Vm.RefreshAsync();
        var blueprint = RoutineBlueprintCatalog.Find(RoutineBlueprintCatalog.TopicDigest)!;

        sut.Vm.StartFromBlueprintCommand.Execute(RoutineBlueprintCatalog.TopicDigest);

        Assert.True(sut.Vm.IsEditorOpen);
        Assert.Null(sut.Vm.EditingJobId);
        Assert.Equal(blueprint.TitleKey, sut.Vm.EditName);
        // Rendered, not the template: the goal box is in front of the user, so a literal {topic} is a defect.
        Assert.Equal(RoutineBlueprintFill.ToCreateArgs(blueprint).Query, sut.Vm.EditQuery);
        Assert.DoesNotContain("{", sut.Vm.EditQuery);
        Assert.Equal(blueprint.Kind, sut.Vm.EditKind);
        Assert.Equal(blueprint.Recurrence, sut.Vm.EditRecurrence);
        Assert.Equal("08:00", sut.Vm.EditTimeOfDay);
        Assert.Equal(string.Join(", ", blueprint.GrantedTools), sut.Vm.EditGrantedTools);
        Assert.Equal(blueprint.QuietOnSuccess, sut.Vm.EditQuietOnSuccess);
        Assert.Null(sut.Vm.EditSpecificDate);
        Assert.Equal(sut.Vm.ProviderChoices.FirstOrDefault(), sut.Vm.EditProvider);
    }

    /// <summary>A card is an offer, not a decision — nothing is written until the user reads it and saves.</summary>
    [Fact]
    public async Task PickingABlueprint_CreatesNothing()
    {
        var sut = CreateSut();
        await sut.Vm.RefreshAsync();

        sut.Vm.StartFromBlueprintCommand.Execute(RoutineBlueprintCatalog.TopicDigest);

        await sut.Jobs.DidNotReceive().CreateAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<RecurrenceType>(), Arg.Any<TimeOnly>(), Arg.Any<DayOfWeek?>(), Arg.Any<int?>(),
            Arg.Any<int?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(), Arg.Any<IReadOnlyCollection<string>>(),
            Arg.Any<ScheduledJobKind>(), Arg.Any<bool>(), Arg.Any<Guid?>(), Arg.Any<ReasoningEffort?>(),
            Arg.Any<string?>());
    }

    /// <summary>The prefill has to survive the editor's own parse, or the card would open a form that refuses to
    /// save — and the save must still be the one existing create path.</summary>
    [Fact]
    public async Task ABlueprintSavesThroughTheExistingCreatePath()
    {
        var sut = CreateSut();
        sut.Jobs.CreateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RecurrenceType>(), Arg.Any<TimeOnly>(),
                Arg.Any<DayOfWeek?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(),
                Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<ScheduledJobKind>(), Arg.Any<bool>(),
                Arg.Any<Guid?>(), Arg.Any<ReasoningEffort?>(), Arg.Any<string?>())
            .Returns(NewJob());
        await sut.Vm.RefreshAsync();
        var blueprint = RoutineBlueprintCatalog.Find(RoutineBlueprintCatalog.TopicDigest)!;

        sut.Vm.StartFromBlueprintCommand.Execute(RoutineBlueprintCatalog.TopicDigest);
        await sut.Vm.SaveCommand.ExecuteAsync(null);

        Assert.Null(sut.Vm.StatusMessage);
        await sut.Jobs.Received(1).CreateAsync(blueprint.TitleKey,
            RoutineBlueprintFill.ToCreateArgs(blueprint).Query!,
            blueprint.Recurrence, new TimeOnly(8, 0), Arg.Any<DayOfWeek?>(), Arg.Any<int?>(),
            Arg.Any<int?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(),
            Arg.Is<IReadOnlyCollection<string>>(g => g.Count == 0), blueprint.Kind, false,
            personaId: Arg.Any<Guid?>(), reasoningEffort: Arg.Any<ReasoningEffort?>(),
            blueprintKey: blueprint.Key);
    }

    /// <summary>Provenance, so "which cards do people actually use" is answerable. A blank start and an edit of
    /// an existing job must both record nothing.</summary>
    [Fact]
    public async Task ABlankStart_RecordsNoBlueprintKey()
    {
        var sut = CreateSut();
        sut.Jobs.CreateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RecurrenceType>(), Arg.Any<TimeOnly>(),
                Arg.Any<DayOfWeek?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(),
                Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<ScheduledJobKind>(), Arg.Any<bool>(),
                Arg.Any<Guid?>(), Arg.Any<ReasoningEffort?>(), Arg.Any<string?>())
            .Returns(NewJob());
        await sut.Vm.RefreshAsync();

        sut.Vm.StartCreateCommand.Execute(null);
        sut.Vm.EditName = "Hand-written";
        sut.Vm.EditQuery = "do the thing";
        await sut.Vm.SaveCommand.ExecuteAsync(null);

        Assert.Null(sut.Vm.StatusMessage);
        await sut.Jobs.Received(1).CreateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RecurrenceType>(),
            Arg.Any<TimeOnly>(), Arg.Any<DayOfWeek?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<DateTime?>(),
            Arg.Any<Guid?>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<ScheduledJobKind>(),
            Arg.Any<bool>(), Arg.Any<Guid?>(), Arg.Any<ReasoningEffort?>(), blueprintKey: null);
    }

    /// <summary>The key must not survive a card click that the user then abandons for a blank start.</summary>
    [Fact]
    public async Task ABlankStartAfterACardClick_RecordsNoBlueprintKey()
    {
        var sut = CreateSut();
        sut.Jobs.CreateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RecurrenceType>(), Arg.Any<TimeOnly>(),
                Arg.Any<DayOfWeek?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(),
                Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<ScheduledJobKind>(), Arg.Any<bool>(),
                Arg.Any<Guid?>(), Arg.Any<ReasoningEffort?>(), Arg.Any<string?>())
            .Returns(NewJob());
        await sut.Vm.RefreshAsync();

        sut.Vm.StartFromBlueprintCommand.Execute(RoutineBlueprintCatalog.TopicDigest);
        sut.Vm.StartCreateCommand.Execute(null);
        sut.Vm.EditName = "Hand-written";
        sut.Vm.EditQuery = "do the thing";
        await sut.Vm.SaveCommand.ExecuteAsync(null);

        await sut.Jobs.Received(1).CreateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RecurrenceType>(),
            Arg.Any<TimeOnly>(), Arg.Any<DayOfWeek?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<DateTime?>(),
            Arg.Any<Guid?>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<ScheduledJobKind>(),
            Arg.Any<bool>(), Arg.Any<Guid?>(), Arg.Any<ReasoningEffort?>(), blueprintKey: null);
    }

    /// <summary>Keys are persisted-adjacent, so a stale one has to be inert rather than open a half-filled form.</summary>
    [Fact]
    public void AnUnknownBlueprintKey_LeavesTheEditorClosed()
    {
        var sut = CreateSut();

        sut.Vm.StartFromBlueprintCommand.Execute("no-such-blueprint");
        sut.Vm.StartFromBlueprintCommand.Execute(null);

        Assert.False(sut.Vm.IsEditorOpen);
        Assert.Equal(string.Empty, sut.Vm.EditQuery);
    }

    [Fact]
    public async Task NewRoutine_OpensTheCatalogRatherThanTheEditor()
    {
        var sut = CreateSut(NewJob());
        await sut.Vm.RefreshAsync();

        sut.Vm.BrowseBlueprintsCommand.Execute(null);

        Assert.True(sut.Vm.ShowsCatalog);
        Assert.False(sut.Vm.IsEditorOpen);
        Assert.False(sut.Vm.ShowsPlaceholder);
    }

    [Fact]
    public async Task StartFromBlank_StillOpensAnEmptyEditor()
    {
        var sut = CreateSut(NewJob());
        await sut.Vm.RefreshAsync();
        sut.Vm.BrowseBlueprintsCommand.Execute(null);

        sut.Vm.StartCreateCommand.Execute(null);

        Assert.True(sut.Vm.IsEditorOpen);
        Assert.False(sut.Vm.ShowsCatalog);
        Assert.Null(sut.Vm.EditingJobId);
        Assert.Equal(string.Empty, sut.Vm.EditName);
        Assert.Equal(string.Empty, sut.Vm.EditQuery);
    }

    /// <summary>The substituted localizer echoes the resx key, so the term here matches a key stem rather than
    /// the English title.</summary>
    [Fact]
    public void ASearchTerm_NarrowsTheCatalogAndDropsTheGroupItEmpties()
    {
        var sut = CreateSut();

        sut.Vm.SearchQuery = "market";

        var group = Assert.Single(sut.Vm.BlueprintGroups);
        Assert.Equal(RoutineBlueprintCategories.Ready, group.Key);
        Assert.Equal(RoutineBlueprintCatalog.MarketSnapshot, Assert.Single(group.Cards).Key);
        Assert.True(sut.Vm.HasBlueprintMatches);
    }

    [Fact]
    public void ATermNothingMatches_LeavesNoGroupsToRender()
    {
        var sut = CreateSut();

        sut.Vm.SearchQuery = "no-blueprint-says-this";

        Assert.Empty(sut.Vm.BlueprintGroups);
        Assert.False(sut.Vm.HasBlueprintMatches);
    }

    [Fact]
    public void ASearchSpanningBothGroups_ForcesBothExpanded()
    {
        var sut = CreateSut();
        Assert.False(sut.Vm.BlueprintGroups
            .Single(g => g.Key == RoutineBlueprintCategories.YourData).IsExpanded);

        sut.Vm.SearchQuery = "brief";

        Assert.Equal(
            new[] { RoutineBlueprintCategories.Ready, RoutineBlueprintCategories.YourData },
            sut.Vm.BlueprintGroups.Select(g => g.Key).ToArray());
        Assert.All(sut.Vm.BlueprintGroups, g => Assert.True(g.IsExpanded));
        Assert.Equal(RoutineBlueprintCatalog.NewsBriefing, Assert.Single(sut.Vm.BlueprintGroups[0].Cards).Key);
        Assert.Equal(RoutineBlueprintCatalog.MorningBrief, Assert.Single(sut.Vm.BlueprintGroups[1].Cards).Key);
    }

    /// <summary>The one keystroke that matters is the one INTO a search; after that the user's own collapse
    /// has to survive.</summary>
    [Fact]
    public void AGroupCollapsedMidSearch_StaysCollapsedOnTheNextKeystroke()
    {
        var sut = CreateSut();
        sut.Vm.SearchQuery = "brie";
        var group = sut.Vm.BlueprintGroups.Single(g => g.Key == RoutineBlueprintCategories.YourData);
        Assert.True(group.IsExpanded);
        group.IsExpanded = false;

        sut.Vm.SearchQuery = "brief";

        Assert.False(sut.Vm.BlueprintGroups
            .Single(g => g.Key == RoutineBlueprintCategories.YourData).IsExpanded);
    }

    /// <summary>A query left over from the last visit would open the menu on one card of twenty.</summary>
    [Fact]
    public async Task ReopeningTheCatalog_ClearsTheSearchItWasLeftWith()
    {
        var sut = CreateSut(NewJob());
        await sut.Vm.RefreshAsync();
        sut.Vm.SearchQuery = "market";

        sut.Vm.BrowseBlueprintsCommand.Execute(null);

        Assert.Equal(string.Empty, sut.Vm.SearchQuery);
        Assert.Equal(
            new[] { RoutineBlueprintCategories.Ready, RoutineBlueprintCategories.YourData },
            sut.Vm.BlueprintGroups.Select(g => g.Key).ToArray());
        Assert.False(sut.Vm.BlueprintGroups
            .Single(g => g.Key == RoutineBlueprintCategories.YourData).IsExpanded);
    }

    /// <summary>The pane a save lands on is decided before the reload, not by whether it succeeds.</summary>
    [Fact]
    public async Task ASaveFromACard_ClosesTheCatalogEvenWhenTheReloadFails()
    {
        var sut = CreateSut();
        sut.Jobs.CreateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RecurrenceType>(), Arg.Any<TimeOnly>(),
                Arg.Any<DayOfWeek?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(),
                Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<ScheduledJobKind>(), Arg.Any<bool>(),
                Arg.Any<Guid?>(), Arg.Any<ReasoningEffort?>(), Arg.Any<string?>())
            .Returns(NewJob());
        await sut.Vm.RefreshAsync();
        Assert.True(sut.Vm.ShowsCatalog);
        sut.Vm.StartFromBlueprintCommand.Execute(RoutineBlueprintCatalog.TopicDigest);
        sut.Jobs.GetAllAsync().ThrowsAsync(new InvalidOperationException("the table is gone"));

        await sut.Vm.SaveCommand.ExecuteAsync(null);

        Assert.False(sut.Vm.IsCatalogOpen);
        Assert.False(sut.Vm.ShowsCatalog);
    }

    [Fact]
    public async Task WithNothingScheduled_TheCatalogOpensItself()
    {
        var sut = CreateSut();

        await sut.Vm.RefreshAsync();

        Assert.True(sut.Vm.ShowsCatalog);
        Assert.False(sut.Vm.ShowsPlaceholder);
    }

    [Fact]
    public async Task WithJobsAlreadyThere_TheCatalogStaysShut()
    {
        var sut = CreateSut(NewJob());

        await sut.Vm.RefreshAsync();

        Assert.False(sut.Vm.ShowsCatalog);
        Assert.True(sut.Vm.ShowsPlaceholder);
    }

    /// <summary>The panes are siblings in one Grid with no ZIndex, so a second true state still hit-tests.</summary>
    [Fact]
    public async Task ExactlyOnePaneShows_AcrossTheWholeCycle()
    {
        var sut = CreateSut(NewJob());
        await sut.Vm.RefreshAsync();
        AssertOnlyPane(sut.Vm, "placeholder");

        sut.Vm.BrowseBlueprintsCommand.Execute(null);
        AssertOnlyPane(sut.Vm, "catalog");

        sut.Vm.SelectedJob = Assert.Single(sut.Vm.Jobs);
        AssertOnlyPane(sut.Vm, "detail");

        // A delete or a deselect must not spring the menu back open over the empty pane.
        sut.Vm.SelectedJob = null;
        AssertOnlyPane(sut.Vm, "placeholder");

        sut.Vm.BrowseBlueprintsCommand.Execute(null);
        sut.Vm.StartCreateCommand.Execute(null);
        AssertOnlyPane(sut.Vm, "editor");

        sut.Vm.CancelEditCommand.Execute(null);
        AssertOnlyPane(sut.Vm, "catalog");
    }

    /// <summary>The exclusion is in the expression, not in the selection handler.</summary>
    [Fact]
    public async Task TheCatalogFlag_CannotBeatASelectedJob()
    {
        var sut = CreateSut(NewJob());
        await sut.Vm.RefreshAsync();
        sut.Vm.SelectedJob = Assert.Single(sut.Vm.Jobs);

        sut.Vm.IsCatalogOpen = true;

        Assert.True(sut.Vm.ShowsDetail);
        Assert.False(sut.Vm.ShowsCatalog);
        Assert.False(sut.Vm.ShowsPlaceholder);
    }

    /// <summary>The hint has to read the same rule the prompt composer applies.</summary>
    [Theory]
    [InlineData(AiProviderType.Ollama, false, true)]
    [InlineData(AiProviderType.OpenAI, true, false)]
    [InlineData(AiProviderType.PiaCloud, false, false)]
    public async Task TheWebSearchHint_FollowsTheProviderAnAssistantRunWouldUse(
        AiProviderType type, bool enableWebSearch, bool expectHint)
    {
        var sut = CreateSut(NewJob());
        sut.Providers.GetDefaultProviderForModeAsync(WindowMode.Assistant).Returns(new AiProvider
        {
            Name = "default",
            Endpoint = "http://localhost:11434",
            ProviderType = type,
            EnableWebSearch = enableWebSearch,
        });

        await sut.Vm.RefreshAsync();

        Assert.Equal(expectHint, sut.Vm.DefaultProviderCannotSearchWeb);
    }

    [Fact]
    public async Task WithNoProviderToResolve_TheWebSearchHintStaysDown()
    {
        var sut = CreateSut(NewJob());

        await sut.Vm.RefreshAsync();

        Assert.False(sut.Vm.DefaultProviderCannotSearchWeb);
    }

    private static void AssertOnlyPane(RoutinesViewModel vm, string expected)
    {
        var showing = new List<string>();
        if (vm.IsEditorOpen) showing.Add("editor");
        if (vm.ShowsCatalog) showing.Add("catalog");
        if (vm.ShowsDetail) showing.Add("detail");
        if (vm.ShowsPlaceholder) showing.Add("placeholder");

        Assert.Equal(expected, Assert.Single(showing));
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

    /// <summary>The picker leads with the "inherit" row, the way the provider one does, so a routine with no
    /// pin has something selected to show.</summary>
    [Fact]
    public async Task ThePersonaPicker_LeadsWithTheDefaultRow_ThenTheServicesPersonas()
    {
        var persona = NewPersona("Analyst");
        var sut = CreateSut();
        sut.Personas.GetPersonasAsync().Returns(new[] { persona });

        await sut.Vm.RefreshAsync();

        Assert.Equal(2, sut.Vm.PersonaChoices.Count);
        Assert.Null(sut.Vm.PersonaChoices[0].Id);
        Assert.Equal("Routines_Field_Persona_Default", sut.Vm.PersonaChoices[0].Name);
        Assert.False(sut.Vm.PersonaChoices[0].IsUnavailable);
        Assert.Equal(persona.Id, sut.Vm.PersonaChoices[1].Id);
        Assert.Equal("Analyst", sut.Vm.PersonaChoices[1].Name);
    }

    /// <summary>A label per member plus the inherit row, every label from the localizer: a ComboBox bound
    /// straight to the enum values renders the C# identifier in every locale.</summary>
    [Fact]
    public void TheEffortPicker_OffersAnInheritRowAndEveryMember_EachLocalized()
    {
        var sut = CreateSut();

        Assert.Equal(Enum.GetValues<ReasoningEffort>().Length + 1, sut.Vm.EffortChoices.Count);
        Assert.Null(sut.Vm.EffortChoices[0].Value);
        Assert.Equal("Routines_Field_Effort_Default", sut.Vm.EffortChoices[0].Label);
        foreach (var effort in Enum.GetValues<ReasoningEffort>())
            Assert.Equal($"Routines_Effort_{effort}",
                Assert.Single(sut.Vm.EffortChoices, c => c.Value == effort).Label);
    }

    /// <summary>The inherit row must not read as <c>None</c> in any locale — a blur risks pinning no-reasoning
    /// on unattended runs by accident, so this checks the shipped strings, not the echoing localizer double.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("de")]
    [InlineData("fr")]
    public void TheInheritRowNeverReusesTheNoReasoningWording(string culture)
    {
        var target = culture.Length == 0 ? CultureInfo.InvariantCulture : new CultureInfo(culture);
        var inherit = ViewStrings.ResourceManager.GetString("Routines_Field_Effort_Default", target);
        var none = ViewStrings.ResourceManager.GetString("Routines_Effort_None", target);

        Assert.False(string.IsNullOrWhiteSpace(inherit));
        Assert.False(string.IsNullOrWhiteSpace(none));
        Assert.False(inherit!.Contains(none!, StringComparison.OrdinalIgnoreCase),
            $"the inherit row must not read as the None row in '{culture}': \"{inherit}\" vs \"{none}\".");
    }

    /// <summary>Both pins have to be filled from the row AND forwarded on save; a field wired into three of the
    /// four editor entry points is the bug the quiet flag already had to have fixed once.</summary>
    [Fact]
    public async Task StartEdit_FillsBothPins_AndSaveForwardsThem()
    {
        var persona = NewPersona("Analyst");
        var job = NewJob();
        job.PersonaId = persona.Id;
        job.ReasoningEffort = ReasoningEffort.High;
        var sut = CreateSut(job);
        sut.Personas.GetPersonasAsync().Returns(new[] { persona });
        await sut.Vm.RefreshAsync();
        sut.Vm.SelectedJob = sut.Vm.Jobs[0];

        sut.Vm.StartEditCommand.Execute(null);

        Assert.Equal(persona.Id, sut.Vm.EditPersona?.Id);
        Assert.Equal(ReasoningEffort.High, sut.Vm.EditEffort?.Value);

        await sut.Vm.SaveCommand.ExecuteAsync(null);

        await sut.Jobs.Received(1).UpdateAsync(job.Id, Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<RecurrenceType?>(), Arg.Any<TimeOnly?>(), Arg.Any<DayOfWeek?>(), Arg.Any<int?>(),
            Arg.Any<int?>(), Arg.Any<Guid?>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<DateTime?>(),
            Arg.Any<ScheduledJobKind?>(), Arg.Any<bool?>(),
            personaId: persona.Id, reasoningEffort: ReasoningEffort.High, clearReasoningEffort: false);
    }

    /// <summary>Guid.Empty, not null: null means "leave unchanged", so the default row used to save as a no-op.
    /// True of the PROVIDER row too, which is where that was a live bug.</summary>
    [Fact]
    public async Task ChoosingTheDefaultRows_SendsTheClearSentinel_ForPersonaAndProvider()
    {
        var persona = NewPersona("Analyst");
        var provider = new AiProvider { Name = "Cloud", Endpoint = "https://example.test" };
        var job = NewJob();
        job.PersonaId = persona.Id;
        job.ProviderId = provider.Id;
        job.ReasoningEffort = ReasoningEffort.Minimal;
        var sut = CreateSut(job);
        sut.Personas.GetPersonasAsync().Returns(new[] { persona });
        sut.Providers.GetProvidersAsync().Returns(new[] { provider });
        await sut.Vm.RefreshAsync();
        sut.Vm.SelectedJob = sut.Vm.Jobs[0];
        sut.Vm.StartEditCommand.Execute(null);

        sut.Vm.EditPersona = sut.Vm.PersonaChoices[0];
        sut.Vm.EditProvider = sut.Vm.ProviderChoices[0];
        sut.Vm.EditEffort = sut.Vm.EffortChoices[0];
        await sut.Vm.SaveCommand.ExecuteAsync(null);

        await sut.Jobs.Received(1).UpdateAsync(job.Id, Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<RecurrenceType?>(), Arg.Any<TimeOnly?>(), Arg.Any<DayOfWeek?>(), Arg.Any<int?>(),
            Arg.Any<int?>(), providerId: Guid.Empty, grantedTools: Arg.Any<IReadOnlyCollection<string>>(),
            specificDate: Arg.Any<DateTime?>(), kind: Arg.Any<ScheduledJobKind?>(),
            quietOnSuccess: Arg.Any<bool?>(), personaId: Guid.Empty,
            reasoningEffort: null, clearReasoningEffort: true);
    }

    /// <summary>A pin whose persona is gone must stay visible and survive an unrelated edit — falling back to
    /// the default row would let the next Save destroy it as a change the user never made.</summary>
    [Fact]
    public async Task AnUnresolvablePersonaPin_ShowsAsUnavailable_AndSurvivesTheNextSave()
    {
        var pinned = Guid.NewGuid();
        var job = NewJob();
        job.PersonaId = pinned;
        var sut = CreateSut(job);
        await sut.Vm.RefreshAsync();
        sut.Vm.SelectedJob = sut.Vm.Jobs[0];

        Assert.True(sut.Vm.Jobs[0].HasPersonaPin);
        Assert.Equal("Routines_Field_Persona_Missing", sut.Vm.Jobs[0].PersonaLabel);

        sut.Vm.StartEditCommand.Execute(null);

        var chosen = sut.Vm.EditPersona;
        Assert.NotNull(chosen);
        Assert.Equal(pinned, chosen!.Id);
        Assert.True(chosen.IsUnavailable);
        Assert.Equal("Routines_Field_Persona_Missing", chosen.Name);

        sut.Vm.EditName = "Renamed";
        await sut.Vm.SaveCommand.ExecuteAsync(null);

        await sut.Jobs.Received(1).UpdateAsync(job.Id, name: Arg.Is("Renamed"), query: Arg.Any<string>(),
            recurrence: Arg.Any<RecurrenceType?>(), timeOfDay: Arg.Any<TimeOnly?>(),
            dayOfWeek: Arg.Any<DayOfWeek?>(), dayOfMonth: Arg.Any<int?>(), month: Arg.Any<int?>(),
            providerId: Arg.Any<Guid?>(), grantedTools: Arg.Any<IReadOnlyCollection<string>>(),
            specificDate: Arg.Any<DateTime?>(), kind: Arg.Any<ScheduledJobKind?>(),
            quietOnSuccess: Arg.Any<bool?>(), personaId: pinned,
            reasoningEffort: Arg.Any<ReasoningEffort?>(), clearReasoningEffort: Arg.Any<bool>());
    }

    /// <summary>The synthetic row belongs to one job. Left behind, the next routine's editor offers a stranger's
    /// dead pin as a choice.</summary>
    [Fact]
    public async Task TheUnavailableRow_DoesNotSurviveIntoTheNextEditorSession()
    {
        var job = NewJob();
        job.PersonaId = Guid.NewGuid();
        var sut = CreateSut(job);
        await sut.Vm.RefreshAsync();
        sut.Vm.SelectedJob = sut.Vm.Jobs[0];
        sut.Vm.StartEditCommand.Execute(null);
        Assert.Equal(2, sut.Vm.PersonaChoices.Count);

        sut.Vm.StartCreateCommand.Execute(null);

        Assert.Single(sut.Vm.PersonaChoices);
        Assert.Same(sut.Vm.PersonaChoices[0], sut.Vm.EditPersona);
    }

    /// <summary>The trap in the naive binding: <c>StartCreate</c> leaves the foreign row selected, so a picker
    /// bound to the selection alone would lock a brand-new routine this device is about to own.</summary>
    [Fact]
    public async Task StartCreate_KeepsThePinsEnabled_WhileAForeignOwnedRowIsStillSelected()
    {
        var sut = CreateSut(NewJob());
        sut.Jobs.IsOwnedByThisDeviceAsync(Arg.Any<ScheduledJob>()).Returns(false);
        await sut.Vm.RefreshAsync();
        sut.Vm.SelectedJob = sut.Vm.Jobs[0];

        sut.Vm.StartEditCommand.Execute(null);
        Assert.False(sut.Vm.EditorPinsEnabled);

        sut.Vm.StartCreateCommand.Execute(null);

        Assert.False(sut.Vm.Jobs[0].OwnedByThisDevice);
        Assert.Same(sut.Vm.Jobs[0], sut.Vm.SelectedJob);
        Assert.True(sut.Vm.EditorPinsEnabled);
    }

    /// <summary>The editor is ONE panel for create and edit, so both pins have to reach the create call too.</summary>
    [Fact]
    public async Task CreatingARoutineWithBothPins_ForwardsThemToTheCreateCall()
    {
        var persona = NewPersona("Analyst");
        var sut = CreateSut();
        sut.Personas.GetPersonasAsync().Returns(new[] { persona });
        sut.Jobs.CreateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RecurrenceType>(), Arg.Any<TimeOnly>(),
                Arg.Any<DayOfWeek?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<DateTime?>(), Arg.Any<Guid?>(),
                Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<ScheduledJobKind>(), Arg.Any<bool>(),
                Arg.Any<Guid?>(), Arg.Any<ReasoningEffort?>(), Arg.Any<string?>())
            .Returns(NewJob());
        await sut.Vm.RefreshAsync();

        sut.Vm.StartCreateCommand.Execute(null);
        sut.Vm.EditName = "Monitor";
        sut.Vm.EditQuery = "check the feed";
        sut.Vm.EditPersona = sut.Vm.PersonaChoices[1];
        sut.Vm.EditEffort = Assert.Single(sut.Vm.EffortChoices, c => c.Value == ReasoningEffort.Minimal);

        await sut.Vm.SaveCommand.ExecuteAsync(null);

        await sut.Jobs.Received(1).CreateAsync("Monitor", "check the feed", Arg.Any<RecurrenceType>(),
            Arg.Any<TimeOnly>(), Arg.Any<DayOfWeek?>(), Arg.Any<int?>(), Arg.Any<int?>(),
            Arg.Any<DateTime?>(), Arg.Any<Guid?>(), Arg.Any<IReadOnlyCollection<string>>(),
            Arg.Any<ScheduledJobKind>(), Arg.Any<bool>(),
            personaId: persona.Id, reasoningEffort: ReasoningEffort.Minimal);
    }

    /// <summary>The run substitutes a fallback persona with only a log line, so the detail pane is the one place
    /// a person can find out what a routine is actually pinned to.</summary>
    [Fact]
    public async Task TheDetailPane_NamesThePinnedPersonaAndEffort()
    {
        var persona = NewPersona("Analyst");
        var pinned = NewJob();
        pinned.PersonaId = persona.Id;
        pinned.ReasoningEffort = ReasoningEffort.XHigh;
        var unpinned = NewJob();
        var sut = CreateSut(pinned, unpinned);
        sut.Personas.GetPersonasAsync().Returns(new[] { persona });

        await sut.Vm.RefreshAsync();

        var pinnedRow = Assert.Single(sut.Vm.Jobs, j => j.Id == pinned.Id);
        Assert.True(pinnedRow.HasPersonaPin);
        Assert.Equal("Analyst", pinnedRow.PersonaLabel);
        Assert.True(pinnedRow.HasEffortPin);
        Assert.Equal("Routines_Effort_XHigh", pinnedRow.EffortLabel);

        var plainRow = Assert.Single(sut.Vm.Jobs, j => j.Id == unpinned.Id);
        Assert.False(plainRow.HasPersonaPin);
        Assert.Equal(string.Empty, plainRow.PersonaLabel);
        Assert.False(plainRow.HasEffortPin);
        Assert.Equal(string.Empty, plainRow.EffortLabel);
    }

    /// <summary>LocalizationTests' literal-key regexes cannot see a key built by interpolation, and a missing one
    /// renders as "[Key]" at runtime with nothing else catching it.</summary>
    [Fact]
    public void EveryInterpolatedEditorPinKeyResolvesInAllThreeLocales()
    {
        var keys = new List<string>
        {
            "Routines_Field_Persona_Default",
            "Routines_Field_Persona_Missing",
            "Routines_Field_Effort_Default",
        };
        keys.AddRange(Enum.GetValues<ReasoningEffort>().Select(e => $"Routines_Effort_{e}"));

        var missing = new List<string>();
        foreach (var culture in new[] { CultureInfo.InvariantCulture, new CultureInfo("de"), new CultureInfo("fr") })
        {
            var available = ResourceKeysFor(culture);
            foreach (var key in keys.Where(k => !available.Contains(k)))
                missing.Add($"{culture.Name}: {key}");
        }

        Assert.True(missing.Count == 0,
            $"every routine pin key must exist in all three locales, but these are missing: {string.Join(", ", missing)}");
    }

    private static HashSet<string> ResourceKeysFor(CultureInfo culture)
    {
        var keys = new HashSet<string>();
        var set = ViewStrings.ResourceManager.GetResourceSet(culture, true, false);
        if (set is null) return keys;

        foreach (DictionaryEntry entry in set) keys.Add((string)entry.Key);
        return keys;
    }
}
