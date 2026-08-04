using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Logging;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.ViewModels;

/// <summary>
/// Batch 09: the scheduled-jobs management surface, as a section of the Assistant settings page.
/// <para>
/// Until this existed, a scheduled job could only be created or edited by ASKING THE ASSISTANT
/// (<c>ScheduledJobToolHandler</c> exposes create/update/delete and not even enable), and two capabilities had
/// no surface at all: re-arming a settled one-off, and firing a job outside its schedule. Both are wired here
/// against the service work in the same batch — see <c>09-scheduler-ui.impl.md</c>.
/// </para>
/// <para>
/// <b>Budget and autonomy policy are deliberately NOT per-job (D1).</b> They are resolved from global settings
/// at fire time, and this surface links to them rather than duplicating them: a per-job autonomy class list
/// becomes peer-writable unvalidated input the moment it crosses the sync wire, which is a batch of its own.
/// </para>
/// </summary>
public partial class ScheduledJobsSettingsViewModel : UiThreadViewModel
{
    private readonly IScheduledJobService _jobs;
    private readonly IScheduledJobRunner _runner;
    private readonly IProviderService _providers;
    private readonly ILocalizationService _localization;
    private readonly ILogger<SettingsViewModel> _logger;

    /// <summary>
    /// T2-18: the per-job run history comes from the RUN rows (`AgentRuns.TriggerRef`), not from a second
    /// store. TRAILING and DEFAULTED like every dependency this surface has gained: null ⇒ no history line,
    /// which is the pre-T2-18 row exactly, and which is what keeps the hand-written test constructions valid.
    /// </summary>
    private readonly IAgentRunService? _runs;

    public ObservableCollection<ScheduledJobRow> Jobs { get; } = [];

    /// <summary>Provider choices for the editor. The leading entry is the "use the default" null row.</summary>
    public ObservableCollection<ScheduledJobProviderChoice> ProviderChoices { get; } = [];

    /// <summary>
    /// The editor's type and repeat choices as (value, LOCALIZED label) pairs — not the bare enums.
    /// Binding a ComboBox straight to <c>Enum.GetValues</c> renders the C# identifier ("AgentTask",
    /// "Weekly") in every locale, which passes <c>LocalizationTests</c> (the keys exist, with parity) while
    /// showing English to a German user. The XAML pairs these with <c>SelectedValuePath</c> so the VM keeps
    /// holding the enum. Same shape as <see cref="ScheduledJobProviderChoice"/>, and no converter.
    /// </summary>
    public IReadOnlyList<ScheduledJobKindChoice> JobKinds { get; }

    public IReadOnlyList<ScheduledJobRecurrenceChoice> Recurrences { get; }

    [ObservableProperty]
    private bool _hasJobs;

    /// <summary>True while a load, a save or a manual run is in flight; every command is gated on it so a
    /// double-click cannot fire a job twice.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>The last thing that happened, localized — including the refusals, which are the interesting
    /// half. Null renders nothing.</summary>
    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>Drives the message line's visibility without a null-to-visibility converter.</summary>
    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

    partial void OnStatusMessageChanged(string? value) => OnPropertyChanged(nameof(HasStatusMessage));

    // ---- editor state -------------------------------------------------------------------------------

    [ObservableProperty]
    private bool _isEditorOpen;

    /// <summary>Null while creating; the row's id while editing. Also what decides which service call the
    /// save takes, so it must be cleared when the editor closes.</summary>
    [ObservableProperty]
    private Guid? _editingJobId;

    [ObservableProperty]
    private string _editName = string.Empty;

    /// <summary>The job's goal — USER CONTENT. It is rendered here and never logged above SensitiveDebug.</summary>
    [ObservableProperty]
    private string _editQuery = string.Empty;

    [ObservableProperty]
    private ScheduledJobKind _editKind = ScheduledJobKind.AgentTask;

    [ObservableProperty]
    private RecurrenceType _editRecurrence = RecurrenceType.Daily;

    /// <summary>"HH:mm", parsed on save. A string rather than a TimeOnly because the failure mode of a
    /// mistyped time must be a refused save with a message, not a silently coerced schedule.</summary>
    [ObservableProperty]
    private string _editTimeOfDay = "09:00";

    /// <summary>Only meaningful for <see cref="RecurrenceType.Once"/> — and it is what makes a settled
    /// one-off re-armable at all, so it is the one editor field with a service change behind it.</summary>
    [ObservableProperty]
    private DateTime? _editSpecificDate;

    [ObservableProperty]
    private ScheduledJobProviderChoice? _editProvider;

    /// <summary>Comma-separated write-tool names. The stored field is a list; this is its flat form.</summary>
    [ObservableProperty]
    private string _editGrantedTools = string.Empty;

    /// <summary>
    /// T2-18 quiet mode: suppress the SUCCESS notification (Flow card + Windows toast) this job would raise.
    /// Failures still notify — see <c>ScheduledJob.QuietOnSuccess</c>. Device-local, so it is not part of what
    /// syncs with the job.
    /// </summary>
    [ObservableProperty]
    private bool _editQuietOnSuccess;

    /// <summary>Drives the date row's visibility: a specific date means nothing for a recurring job.</summary>
    public bool EditorWantsSpecificDate => EditRecurrence == RecurrenceType.Once;

    partial void OnEditRecurrenceChanged(RecurrenceType value) =>
        OnPropertyChanged(nameof(EditorWantsSpecificDate));

    public ScheduledJobsSettingsViewModel(
        IScheduledJobService jobs,
        IScheduledJobRunner runner,
        IProviderService providers,
        ILocalizationService localization,
        ILogger<SettingsViewModel> logger,
        IAgentRunService? runs = null)
    {
        _runs = runs;
        _jobs = jobs;
        _runner = runner;
        _providers = providers;
        _localization = localization;
        _logger = logger;

        JobKinds = Enum.GetValues<ScheduledJobKind>()
            .Select(k => new ScheduledJobKindChoice(k, _localization[$"Settings_ScheduledJobs_Kind_{k}"]))
            .ToList();
        Recurrences = Enum.GetValues<RecurrenceType>()
            .Select(r => new ScheduledJobRecurrenceChoice(
                r, _localization[$"Settings_ScheduledJobs_Recurrence_{r}"]))
            .ToList();
    }

    /// <summary>
    /// Loads jobs and provider choices. Called by the settings host rather than from the constructor: this
    /// reads the database, and a ctor that awaits is how a settings page becomes a startup cost.
    /// </summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var jobs = await _jobs.GetAllAsync();
            var providers = await _providers.GetProvidersAsync();

            var rows = new List<ScheduledJobRow>(jobs.Count);
            foreach (var job in jobs)
            {
                var providerName = job.ProviderId is { } id
                    ? providers.FirstOrDefault(p => p.Id == id)?.Name
                    : null;
                rows.Add(BuildRow(job, providerName, await _jobs.IsOwnedByThisDeviceAsync(job.Id),
                    await LoadRecentFiringsAsync(job.Id)));
            }

            PostOrRun(() =>
            {
                Jobs.Clear();
                foreach (var row in rows) Jobs.Add(row);
                HasJobs = Jobs.Count > 0;

                ProviderChoices.Clear();
                ProviderChoices.Add(new ScheduledJobProviderChoice(
                    null, _localization["Settings_ScheduledJobs_Provider_Default"]));
                foreach (var provider in providers)
                    ProviderChoices.Add(new ScheduledJobProviderChoice(provider.Id, provider.Name));
            });
        }
        catch (Exception ex)
        {
            // A settings section that cannot read its own table must say so rather than render an empty list,
            // which would read as "you have no scheduled jobs" — the same distinction Batch 03's trace panel
            // draws between "nothing recorded" and "could not be read".
            _logger.LogWarning(ex, "Could not load scheduled jobs");
            PostOrRun(() => StatusMessage = _localization["Settings_ScheduledJobs_LoadFailed"]);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// T2-18: this job's recent settled firings, or an empty list when there is no run service (the
    /// pre-T2-18 shape) or the read fails. Failure-isolated on purpose: a history line is decoration, and the
    /// jobs list must still render without it — the same rule the load's own catch states one level up.
    /// <para>
    /// It joins the load's KNOWN N+1 (one `IsOwnedByThisDeviceAsync` per job) rather than introducing one:
    /// the query seeks `IX_AgentRuns_TriggerRef` and returns at most <see cref="RecentFiringsShown"/> rows.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<ScheduledFiringOutcome>> LoadRecentFiringsAsync(Guid jobId)
    {
        if (_runs is null) return [];
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

    /// <summary>How many firings the history line covers. Five is a glance, not an audit — the run panel and the
    /// chat list are where a full history belongs.</summary>
    private const int RecentFiringsShown = 5;

    /// <summary>
    /// T2-18: the one-line history a person reads at a glance — "3 ok, 1 failed" over the last few firings.
    /// Empty string when nothing is recorded, so the row renders no line rather than a zero.
    /// <para>
    /// <c>Completed</c> is the only success; <c>Failed</c> and <c>Cancelled</c> are counted together as "failed"
    /// deliberately, because this line answers "is this job working?" and a cancelled firing did not deliver
    /// either. The DETAIL below keeps them apart for whoever wants the difference.
    /// </para>
    /// </summary>
    private string BuildRecentRunsSummary(IReadOnlyList<ScheduledFiringOutcome> firings)
    {
        if (firings.Count == 0) return string.Empty;
        var ok = firings.Count(f => f.State == AgentRunState.Completed);
        return _localization.Format(
            "Settings_ScheduledJobs_RecentRuns", firings.Count, ok, firings.Count - ok);
    }

    /// <summary>
    /// The list itself, one line per firing, newest first — the tooltip behind the summary. Instants are shown
    /// LOCAL: the record carries UTC (deliberately, so nothing compares it to a local column by accident) and
    /// every other time on this surface is local.
    /// </summary>
    private string BuildRecentRunsDetail(IReadOnlyList<ScheduledFiringOutcome> firings)
    {
        if (firings.Count == 0) return string.Empty;
        return string.Join(Environment.NewLine, firings.Select(f =>
        {
            var state = Enum.IsDefined(f.State)
                ? _localization[$"Settings_ScheduledJobs_RunState_{f.State}"]
                : ((int)f.State).ToString();
            return $"{f.SettledAtUtc.ToLocalTime():g} — {state}";
        }));
    }

    private ScheduledJobRow BuildRow(ScheduledJob job, string? providerName, bool ownedHere,
        IReadOnlyList<ScheduledFiringOutcome> recentFirings)
    {
        // An UNKNOWN status is a real possibility, not defensive padding: ScheduledJobStatus crosses the sync
        // wire as an int and SyncMapper casts it back with no Enum.IsDefined check, so a newer peer's ordinal
        // arrives here as an undefined value. The enum's own doc requires any UI to tolerate it. Rendering it
        // as its number and treating the row as inert is the honest reading — the one thing that must never
        // happen is coercing it to Active, which would offer to run a job whose state this build cannot judge.
        var known = Enum.IsDefined(job.Status);
        var statusLabel = known
            ? _localization[$"Settings_ScheduledJobs_Status_{job.Status}"]
            : _localization.Format("Settings_ScheduledJobs_Status_Unknown", (int)job.Status);

        return new ScheduledJobRow
        {
            Id = job.Id,
            Name = job.Name,
            Query = job.Query,
            Kind = job.Kind,
            KindLabel = _localization[$"Settings_ScheduledJobs_Kind_{job.Kind}"],
            Recurrence = job.Recurrence,
            RecurrenceLabel = _localization[$"Settings_ScheduledJobs_Recurrence_{job.Recurrence}"],
            TimeOfDay = job.TimeOfDay,
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
            RecentRunsDetail = BuildRecentRunsDetail(recentFirings),
            OwnedByThisDevice = ownedHere,
        };
    }

    [RelayCommand]
    private void StartCreate()
    {
        EditingJobId = null;
        EditName = string.Empty;
        EditQuery = string.Empty;
        EditKind = ScheduledJobKind.AgentTask;
        EditRecurrence = RecurrenceType.Daily;
        EditTimeOfDay = "09:00";
        EditSpecificDate = null;
        EditGrantedTools = string.Empty;
        EditQuietOnSuccess = false;
        EditProvider = ProviderChoices.FirstOrDefault();
        StatusMessage = null;
        IsEditorOpen = true;
    }

    [RelayCommand]
    private void StartEdit(ScheduledJobRow? row)
    {
        if (row is null) return;

        EditingJobId = row.Id;
        EditName = row.Name;
        EditQuery = row.Query;
        EditKind = row.Kind;
        EditRecurrence = row.Recurrence;
        EditTimeOfDay = row.TimeOfDay.ToString("HH\\:mm");
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

    [RelayCommand]
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

        // Only carried for a one-off. Sending a date for a recurring job would persist a field the recurrence
        // calculator ignores, and it would then reappear in the editor if the job were later made a one-off.
        var specificDate = EditRecurrence == RecurrenceType.Once ? EditSpecificDate : null;

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
                    providerId: EditProvider?.Id,
                    grantedTools: grants,
                    specificDate: specificDate,
                    kind: EditKind,
                    quietOnSuccess: EditQuietOnSuccess);

                _logger.LogInformation("Updated scheduled job {Id} from settings", id);
                _logger.SensitiveDebug("Updated scheduled job {Id} name: {Name} goal: {Goal}",
                    id, EditName, EditQuery);
            }
            else
            {
                var created = await _jobs.CreateAsync(EditName.Trim(), EditQuery.Trim(), EditRecurrence,
                    timeOfDay, specificDate: specificDate, providerId: EditProvider?.Id,
                    grantedTools: grants, kind: EditKind);

                _logger.LogInformation("Created scheduled job {Id} from settings ({Kind})", created.Id, EditKind);
                _logger.SensitiveDebug("Created scheduled job {Id} name: {Name} goal: {Goal}",
                    created.Id, EditName, EditQuery);
            }

            IsEditorOpen = false;
            EditingJobId = null;
            StatusMessage = null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save scheduled job");
            StatusMessage = _localization["Settings_ScheduledJobs_SaveFailed"];
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync();
    }

    [RelayCommand]
    private async Task ToggleEnabledAsync(ScheduledJobRow? row)
    {
        if (row is null || IsBusy) return;

        // An unknown status is inert on purpose: this build cannot say what enabling or disabling it would
        // mean, and guessing is how an unrecognised state becomes a fired job.
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

    [RelayCommand]
    private async Task DeleteAsync(ScheduledJobRow? row)
    {
        if (row is null || IsBusy) return;

        IsBusy = true;
        try
        {
            await _jobs.DeleteAsync(row.Id);
            _logger.LogInformation("Deleted scheduled job {Id} from settings", row.Id);
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

    /// <summary>
    /// Fires a job outside its schedule. DISPATCHES it, like the scheduler's own tick does, so the button frees
    /// up as soon as the run has started rather than sitting busy for its whole wall clock — the run's result
    /// arrives in the chat it creates. The status message says "started" for that reason; claiming the job had
    /// finished would be a lie the moment the run outlived this method.
    /// </summary>
    [RelayCommand]
    private async Task RunNowAsync(ScheduledJobRow? row)
    {
        if (row is null || IsBusy) return;

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
}

/// <summary>One provider the editor may pin a job to; <see cref="Id"/> null is "use the default".</summary>
public sealed record ScheduledJobProviderChoice(Guid? Id, string Name);

/// <summary>A job type and the label a user should see for it.</summary>
public sealed record ScheduledJobKindChoice(ScheduledJobKind Value, string Label);

/// <summary>A repeat interval and the label a user should see for it.</summary>
public sealed record ScheduledJobRecurrenceChoice(RecurrenceType Value, string Label);

/// <summary>
/// A display projection of one <see cref="ScheduledJob"/>. Everything the row template needs is precomputed
/// here — including the localized labels — so the template carries no converter that would have to guess what
/// an unrecognised status means.
/// </summary>
public sealed class ScheduledJobRow
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
    public DateTime? SpecificDate { get; init; }
    public required DateTime NextFireAt { get; init; }

    public required ScheduledJobStatus Status { get; init; }
    public required string StatusLabel { get; init; }

    /// <summary>False when the persisted ordinal is one this build does not define — see
    /// <c>BuildRow</c>. The row stays visible and deletable, and is inert for everything else.</summary>
    public required bool StatusIsKnown { get; init; }

    public required bool IsEnabled { get; init; }

    /// <summary>"Enable" or "Disable", resolved once where the localizer is, so the row template needs no
    /// converter that would have to decide what an unrecognised status means.</summary>
    public required string ToggleLabel { get; init; }

    public DateTime? LastFiredAt { get; init; }
    public Guid? LastResultEntryId { get; init; }
    public int ConsecutiveFailures { get; init; }
    public bool HasLastResult => LastResultEntryId is not null;

    public Guid? ProviderId { get; init; }
    public string? ProviderName { get; init; }
    public required string GrantedTools { get; init; }

    /// <summary>T2-18: this job's successes are not announced (device-local; failures still are).</summary>
    public required bool QuietOnSuccess { get; init; }

    /// <summary>T2-18: "N runs: X ok, Y failed" over the last few firings; empty when none are recorded.</summary>
    public required string RecentRunsSummary { get; init; }

    /// <summary>T2-18: the firings themselves, one per line, newest first — the tooltip behind the summary.</summary>
    public required string RecentRunsDetail { get; init; }

    /// <summary>Drives the history line's visibility without a null-to-visibility converter.</summary>
    public bool HasRecentRuns => !string.IsNullOrEmpty(RecentRunsSummary);

    /// <summary>Only the owner device may advance a job, so "run now" is unavailable elsewhere.</summary>
    public required bool OwnedByThisDevice { get; init; }

    /// <summary>Both conditions, because they refuse for different reasons: another device owns the schedule,
    /// or this build cannot judge the job's state. The command re-checks the first anyway — a disabled button
    /// is a courtesy, and the service call is the guardrail.</summary>
    public bool CanRunNow => OwnedByThisDevice && StatusIsKnown;
}
