using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Logging;
using Pia.Models;
using Pia.Navigation;
using Pia.Services.Interfaces;

namespace Pia.ViewModels;

/// <summary>Scheduled jobs as a top-level master-detail view. Budget and autonomy are absent here by design —
/// resolved from global settings at fire time, since per-job they would cross the sync wire as unvalidated
/// peer-writable input.</summary>
public partial class RoutinesViewModel : UiThreadViewModel, INavigationAware
{
    /// <summary>How many firings the detail pane lists. A glance, not an audit — the chat list is the archive.</summary>
    private const int RecentFiringsShown = 5;

    private readonly IScheduledJobService _jobs;
    private readonly IScheduledJobRunner _runner;
    private readonly IProviderService _providers;
    private readonly IAgentRunService _runs;
    private readonly IDialogService _dialogs;
    private readonly IWindowManagerService _windowManager;
    private readonly ILocalizationService _localization;
    private readonly ILogger<RoutinesViewModel> _logger;

    public ObservableCollection<RoutineRow> Jobs { get; } = [];

    /// <summary>Provider choices for the editor. The leading entry is the "use the default" null row.</summary>
    public ObservableCollection<RoutineProviderChoice> ProviderChoices { get; } = [];

    /// <summary>(value, LOCALIZED label) pairs: a ComboBox bound straight to <c>Enum.GetValues</c> renders the
    /// C# identifier in every locale, which the localization parity tests cannot see.</summary>
    public IReadOnlyList<RoutineKindChoice> JobKinds { get; }

    public IReadOnlyList<RoutineRecurrenceChoice> Recurrences { get; }

    /// <summary>Day and month names come from the culture, not from resx keys — .NET already ships the
    /// translations of "Tuesday" and "March" correctly.</summary>
    public IReadOnlyList<RoutineDayOfWeekChoice> DayOfWeekChoices { get; }

    public IReadOnlyList<int> DayOfMonthChoices { get; } = [.. Enumerable.Range(1, 31)];

    public IReadOnlyList<RoutineMonthChoice> MonthChoices { get; }

    [ObservableProperty]
    private bool _hasJobs;

    /// <summary>True while a load, a save or a manual run is in flight. Every command's CanExecute reads it, so
    /// a double-click cannot fire a job twice.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCreateCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartEditCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleEnabledCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunNowCommand))]
    private bool _isBusy;

    /// <summary>The last thing that happened, localized — including the refusals, which are the interesting
    /// half. Null renders nothing.</summary>
    [ObservableProperty]
    private string? _statusMessage;

    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

    partial void OnStatusMessageChanged(string? value) => OnPropertyChanged(nameof(HasStatusMessage));

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartEditCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleEnabledCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunNowCommand))]
    private RoutineRow? _selectedJob;

    public bool HasSelection => SelectedJob is not null;

    partial void OnSelectedJobChanged(RoutineRow? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(ShowsDetail));
        OnPropertyChanged(nameof(ShowsPlaceholder));

        // A selection change is a different job, so an editor still open on the previous one has to go — saving
        // it afterwards would write this job's fields onto that one's id.
        if (IsEditorOpen) CancelEdit();
    }

    // ---- editor state -------------------------------------------------------------------------------

    [ObservableProperty]
    private bool _isEditorOpen;

    partial void OnIsEditorOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowsDetail));
        OnPropertyChanged(nameof(ShowsPlaceholder));
    }

    /// <summary>The right pane is a three-state machine: the editor, a selected job, or the empty state.</summary>
    public bool ShowsDetail => !IsEditorOpen && SelectedJob is not null;

    public bool ShowsPlaceholder => !IsEditorOpen && SelectedJob is null;

    /// <summary>Null while creating; the row's id while editing. Also what decides which service call the save
    /// takes, so it must be cleared when the editor closes.</summary>
    [ObservableProperty]
    private Guid? _editingJobId;

    [ObservableProperty]
    private string _editName = string.Empty;

    /// <summary>The job's goal — USER CONTENT. Rendered here, never logged above SensitiveDebug.</summary>
    [ObservableProperty]
    private string _editQuery = string.Empty;

    [ObservableProperty]
    private ScheduledJobKind _editKind = ScheduledJobKind.AgentTask;

    [ObservableProperty]
    private RecurrenceType _editRecurrence = RecurrenceType.Daily;

    /// <summary>"HH:mm", parsed on save. A string rather than a TimeOnly because the failure mode of a mistyped
    /// time must be a refused save with a message, not a silently coerced schedule.</summary>
    [ObservableProperty]
    private string _editTimeOfDay = "09:00";

    [ObservableProperty]
    private DayOfWeek _editDayOfWeek = DateTime.Now.DayOfWeek;

    [ObservableProperty]
    private int _editDayOfMonth = DateTime.Now.Day;

    [ObservableProperty]
    private int _editMonth = DateTime.Now.Month;

    /// <summary>Only meaningful for <see cref="RecurrenceType.Once"/>, and it is what makes a settled one-off
    /// re-armable at all.</summary>
    [ObservableProperty]
    private DateTime? _editSpecificDate;

    [ObservableProperty]
    private RoutineProviderChoice? _editProvider;

    /// <summary>Comma-separated write-tool names. The stored field is a list; this is its flat form.</summary>
    [ObservableProperty]
    private string _editGrantedTools = string.Empty;

    /// <summary>Suppresses the SUCCESS notification this job would raise. Failures still notify, and the result
    /// chat is written either way. Device-local, so it is not part of what syncs with the job.</summary>
    [ObservableProperty]
    private bool _editQuietOnSuccess;

    public bool EditorWantsSpecificDate => EditRecurrence == RecurrenceType.Once;
    public bool EditorWantsDayOfWeek => EditRecurrence == RecurrenceType.Weekly;
    public bool EditorWantsDayOfMonth => EditRecurrence is RecurrenceType.Monthly or RecurrenceType.Yearly;
    public bool EditorWantsMonth => EditRecurrence == RecurrenceType.Yearly;

    partial void OnEditRecurrenceChanged(RecurrenceType value)
    {
        OnPropertyChanged(nameof(EditorWantsSpecificDate));
        OnPropertyChanged(nameof(EditorWantsDayOfWeek));
        OnPropertyChanged(nameof(EditorWantsDayOfMonth));
        OnPropertyChanged(nameof(EditorWantsMonth));
    }

    public RoutinesViewModel(
        IScheduledJobService jobs,
        IScheduledJobRunner runner,
        IProviderService providers,
        IAgentRunService runs,
        IDialogService dialogs,
        IWindowManagerService windowManager,
        ILocalizationService localization,
        ILogger<RoutinesViewModel> logger)
    {
        _jobs = jobs;
        _runner = runner;
        _providers = providers;
        _runs = runs;
        _dialogs = dialogs;
        _windowManager = windowManager;
        _localization = localization;
        _logger = logger;

        JobKinds = [.. Enum.GetValues<ScheduledJobKind>()
            .Select(k => new RoutineKindChoice(k, _localization[$"Settings_ScheduledJobs_Kind_{k}"]))];
        Recurrences = [.. Enum.GetValues<RecurrenceType>()
            .Select(r => new RoutineRecurrenceChoice(
                r, _localization[$"Settings_ScheduledJobs_Recurrence_{r}"]))];

        var names = CultureInfo.CurrentCulture.DateTimeFormat;
        DayOfWeekChoices = [.. Enum.GetValues<DayOfWeek>()
            .Select(d => new RoutineDayOfWeekChoice(d, names.GetDayName(d)))];
        MonthChoices = [.. Enumerable.Range(1, 12)
            .Select(m => new RoutineMonthChoice(m, names.GetMonthName(m)))];
    }

    public void OnNavigatedTo(object? parameter) { }

    public void OnNavigatedFrom() { }

    /// <summary>Reads the database, so it runs on navigation rather than in the constructor — a ctor that awaits
    /// is how a view becomes a startup cost.</summary>
    public async Task OnNavigatedToAsync(object? parameter) => await RefreshAsync();

    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var jobs = await _jobs.GetAllAsync();
            var providers = await _providers.GetProvidersAsync();

            var rows = new List<RoutineRow>(jobs.Count);
            foreach (var job in jobs)
            {
                var providerName = job.ProviderId is { } id
                    ? providers.FirstOrDefault(p => p.Id == id)?.Name
                    : null;
                rows.Add(BuildRow(job, providerName, await _jobs.IsOwnedByThisDeviceAsync(job),
                    await LoadRecentFiringsAsync(job.Id)));
            }

            PostOrRun(() =>
            {
                // Rows are rebuilt wholesale, so the selection has to be re-resolved by id or every refresh
                // would silently empty the detail pane the user is reading.
                var selectedId = SelectedJob?.Id;

                Jobs.Clear();
                foreach (var row in rows) Jobs.Add(row);
                HasJobs = Jobs.Count > 0;

                SelectedJob = selectedId is { } id
                    ? Jobs.FirstOrDefault(j => j.Id == id)
                    : null;

                ProviderChoices.Clear();
                ProviderChoices.Add(new RoutineProviderChoice(
                    null, _localization["Settings_ScheduledJobs_Provider_Default"]));
                foreach (var provider in providers)
                    ProviderChoices.Add(new RoutineProviderChoice(provider.Id, provider.Name));
            });
        }
        catch (Exception ex)
        {
            // A view that cannot read its own table must say so rather than render an empty list, which would
            // read as "you have no routines".
            _logger.LogWarning(ex, "Could not load scheduled jobs");
            PostOrRun(() => StatusMessage = _localization["Settings_ScheduledJobs_LoadFailed"]);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Failure-isolated on purpose: history is decoration, and the list must still render without
    /// it.</summary>
    private async Task<IReadOnlyList<ScheduledFiringOutcome>> LoadRecentFiringsAsync(Guid jobId)
    {
        try
        {
            return await _runs.GetFiringsForTriggerAsync(jobId, RecentFiringsShown);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the run history for scheduled job {Id}", jobId);
            return [];
        }
    }

    /// <summary><c>Cancelled</c> counts with <c>Failed</c>: this line answers "is this job working?" and a
    /// cancelled firing did not deliver either. The detail list keeps them apart.</summary>
    private string BuildRecentRunsSummary(IReadOnlyList<ScheduledFiringOutcome> firings)
    {
        if (firings.Count == 0) return string.Empty;
        var ok = firings.Count(f => f.State == AgentRunState.Completed);
        return _localization.Format(
            "Settings_ScheduledJobs_RecentRuns", firings.Count, ok, firings.Count - ok);
    }

    private RoutineRunRow BuildRunRow(ScheduledFiringOutcome firing) => new()
    {
        // The record carries UTC deliberately, so nothing compares it to a local column by accident; every time
        // shown on this surface is local.
        SettledAt = firing.SettledAtUtc.ToLocalTime(),
        Succeeded = firing.State == AgentRunState.Completed,
        StateLabel = Enum.IsDefined(firing.State)
            ? _localization[$"Settings_ScheduledJobs_RunState_{firing.State}"]
            : ((int)firing.State).ToString(),
        ChatId = firing.ChatId,
    };

    private RoutineRow BuildRow(ScheduledJob job, string? providerName, bool ownedHere,
        IReadOnlyList<ScheduledFiringOutcome> recentFirings)
    {
        // Not defensive padding: ScheduledJobStatus crosses the sync wire as an int that SyncMapper casts back
        // with no Enum.IsDefined check, and coercing a newer peer's ordinal to Active is what must never happen.
        var known = Enum.IsDefined(job.Status);
        var statusLabel = known
            ? _localization[$"Settings_ScheduledJobs_Status_{job.Status}"]
            : _localization.Format("Settings_ScheduledJobs_Status_Unknown", (int)job.Status);

        return new RoutineRow
        {
            Id = job.Id,
            Name = job.Name,
            Query = job.Query,
            Kind = job.Kind,
            KindLabel = _localization[$"Settings_ScheduledJobs_Kind_{job.Kind}"],
            Recurrence = job.Recurrence,
            RecurrenceLabel = _localization[$"Settings_ScheduledJobs_Recurrence_{job.Recurrence}"],
            TimeOfDay = job.TimeOfDay,
            DayOfWeek = job.DayOfWeek,
            DayOfMonth = job.DayOfMonth,
            Month = job.Month,
            SpecificDate = job.SpecificDate,
            NextFireAt = job.NextFireAt,
            Status = job.Status,
            StatusLabel = statusLabel,
            StatusIsKnown = known,
            IsEnabled = known && job.Status == ScheduledJobStatus.Active,
            ToggleLabel = known && job.Status == ScheduledJobStatus.Active
                ? _localization["Settings_ScheduledJobs_Disable"]
                : _localization["Settings_ScheduledJobs_Enable"],
            LastFiredAt = job.LastFiredAt,
            LastResultEntryId = job.LastResultEntryId,
            ConsecutiveFailures = job.ConsecutiveFailures,
            ProviderId = job.ProviderId,
            ProviderName = providerName,
            GrantedTools = string.Join(", ", job.GrantedTools),
            QuietOnSuccess = job.QuietOnSuccess,
            RecentRunsSummary = BuildRecentRunsSummary(recentFirings),
            RecentRuns = [.. recentFirings.Select(BuildRunRow)],
            OwnedByThisDevice = ownedHere,
        };
    }

    private bool CanWork() => !IsBusy;

    private bool CanActOnSelection() => !IsBusy && SelectedJob is not null;

    [RelayCommand(CanExecute = nameof(CanWork))]
    private void StartCreate()
    {
        var now = DateTime.Now;
        EditingJobId = null;
        EditName = string.Empty;
        EditQuery = string.Empty;
        EditKind = ScheduledJobKind.AgentTask;
        EditRecurrence = RecurrenceType.Daily;
        EditTimeOfDay = "09:00";
        EditDayOfWeek = now.DayOfWeek;
        EditDayOfMonth = now.Day;
        EditMonth = now.Month;
        EditSpecificDate = null;
        EditGrantedTools = string.Empty;
        EditQuietOnSuccess = false;
        EditProvider = ProviderChoices.FirstOrDefault();
        StatusMessage = null;
        IsEditorOpen = true;
    }

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private void StartEdit()
    {
        if (SelectedJob is not { } row) return;

        EditingJobId = row.Id;
        EditName = row.Name;
        EditQuery = row.Query;
        EditKind = row.Kind;
        EditRecurrence = row.Recurrence;
        EditTimeOfDay = row.TimeOfDay.ToString("HH\\:mm");
        // A job predating the day pickers has no stored day; NextFireAt is the day it actually fires on, which
        // is the honest thing to show rather than today.
        EditDayOfWeek = row.DayOfWeek ?? row.NextFireAt.DayOfWeek;
        EditDayOfMonth = row.DayOfMonth ?? row.NextFireAt.Day;
        EditMonth = row.Month ?? row.NextFireAt.Month;
        EditSpecificDate = row.SpecificDate;
        EditGrantedTools = row.GrantedTools;
        EditQuietOnSuccess = row.QuietOnSuccess;
        EditProvider = ProviderChoices.FirstOrDefault(p => p.Id == row.ProviderId)
                       ?? ProviderChoices.FirstOrDefault();
        StatusMessage = null;
        IsEditorOpen = true;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditorOpen = false;
        EditingJobId = null;
    }

    [RelayCommand(CanExecute = nameof(CanWork))]
    private async Task SaveAsync()
    {
        if (IsBusy) return;

        if (string.IsNullOrWhiteSpace(EditName) || string.IsNullOrWhiteSpace(EditQuery))
        {
            StatusMessage = _localization["Settings_ScheduledJobs_Validation_NameAndGoal"];
            return;
        }

        if (!TimeOnly.TryParseExact(EditTimeOfDay.Trim(), "HH\\:mm", out var timeOfDay))
        {
            StatusMessage = _localization["Settings_ScheduledJobs_Validation_Time"];
            return;
        }

        var grants = EditGrantedTools
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Each field is carried only for the recurrences that read it; a value left behind on a job switched to
        // another recurrence is inert, and is what lets the editor remember the old day if the user switches back.
        var specificDate = EditRecurrence == RecurrenceType.Once ? EditSpecificDate : null;
        var dayOfWeek = EditorWantsDayOfWeek ? EditDayOfWeek : (DayOfWeek?)null;
        var dayOfMonth = EditorWantsDayOfMonth ? EditDayOfMonth : (int?)null;
        var month = EditorWantsMonth ? EditMonth : (int?)null;

        IsBusy = true;
        try
        {
            if (EditingJobId is { } id)
            {
                await _jobs.UpdateAsync(id,
                    name: EditName.Trim(),
                    query: EditQuery.Trim(),
                    recurrence: EditRecurrence,
                    timeOfDay: timeOfDay,
                    dayOfWeek: dayOfWeek,
                    dayOfMonth: dayOfMonth,
                    month: month,
                    providerId: EditProvider?.Id,
                    grantedTools: grants,
                    specificDate: specificDate,
                    kind: EditKind,
                    quietOnSuccess: EditQuietOnSuccess);

                _logger.LogInformation("Updated scheduled job {Id} from the routines view", id);
                _logger.SensitiveDebug("Updated scheduled job {Id} name: {Name} goal: {Goal}",
                    id, EditName, EditQuery);
            }
            else
            {
                var created = await _jobs.CreateAsync(EditName.Trim(), EditQuery.Trim(), EditRecurrence,
                    timeOfDay, dayOfWeek: dayOfWeek, dayOfMonth: dayOfMonth, month: month,
                    specificDate: specificDate, providerId: EditProvider?.Id,
                    grantedTools: grants, kind: EditKind, quietOnSuccess: EditQuietOnSuccess);

                _logger.LogInformation("Created scheduled job {Id} from the routines view ({Kind})",
                    created.Id, EditKind);
                _logger.SensitiveDebug("Created scheduled job {Id} name: {Name} goal: {Goal}",
                    created.Id, EditName, EditQuery);
                EditingJobId = created.Id;
            }

            IsEditorOpen = false;
            StatusMessage = null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save scheduled job");
            StatusMessage = _localization["Settings_ScheduledJobs_SaveFailed"];
            // Returning keeps the editing id and the user's input: the tail below clears the id, and its refresh
            // re-resolves the selection to a fresh row instance, which cancels the editor.
            return;
        }
        finally
        {
            IsBusy = false;
        }

        // Select what was just saved, so the detail pane shows it rather than reverting to the empty state.
        var savedId = EditingJobId;
        EditingJobId = null;
        await RefreshAsync();
        if (savedId is { } saved)
            SelectedJob = Jobs.FirstOrDefault(j => j.Id == saved) ?? SelectedJob;
    }

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private async Task ToggleEnabledAsync()
    {
        if (SelectedJob is not { } row || IsBusy) return;

        // An unknown status is inert on purpose: this build cannot say what enabling or disabling it would mean,
        // and guessing is how an unrecognised state becomes a fired job.
        if (!row.StatusIsKnown)
        {
            StatusMessage = _localization["Settings_ScheduledJobs_UnknownStatusInert"];
            return;
        }

        IsBusy = true;
        try
        {
            if (row.IsEnabled) await _jobs.DisableAsync(row.Id);
            else await _jobs.EnableAsync(row.Id);
            _logger.LogInformation("Toggled scheduled job {Id} to {State}", row.Id, !row.IsEnabled);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not toggle scheduled job {Id}", row.Id);
            StatusMessage = _localization["Settings_ScheduledJobs_SaveFailed"];
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private async Task DeleteAsync()
    {
        if (SelectedJob is not { } row || IsBusy) return;

        var confirmed = await _dialogs.ShowConfirmationDialogAsync(
            _localization["Routines_Delete_Title"],
            _localization.Format("Routines_Delete_Confirm", row.Name));
        if (!confirmed) return;

        IsBusy = true;
        try
        {
            await _jobs.DeleteAsync(row.Id);
            _logger.LogInformation("Deleted scheduled job {Id} from the routines view", row.Id);
            SelectedJob = null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete scheduled job {Id}", row.Id);
            StatusMessage = _localization["Settings_ScheduledJobs_SaveFailed"];
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync();
    }

    /// <summary>DISPATCHES, like the scheduler's own tick does, so the status message says "started": the run
    /// outlives this method, and claiming it had finished would be a lie.</summary>
    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private async Task RunNowAsync()
    {
        if (SelectedJob is not { } row || IsBusy) return;

        IsBusy = true;
        StatusMessage = _localization["Settings_ScheduledJobs_Running"];
        try
        {
            var result = await _runner.RunNowAsync(row.Id);
            StatusMessage = result switch
            {
                ScheduledJobRunNowResult.Dispatched => _localization["Settings_ScheduledJobs_RunStarted"],
                ScheduledJobRunNowResult.NotOwner => _localization["Settings_ScheduledJobs_RunNotOwner"],
                ScheduledJobRunNowResult.AlreadyRunning => _localization["Settings_ScheduledJobs_RunAlreadyRunning"],
                _ => _localization["Settings_ScheduledJobs_RunNotFound"],
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Manual run failed for scheduled job {Id}", row.Id);
            StatusMessage = _localization["Settings_ScheduledJobs_RunFailed"];
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync();
    }

    /// <summary>Opens the chat a firing produced — the same destination the success toast's button routes to.</summary>
    [RelayCommand]
    private void OpenRunChat(RoutineRunRow? run)
    {
        if (run is null || run.ChatId == Guid.Empty) return;

        try
        {
            _windowManager.ShowAssistantChat(run.ChatId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open the chat for a scheduled firing");
        }
    }
}

/// <summary>One provider the editor may pin a job to; <see cref="Id"/> null is "use the default".</summary>
public sealed record RoutineProviderChoice(Guid? Id, string Name);

/// <summary>A job type and the label a user should see for it.</summary>
public sealed record RoutineKindChoice(ScheduledJobKind Value, string Label);

/// <summary>A repeat interval and the label a user should see for it.</summary>
public sealed record RoutineRecurrenceChoice(RecurrenceType Value, string Label);

/// <summary>A weekday and the label a user should see for it.</summary>
public sealed record RoutineDayOfWeekChoice(DayOfWeek Value, string Label);

/// <summary>A month number and the label a user should see for it.</summary>
public sealed record RoutineMonthChoice(int Value, string Label);

/// <summary>One settled firing, as the detail pane lists it.</summary>
public sealed class RoutineRunRow
{
    public required DateTime SettledAt { get; init; }
    public required bool Succeeded { get; init; }
    public required string StateLabel { get; init; }
    public Guid ChatId { get; init; }

    /// <summary>Drives the open-chat affordance: a failed firing produced no chat to point at.</summary>
    public bool HasChat => ChatId != Guid.Empty;
}

/// <summary>A display projection of one <see cref="ScheduledJob"/>, labels included, so no converter has to
/// guess what an unrecognised status means.</summary>
public sealed class RoutineRow
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }

    /// <summary>The goal. User content: rendered, never logged above <c>SensitiveDebug</c>.</summary>
    public required string Query { get; init; }

    public required ScheduledJobKind Kind { get; init; }
    public required string KindLabel { get; init; }
    public required RecurrenceType Recurrence { get; init; }
    public required string RecurrenceLabel { get; init; }
    public required TimeOnly TimeOfDay { get; init; }
    public DayOfWeek? DayOfWeek { get; init; }
    public int? DayOfMonth { get; init; }
    public int? Month { get; init; }
    public DateTime? SpecificDate { get; init; }
    public required DateTime NextFireAt { get; init; }

    public required ScheduledJobStatus Status { get; init; }
    public required string StatusLabel { get; init; }

    /// <summary>False when the persisted ordinal is one this build does not define. The row stays visible and
    /// deletable, and is inert for everything else.</summary>
    public required bool StatusIsKnown { get; init; }

    public required bool IsEnabled { get; init; }

    /// <summary>"Enable" or "Disable", resolved once where the localizer is.</summary>
    public required string ToggleLabel { get; init; }

    public DateTime? LastFiredAt { get; init; }
    public Guid? LastResultEntryId { get; init; }
    public int ConsecutiveFailures { get; init; }
    public bool HasLastResult => LastResultEntryId is not null;

    public Guid? ProviderId { get; init; }
    public string? ProviderName { get; init; }
    public required string GrantedTools { get; init; }
    public bool HasGrantedTools => !string.IsNullOrEmpty(GrantedTools);

    /// <summary>This job's successes are not announced (device-local; failures still are).</summary>
    public required bool QuietOnSuccess { get; init; }

    /// <summary>"N runs: X ok, Y failed"; empty when none are recorded.</summary>
    public required string RecentRunsSummary { get; init; }

    /// <summary>The firings themselves, newest first — a list in the detail pane, not a tooltip.</summary>
    public required IReadOnlyList<RoutineRunRow> RecentRuns { get; init; }

    public bool HasRecentRuns => RecentRuns.Count > 0;

    /// <summary>Only the owner device may advance a job, so "run now" is unavailable elsewhere.</summary>
    public required bool OwnedByThisDevice { get; init; }

    /// <summary>Two different refusals: another device owns the schedule, or this build cannot judge the state.
    /// A disabled button is only the courtesy half — the service re-checks ownership itself.</summary>
    public bool CanRunNow => OwnedByThisDevice && StatusIsKnown;
}
