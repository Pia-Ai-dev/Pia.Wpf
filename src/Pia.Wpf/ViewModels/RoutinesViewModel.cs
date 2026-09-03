using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Logging;
using Pia.Models;
using Pia.Navigation;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.ViewModels.Models;

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
    private readonly IPersonaService _personas;
    private readonly IAgentRunService _runs;
    private readonly IDialogService _dialogs;
    private readonly IWindowManagerService _windowManager;
    private readonly ILocalizationService _localization;
    private readonly IPluginService _plugins;
    private readonly ITextOptimizationService? _textOptimization;
    private readonly ILogger<RoutinesViewModel> _logger;

    /// <summary>The grant list as it will be persisted: insertion-ordered, deduped OrdinalIgnoreCase. The rows
    /// project this, never the reverse — the stored column is a JSON array, so saving in display order would
    /// rewrite it on every save and manufacture a sync diff on a routine nobody edited.</summary>
    private readonly List<string> _editGrantSelection = [];

    /// <summary>Which card opened the editor, so a save can record it. Not an editable field — a blank start
    /// and an edit of an existing job both clear it.</summary>
    private string? _editBlueprintKey;

    /// <summary>Resolves a blueprint's text in the UI language at the moment a card is opened; the rendered goal
    /// is then stored, so a later language switch leaves an existing routine alone.</summary>
    private string Localize(string key) => _localization[key];

    private bool _searchWasActive;

    /// <summary>True while the kind, recurrence, day, time and effort are still what opening the editor set.
    /// None of those has a blank state, so the draft prefill needs this rather than an emptiness check.</summary>
    private bool _pickersUntouched;

    // The draft writes the same properties the user does, so it announces itself rather than tripping the latch.
    private bool _applyingDraft;

    private void PickersTouched()
    {
        if (!_applyingDraft) _pickersUntouched = false;
    }

    public ObservableCollection<RoutineRow> Jobs { get; } = [];

    /// <summary>Provider choices for the editor. The leading entry is the "use the default" null row.</summary>
    public ObservableCollection<RoutineProviderChoice> ProviderChoices { get; } = [];

    /// <summary>Persona choices for the editor, led by the "use the active persona" null row. A saved pin that
    /// no longer resolves is appended as its own visible entry rather than dropped.</summary>
    public ObservableCollection<RoutinePersonaChoice> PersonaChoices { get; } = [];

    /// <summary>Reasoning-effort choices, led by the null "inherit the persona's" row.</summary>
    public IReadOnlyList<RoutineEffortChoice> EffortChoices { get; }

    /// <summary>(value, LOCALIZED label) pairs: a ComboBox bound straight to <c>Enum.GetValues</c> renders the
    /// C# identifier in every locale, which the localization parity tests cannot see.</summary>
    public IReadOnlyList<RoutineKindChoice> JobKinds { get; }

    public IReadOnlyList<RoutineRecurrenceChoice> Recurrences { get; }

    /// <summary>Day and month names come from the culture, not from resx keys — .NET already ships the
    /// translations of "Tuesday" and "March" correctly.</summary>
    public IReadOnlyList<RoutineDayOfWeekChoice> DayOfWeekChoices { get; }

    public IReadOnlyList<int> DayOfMonthChoices { get; } = [.. Enumerable.Range(1, 31)];

    public IReadOnlyList<RoutineMonthChoice> MonthChoices { get; }

    /// <summary>The starting points offered instead of a blank editor, with their text already resolved.</summary>
    public IReadOnlyList<RoutineBlueprintCard> Blueprints { get; }

    public bool HasBlueprints => Blueprints.Count > 0;

    public ObservableCollection<RoutineBlueprintGroup> BlueprintGroups { get; } = [];

    public bool HasBlueprintMatches => BlueprintGroups.Count > 0;

    /// <summary>Matched against title and description only; there are no search keywords in resx.</summary>
    [ObservableProperty]
    private string _searchQuery = string.Empty;

    partial void OnSearchQueryChanged(string value) => RebuildBlueprintGroups();

    [ObservableProperty]
    private bool _defaultProviderCannotSearchWeb;

    [ObservableProperty]
    private bool _hasJobs;

    /// <summary>True while a load, a save or a manual run is in flight. Every command's CanExecute reads it, so
    /// a double-click cannot fire a job twice.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCreateCommand))]
    [NotifyCanExecuteChangedFor(nameof(BrowseBlueprintsCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartFromBlueprintCommand))]
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
        OnPropertyChanged(nameof(ShowsCatalog));
        OnPropertyChanged(nameof(ShowsPlaceholder));
        OnPropertyChanged(nameof(EditorPinsEnabled));

        // Otherwise the menu springs open again the next time the selection clears — after a delete.
        if (value is not null) IsCatalogOpen = false;

        // A selection change is a different job, so an editor still open on the previous one has to go — saving
        // it afterwards would write this job's fields onto that one's id.
        if (IsEditorOpen) CancelEdit();
    }

    // ---- editor state -------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BrowseBlueprintsCommand))]
    private bool _isEditorOpen;

    partial void OnIsEditorOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowsDetail));
        OnPropertyChanged(nameof(ShowsCatalog));
        OnPropertyChanged(nameof(ShowsPlaceholder));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsCatalog))]
    [NotifyPropertyChangedFor(nameof(ShowsPlaceholder))]
    private bool _isCatalogOpen;

    /// <summary>Four states, each a full expression: the panes are siblings in one Grid, so a second true state
    /// still hit-tests over the visible one.</summary>
    public bool ShowsDetail => !IsEditorOpen && SelectedJob is not null;

    public bool ShowsCatalog => IsCatalogOpen && !IsEditorOpen && SelectedJob is null;

    public bool ShowsPlaceholder => !IsCatalogOpen && !IsEditorOpen && SelectedJob is null;

    /// <summary>Null while creating; the row's id while editing. Also what decides which service call the save
    /// takes, so it must be cleared when the editor closes.</summary>
    [ObservableProperty]
    private Guid? _editingJobId;

    partial void OnEditingJobIdChanged(Guid? value) => OnPropertyChanged(nameof(EditorPinsEnabled));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _editName = string.Empty;

    /// <summary>The job's goal — USER CONTENT. Rendered here, never logged above SensitiveDebug.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _editQuery = string.Empty;

    /// <summary>Free text the AI assist drafts a routine from — USER CONTENT, never logged above SensitiveDebug.</summary>
    [ObservableProperty]
    private string _editDescription = string.Empty;

    [ObservableProperty]
    private bool _isDrafting;

    /// <summary>Slot fields for the card the editor came from; empty for a blank start and for an edit.</summary>
    public ObservableCollection<RoutineSlotRow> EditSlots { get; } = [];

    public bool HasEditSlots => EditSlots.Count > 0;

    /// <summary>Where the slot value sits inside the rendered goal, so the editor can tint it and the link
    /// between the two fields is visible without touching either. Length 0 = nothing to mark.</summary>
    [ObservableProperty]
    private int _goalHighlightStart;

    [ObservableProperty]
    private int _goalHighlightLength;

    /// <summary>True once the user has asked to edit the instruction by hand. A TextBox cannot tint part of
    /// its own text, so the goal is shown as read-only coloured text until then and as a plain box after —
    /// only ever one of the two, which is what keeps them from having to line up pixel for pixel.</summary>
    [ObservableProperty]
    private bool _isGoalEditing;

    public bool ShowsGoalPreview => GoalHighlightLength > 0 && !IsGoalEditing && !EditorIsMeeting;

    /// <summary>The rendered goal split around the slot value, so the preview can bind three Runs rather
    /// than cut the string up in XAML.</summary>
    public string GoalPrefix => GoalSpanIsSound ? EditQuery[..GoalHighlightStart] : EditQuery;

    public string GoalHighlightText =>
        GoalSpanIsSound ? EditQuery.Substring(GoalHighlightStart, GoalHighlightLength) : string.Empty;

    public string GoalSuffix =>
        GoalSpanIsSound ? EditQuery[(GoalHighlightStart + GoalHighlightLength)..] : string.Empty;

    // The span is published before the goal it describes on at least one path, so every read is guarded.
    private bool GoalSpanIsSound =>
        GoalHighlightLength > 0
        && GoalHighlightStart >= 0
        && GoalHighlightStart + GoalHighlightLength <= EditQuery.Length;

    private void NotifyGoalPreviewChanged()
    {
        OnPropertyChanged(nameof(ShowsGoalPreview));
        OnPropertyChanged(nameof(GoalPrefix));
        OnPropertyChanged(nameof(GoalHighlightText));
        OnPropertyChanged(nameof(GoalSuffix));
    }

    partial void OnGoalHighlightStartChanged(int value) => NotifyGoalPreviewChanged();

    partial void OnGoalHighlightLengthChanged(int value) => NotifyGoalPreviewChanged();

    partial void OnIsGoalEditingChanged(bool value) => NotifyGoalPreviewChanged();

    /// <summary>Hands the instruction over to the plain editable box. The preview does not edit in place: the
    /// tint describes a machine-rendered goal, and the moment the prose is the user's it stops being true.</summary>
    [RelayCommand]
    private void EditGoal() => IsGoalEditing = true;

    private void ClearGoalHighlight() => GoalHighlightLength = 0;

    /// <summary>One slot per blueprint and one placeholder per template — asserted in the catalog tests — so
    /// a single span is exactly enough and the placeholder index in the template IS the index in the
    /// rendered goal.</summary>
    private void SetGoalHighlight(RoutineBlueprint blueprint, RoutineBlueprintText text)
    {
        if (blueprint.Slots.FirstOrDefault() is not { } slot)
        {
            ClearGoalHighlight();
            return;
        }

        var at = text.Template.IndexOf($"{{{slot.Name}}}", StringComparison.Ordinal);
        if (at < 0)
        {
            ClearGoalHighlight();
            return;
        }

        // The effective value, which is what the fill engine substituted: the row when it holds something,
        // the localized default when it is blank.
        var row = EditSlots.FirstOrDefault(r => string.Equals(r.Name, slot.Name, StringComparison.Ordinal));
        var value = row is not null && !string.IsNullOrWhiteSpace(row.Value)
            ? row.Value.Trim()
            : text.SlotDefaults.GetValueOrDefault(slot.Name) ?? string.Empty;

        GoalHighlightStart = at;
        GoalHighlightLength = value.Length;
    }

    // A keystroke in the goal box and the renderer's own write are the same PropertyChanged event, so the
    // renderer announces itself rather than the handler guessing.
    private bool _renderingGoal;
    private bool _goalHandEdited;

    partial void OnEditQueryChanged(string value)
    {
        NotifyGoalPreviewChanged();
        if (_renderingGoal) return;

        _goalHandEdited = true;
        // The prose is theirs now, so the span no longer describes it.
        ClearGoalHighlight();
    }

    private void RenderGoalFromSlots()
    {
        if (_goalHandEdited) return;
        if (RoutineBlueprintCatalog.Find(_editBlueprintKey) is not { } blueprint) return;

        var text = RoutineBlueprintText.Resolve(blueprint, Localize);
        var values = EditSlots.ToDictionary(s => s.Name, s => s.Value, StringComparer.Ordinal);
        if (RoutineBlueprintFill.ToCreateArgs(blueprint, text, values).Query is not { } rendered) return;

        _renderingGoal = true;
        EditQuery = rendered;
        _renderingGoal = false;

        SetGoalHighlight(blueprint, text);
    }

    private void ResetEditSlots(RoutineBlueprint? blueprint, RoutineBlueprintText? text = null)
    {
        EditSlots.Clear();
        if (blueprint is not null && text is not null)
            foreach (var slot in blueprint.Slots)
                EditSlots.Add(new RoutineSlotRow(
                    slot.Name,
                    _localization[slot.LabelKey],
                    _localization[slot.HelpKey],
                    text.SlotDefaults.GetValueOrDefault(slot.Name),
                    RenderGoalFromSlots));

        OnPropertyChanged(nameof(HasEditSlots));
    }

    /// <summary>What <c>SaveAsync</c> requires, so the editor can mark the fields and grey out Save instead
    /// of refusing after the click. The time FORMAT is still checked there — a required marker cannot say
    /// "that is not HH:mm".</summary>
    public bool CanSave =>
        !string.IsNullOrWhiteSpace(EditName)
        && !string.IsNullOrWhiteSpace(EditTimeOfDay)
        && (EditorIsMeeting
            ? !string.IsNullOrWhiteSpace(EditMeetingUrl) && EditMeetingConsent
            : !string.IsNullOrWhiteSpace(EditQuery));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private ScheduledJobKind _editKind = ScheduledJobKind.AgentTask;

    [ObservableProperty]
    private RecurrenceType _editRecurrence = RecurrenceType.Daily;

    /// <summary>"HH:mm", parsed on save. A string rather than a TimeOnly because the failure mode of a mistyped
    /// time must be a refused save with a message, not a silently coerced schedule.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _editTimeOfDay = "09:00";

    partial void OnEditTimeOfDayChanged(string value) => PickersTouched();

    [ObservableProperty]
    private DayOfWeek _editDayOfWeek = DateTime.Now.DayOfWeek;

    partial void OnEditDayOfWeekChanged(DayOfWeek value) => PickersTouched();

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

    /// <summary>Every tool this device offers, grouped by plugin, plus a trailing group for stored grants
    /// nothing here provides.</summary>
    public ObservableCollection<RoutineToolGroup> EditToolGroups { get; } = [];

    public bool HasEditToolCatalog => EditToolGroups.Any(g => !g.IsUnavailableGroup);

    public bool HasEditMissingTools => EditToolGroups.Any(g => g.IsUnavailableGroup);

    public bool EditorIsAgentTask => EditKind == ScheduledJobKind.AgentTask;

    /// <summary>A meeting routine has a link and a consent tick instead of a goal, a provider and tool grants —
    /// none of the latter mean anything when the "run" is a browser sitting in a call.</summary>
    public bool EditorIsMeeting => EditKind == ScheduledJobKind.MeetingAttendance;

    /// <summary>Device-local: the Teams link is a bearer token for the meeting and never reaches the wire.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _editMeetingUrl = string.Empty;

    /// <summary>The overlay's consent tick is per-session; an unattended join has to carry its own.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private bool _editMeetingConsent;

    /// <summary>The collapsed picker's one line. Names the launcher's default OUT LOUD when nothing is ticked
    /// on an agent routine: a header reading "none" would restate the very lie this picker removes.</summary>
    public string EditToolsSummary => _editGrantSelection.Count > 0
        ? string.Join(", ", _editGrantSelection)
        : EditKind == ScheduledJobKind.AgentTask
            ? _localization["Routines_Field_Tools_Summary_None_Agent"]
            : _localization["Routines_Field_Tools_Summary_None_Research"];

    /// <summary>Suppresses the SUCCESS notification this job would raise. Failures still notify, and the result
    /// chat is written either way. Device-local, so it is not part of what syncs with the job.</summary>
    [ObservableProperty]
    private bool _editQuietOnSuccess;

    /// <summary>Which persona this routine's runs use. Device-local, so it never travels with the job.</summary>
    [ObservableProperty]
    private RoutinePersonaChoice? _editPersona;

    [ObservableProperty]
    private RoutineEffortChoice? _editEffort;

    partial void OnEditEffortChanged(RoutineEffortChoice? value) => PickersTouched();

    /// <summary>False only while editing a routine another device owns. Not selection-bound: <see cref="StartCreate"/>
    /// leaves a foreign row selected, and the new routine it opens must stay editable.</summary>
    public bool EditorPinsEnabled =>
        EditingJobId is null || SelectedJob?.OwnedByThisDevice != false;

    public bool EditorWantsSpecificDate => EditRecurrence == RecurrenceType.Once;
    public bool EditorWantsDayOfWeek => EditRecurrence == RecurrenceType.Weekly;
    public bool EditorWantsDayOfMonth => EditRecurrence is RecurrenceType.Monthly or RecurrenceType.Yearly;
    public bool EditorWantsMonth => EditRecurrence == RecurrenceType.Yearly;

    partial void OnEditRecurrenceChanged(RecurrenceType value)
    {
        PickersTouched();
        OnPropertyChanged(nameof(EditorWantsSpecificDate));
        OnPropertyChanged(nameof(EditorWantsDayOfWeek));
        OnPropertyChanged(nameof(EditorWantsDayOfMonth));
        OnPropertyChanged(nameof(EditorWantsMonth));
    }

    // The kind decides what an EMPTY grant list means, so switching it rewrites both lines the picker shows.
    partial void OnEditKindChanged(ScheduledJobKind value)
    {
        PickersTouched();
        OnPropertyChanged(nameof(ShowsGoalPreview));
        OnPropertyChanged(nameof(EditorIsAgentTask));
        OnPropertyChanged(nameof(EditorIsMeeting));
        OnPropertyChanged(nameof(EditToolsSummary));
    }

    private readonly Services.MeetingAttendee.IBrowserProvisioner _browserProvisioner;

    public RoutinesViewModel(
        IScheduledJobService jobs,
        IScheduledJobRunner runner,
        IProviderService providers,
        IPersonaService personas,
        IAgentRunService runs,
        IDialogService dialogs,
        IWindowManagerService windowManager,
        ILocalizationService localization,
        IPluginService plugins,
        Services.MeetingAttendee.IBrowserProvisioner browserProvisioner,
        ILogger<RoutinesViewModel> logger,
        ITextOptimizationService? textOptimization = null)
    {
        _jobs = jobs;
        _runner = runner;
        _providers = providers;
        _personas = personas;
        _runs = runs;
        _dialogs = dialogs;
        _windowManager = windowManager;
        _localization = localization;
        _plugins = plugins;
        _browserProvisioner = browserProvisioner;
        _logger = logger;
        _textOptimization = textOptimization;

        JobKinds = [.. Enum.GetValues<ScheduledJobKind>()
            .Select(k => new RoutineKindChoice(k, _localization[$"Settings_ScheduledJobs_Kind_{k}"]))];
        Recurrences = [.. Enum.GetValues<RecurrenceType>()
            .Select(r => new RoutineRecurrenceChoice(
                r, _localization[$"Settings_ScheduledJobs_Recurrence_{r}"]))];
        // The leading row inherits the persona's effort, a different instruction from None ("no reasoning"),
        // so its label must not reuse that word in any locale.
        EffortChoices = [
            new RoutineEffortChoice(null, _localization["Routines_Field_Effort_Default"]),
            .. Enum.GetValues<ReasoningEffort>()
                .Select(e => new RoutineEffortChoice(e, _localization[$"Routines_Effort_{e}"]))];
        _editEffort = EffortChoices[0];

        var names = CultureInfo.CurrentCulture.DateTimeFormat;
        DayOfWeekChoices = [.. Enum.GetValues<DayOfWeek>()
            .Select(d => new RoutineDayOfWeekChoice(d, names.GetDayName(d)))];
        MonthChoices = [.. Enumerable.Range(1, 12)
            .Select(m => new RoutineMonthChoice(m, names.GetMonthName(m)))];

        Blueprints = [.. RoutineBlueprintCatalog.All.Select(b => new RoutineBlueprintCard(
            b.Key,
            _localization[b.TitleKey],
            _localization[b.DescriptionKey],
            _localization[$"Settings_ScheduledJobs_Kind_{b.Kind}"],
            _localization[$"Settings_ScheduledJobs_Recurrence_{b.Recurrence}"],
            b.DefaultTime.ToString("HH\\:mm"),
            b.Category,
            b.RequiresWebSearch))];
        RebuildBlueprintGroups();
    }

    public void OnNavigatedTo(object? parameter) { }

    public void OnNavigatedFrom() { }

    /// <summary>Reads the database, so it runs on navigation rather than in the constructor — a ctor that awaits
    /// is how a view becomes a startup cost.</summary>
    public async Task OnNavigatedToAsync(object? parameter) => await RefreshAsync();

    /// <param name="selectJobId">Selected once the rows exist. The rebuild is marshalled, so a caller that
    /// awaits this and then picks out of <c>Jobs</c> is reading the rows from before it.</param>
    public async Task RefreshAsync(Guid? selectJobId = null)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var jobs = await _jobs.GetAllAsync();
            var providers = await _providers.GetProvidersAsync();
            var personas = await _personas.GetPersonasAsync();
            var assistantProvider = await ResolveAssistantProviderAsync();

            var rows = new List<RoutineRow>(jobs.Count);
            foreach (var job in jobs)
            {
                var providerName = job.ProviderId is { } id
                    ? providers.FirstOrDefault(p => p.Id == id)?.Name
                    : null;
                // Null for a pin nothing resolves, which is what the row renders as "no longer available".
                var personaName = job.PersonaId is { } personaId
                    ? personas.FirstOrDefault(p => p.Id == personaId)?.Name
                    : null;
                rows.Add(BuildRow(job, providerName, personaName, await _jobs.IsOwnedByThisDeviceAsync(job),
                    await LoadRecentFiringsAsync(job.Id)));
            }

            PostOrRun(() =>
            {
                // Rows are rebuilt wholesale, so the selection has to be re-resolved by id or every refresh
                // would silently empty the detail pane the user is reading.
                var selectedId = selectJobId ?? SelectedJob?.Id;

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

                PersonaChoices.Clear();
                PersonaChoices.Add(new RoutinePersonaChoice(
                    null, _localization["Routines_Field_Persona_Default"]));
                foreach (var persona in personas)
                    PersonaChoices.Add(new RoutinePersonaChoice(persona.Id, persona.Name));

                // No provider at all is a different problem, and claiming this one about it would be noise.
                DefaultProviderCannotSearchWeb = assistantProvider is not null
                    && !AssistantPromptComposer.IsWebSearchActive(assistantProvider);

                if (Jobs.Count == 0)
                {
                    SelectedJob = null;
                    OpenCatalog();
                }
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

    /// <summary>Failure-isolated on purpose: the web-search hint is decoration, and the list must still render
    /// without it.</summary>
    private async Task<AiProvider?> ResolveAssistantProviderAsync()
    {
        try
        {
            return await _providers.GetDefaultProviderForModeAsync(WindowMode.Assistant);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve the assistant's default provider");
            return null;
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

    private RoutineRow BuildRow(ScheduledJob job, string? providerName, string? personaName, bool ownedHere,
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
            PersonaId = job.PersonaId,
            PersonaLabel = job.PersonaId is null
                ? string.Empty
                : personaName ?? _localization["Routines_Field_Persona_Missing"],
            ReasoningEffort = job.ReasoningEffort,
            EffortLabel = job.ReasoningEffort is { } effort
                ? _localization[$"Routines_Effort_{effort}"]
                : string.Empty,
            GrantedToolNames = [.. job.GrantedTools],
            ToolsSummary = job.GrantedTools.Count > 0
                ? string.Join(", ", job.GrantedTools)
                : job.Kind == ScheduledJobKind.AgentTask
                    ? _localization["Routines_Detail_Tools_AgentDefault"]
                    : _localization["Routines_Detail_Tools_None"],
            QuietOnSuccess = job.QuietOnSuccess,
            MeetingUrl = job.MeetingUrl,
            MeetingConsentAckAt = job.MeetingConsentAckAt,
            RecentRunsSummary = BuildRecentRunsSummary(recentFirings),
            RecentRuns = [.. recentFirings.Select(BuildRunRow)],
            OwnedByThisDevice = ownedHere,
        };
    }

    private bool CanWork() => !IsBusy;

    /// <summary>New routine is refused, not just greyed, while the editor is open: the alternative is
    /// discarding whatever the user has typed into it.</summary>
    private bool CanStartNew() => !IsBusy && !IsEditorOpen;

    private bool CanActOnSelection() => !IsBusy && SelectedJob is not null;

    [RelayCommand(CanExecute = nameof(CanWork))]
    private void StartCreate()
    {
        var now = DateTime.Now;
        EditingJobId = null;
        _editBlueprintKey = null;
        EditName = string.Empty;
        EditQuery = string.Empty;
        EditDescription = string.Empty;
        EditKind = ScheduledJobKind.AgentTask;
        EditRecurrence = RecurrenceType.Daily;
        EditTimeOfDay = "09:00";
        EditDayOfWeek = now.DayOfWeek;
        EditDayOfMonth = now.Day;
        EditMonth = now.Month;
        EditSpecificDate = null;
        EditMeetingUrl = string.Empty;
        EditMeetingConsent = false;
        ResetEditTools([]);
        EditQuietOnSuccess = false;
        ApplyPinChoices(null, null, null);
        ResetEditSlots(null);
        _goalHandEdited = false;
        ClearGoalHighlight();
        IsGoalEditing = false;
        // Last, after the field writes above have each reported a touch.
        _pickersUntouched = true;
        StatusMessage = null;
        IsEditorOpen = true;
    }

    /// <summary>What "New routine" opens: the catalog, not a blank editor.</summary>
    [RelayCommand(CanExecute = nameof(CanStartNew))]
    private void BrowseBlueprints()
    {
        if (!CanStartNew())
            return;

        SelectedJob = null;
        OpenCatalog();
    }

    /// <summary>Always unfiltered: the setter is silent when the query was already empty, so the rebuild that
    /// restores the default expansion has to be explicit.</summary>
    private void OpenCatalog()
    {
        SearchQuery = string.Empty;
        RebuildBlueprintGroups();
        IsCatalogOpen = true;
    }

    [RelayCommand]
    private void CloseCatalog() => IsCatalogOpen = false;

    // All three explicitly: clearing the selection only closes the catalog when the new value is non-null.
    [RelayCommand]
    private void GoHome()
    {
        CancelEdit();
        IsCatalogOpen = false;
        SelectedJob = null;
    }

    /// <summary>Rebuilt rather than filtered in place: the query decides which groups exist at all.</summary>
    private void RebuildBlueprintGroups()
    {
        var terms = SearchQuery.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var searching = terms.Length > 0;
        var carryExpansion = searching && _searchWasActive;
        var wasExpanded = BlueprintGroups.ToDictionary(g => g.Key, g => g.IsExpanded, StringComparer.Ordinal);
        _searchWasActive = searching;

        BlueprintGroups.Clear();
        foreach (var category in RoutineBlueprintCategories.InDisplayOrder)
        {
            var matches = Blueprints.Where(c => c.Category == category && Matches(c, terms)).ToList();
            if (matches.Count == 0) continue;

            var stem = RoutineBlueprintCategories.StemOf(category);
            var group = new RoutineBlueprintGroup(
                category,
                _localization[$"Routines_Category_{stem}_Title"],
                _localization[$"Routines_Category_{stem}_Subtitle"])
            {
                // A collapsed group would hide a match, so entering a search forces both open — but only the
                // step into one, or a group collapsed mid-search springs back on the next keystroke.
                IsExpanded = carryExpansion && wasExpanded.TryGetValue(category, out var open)
                    ? open
                    : searching || category == RoutineBlueprintCategories.Ready
            };
            foreach (var card in matches) group.Cards.Add(card);
            BlueprintGroups.Add(group);
        }

        OnPropertyChanged(nameof(HasBlueprintMatches));
    }

    private static bool Matches(RoutineBlueprintCard card, string[] terms)
    {
        var haystack = $"{card.Title} {card.Description}";
        return terms.All(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Fills the same editor <see cref="StartCreate"/> opens; nothing is persisted until the user saves.</summary>
    [RelayCommand(CanExecute = nameof(CanWork))]
    private void StartFromBlueprint(string? key)
    {
        if (RoutineBlueprintCatalog.Find(key) is not { } blueprint) return;

        // A card that cannot render is refused rather than opened on the raw template: falling back would put
        // a literal {slot} in the goal box and let the user schedule it. The tool path refuses the same case.
        var text = RoutineBlueprintText.Resolve(blueprint, Localize);
        var fill = RoutineBlueprintFill.ToCreateArgs(blueprint, text);
        if (fill.Query is not { } rendered)
        {
            _logger.LogWarning("Blueprint {Key} did not render: {Kind} on slot {Slot}",
                blueprint.Key, fill.Error!.Kind, fill.Error.SlotName);
            StatusMessage = _localization["Settings_ScheduledJobs_LoadFailed"];
            return;
        }

        var now = DateTime.Now;
        EditingJobId = null;
        _editBlueprintKey = blueprint.Key;
        EditName = _localization[blueprint.TitleKey];
        EditQuery = rendered;
        EditDescription = string.Empty;
        EditKind = blueprint.Kind;
        EditRecurrence = blueprint.Recurrence;
        EditTimeOfDay = blueprint.DefaultTime.ToString("HH\\:mm");
        EditDayOfWeek = blueprint.DefaultDayOfWeek ?? now.DayOfWeek;
        EditDayOfMonth = now.Day;
        EditMonth = now.Month;
        EditSpecificDate = null;
        ResetEditTools(blueprint.GrantedTools);
        EditQuietOnSuccess = blueprint.QuietOnSuccess;
        ApplyPinChoices(null, null, blueprint.DefaultEffort);
        ResetEditSlots(blueprint, text);
        _goalHandEdited = false;
        // After the slot rows exist: the span is measured against the value they hold.
        SetGoalHighlight(blueprint, text);
        IsGoalEditing = false;
        _pickersUntouched = false;
        StatusMessage = null;
        IsEditorOpen = true;

        _logger.LogInformation("Opened the routines editor from blueprint {Key}", blueprint.Key);
        _logger.SensitiveDebug("Blueprint {Key} prefilled name: {Name} goal: {Goal}",
            blueprint.Key, EditName, EditQuery);
    }

    [RelayCommand(CanExecute = nameof(CanActOnSelection))]
    private void StartEdit()
    {
        if (SelectedJob is not { } row) return;

        EditingJobId = row.Id;
        _editBlueprintKey = null;
        EditName = row.Name;
        EditQuery = row.Query;
        EditDescription = string.Empty;
        EditKind = row.Kind;
        EditRecurrence = row.Recurrence;
        EditTimeOfDay = row.TimeOfDay.ToString("HH\\:mm");
        // A job predating the day pickers has no stored day; NextFireAt is the day it actually fires on, which
        // is the honest thing to show rather than today.
        EditDayOfWeek = row.DayOfWeek ?? row.NextFireAt.DayOfWeek;
        EditDayOfMonth = row.DayOfMonth ?? row.NextFireAt.Day;
        EditMonth = row.Month ?? row.NextFireAt.Month;
        EditSpecificDate = row.SpecificDate;
        EditMeetingUrl = row.MeetingUrl ?? string.Empty;
        EditMeetingConsent = row.MeetingConsentAckAt is not null;
        ResetEditTools(row.GrantedToolNames);
        EditQuietOnSuccess = row.QuietOnSuccess;
        ApplyPinChoices(row.ProviderId, row.PersonaId, row.ReasoningEffort);
        // No slot block on an edit: the stored query is the user's own text, and re-rendering it from slot
        // defaults would overwrite exactly what the fields exist to protect.
        ResetEditSlots(null);
        _goalHandEdited = false;
        ClearGoalHighlight();
        IsGoalEditing = false;
        _pickersUntouched = false;
        StatusMessage = null;
        IsEditorOpen = true;
    }

    private void ApplyPinChoices(Guid? providerId, Guid? personaId, ReasoningEffort? effort)
    {
        EditProvider = ProviderChoices.FirstOrDefault(p => p.Id == providerId) ?? ProviderChoices.FirstOrDefault();
        EditPersona = ResolvePersonaChoice(personaId);
        EditEffort = EffortChoices.FirstOrDefault(e => e.Value == effort) ?? EffortChoices[0];
    }

    /// <summary>A pin whose persona is gone gets a row of its own: falling back to the default row would show
    /// "active persona", and the next save would then destroy a pin the user never touched.</summary>
    private RoutinePersonaChoice? ResolvePersonaChoice(Guid? personaId)
    {
        // The synthetic row belongs to one job, so it must not survive into the next editor session.
        for (var i = PersonaChoices.Count - 1; i >= 0; i--)
            if (PersonaChoices[i].IsUnavailable) PersonaChoices.RemoveAt(i);

        if (personaId is not { } id) return PersonaChoices.FirstOrDefault();
        if (PersonaChoices.FirstOrDefault(p => p.Id == id) is { } match) return match;

        var missing = new RoutinePersonaChoice(id, _localization["Routines_Field_Persona_Missing"], true);
        PersonaChoices.Add(missing);
        _logger.LogInformation("The routines editor opened on persona pin {PersonaId}, which no longer resolves", id);
        return missing;
    }

    /// <summary>Seed the picker for one editor session. The catalog is read here rather than subscribed to:
    /// this VM is scoped and re-navigable, so a PluginsChanged handler would leak one per navigation.</summary>
    private void ResetEditTools(IEnumerable<string> granted)
    {
        _editGrantSelection.Clear();
        foreach (var name in granted)
        {
            var trimmed = name.Trim();
            if (trimmed.Length == 0) continue;
            if (_editGrantSelection.Contains(trimmed, StringComparer.OrdinalIgnoreCase)) continue;
            _editGrantSelection.Add(trimmed);
        }

        RebuildEditToolGroups();
    }

    private void RebuildEditToolGroups()
    {
        EditToolGroups.Clear();

        IReadOnlyList<ToolCatalogEntry> catalog;
        try
        {
            catalog = _plugins.GetToolCatalog();
        }
        catch (Exception ex)
        {
            // Degrade to "no tools offered" rather than dropping grants: the selection list is untouched.
            _logger.LogWarning(ex, "Could not read the tool catalog for the routines editor");
            catalog = [];
        }

        var offered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in catalog.GroupBy(e => e.PluginName, StringComparer.Ordinal)
                                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var rows = group.OrderBy(e => e.ToolName, StringComparer.OrdinalIgnoreCase)
                            .Select(e => RoutineToolRow.FromCatalog(e, _localization, OnToolRowToggled))
                            .ToList();
            foreach (var row in rows) offered.Add(row.ToolName);
            EditToolGroups.Add(new RoutineToolGroup(group.Key, false, rows));
        }

        var orphans = _editGrantSelection.Where(n => !offered.Contains(n)).ToList();
        if (orphans.Count > 0)
        {
            // Same contract as an unresolvable persona pin: shown, not dropped, or the next save would revoke
            // a grant the user never touched. Filed on their own — an orphan has no plugin to sit under.
            EditToolGroups.Add(new RoutineToolGroup(
                _localization["Routines_Field_Tools_Missing"], true,
                [.. orphans.Select(n => RoutineToolRow.Unavailable(n, _localization, OnToolRowToggled))]));
            _logger.LogInformation(
                "The routines editor opened on {Count} granted tool(s) with no catalog row", orphans.Count);
        }

        foreach (var row in EditToolGroups.SelectMany(g => g.Tools))
            row.SyncSelection(_editGrantSelection.Contains(row.ToolName, StringComparer.OrdinalIgnoreCase));

        NotifyToolSelectionChanged();
    }

    private void OnToolRowToggled(RoutineToolRow row, bool selected)
    {
        if (selected)
        {
            // Append, never insert or sort: the stored column is a JSON array whose order reaches sync.
            if (!_editGrantSelection.Contains(row.ToolName, StringComparer.OrdinalIgnoreCase))
                _editGrantSelection.Add(row.ToolName);
        }
        else
        {
            _editGrantSelection.RemoveAll(n => string.Equals(n, row.ToolName, StringComparison.OrdinalIgnoreCase));
        }

        // A grant is by NAME, so a second plugin exposing the same name is the same grant and must not show a
        // different tick. SyncSelection, not the setter, or this callback recurses.
        foreach (var other in EditToolGroups.SelectMany(g => g.Tools))
            if (!ReferenceEquals(other, row)
                && string.Equals(other.ToolName, row.ToolName, StringComparison.OrdinalIgnoreCase))
                other.SyncSelection(selected);

        NotifyToolSelectionChanged();
    }

    private void NotifyToolSelectionChanged()
    {
        OnPropertyChanged(nameof(EditToolsSummary));
        OnPropertyChanged(nameof(HasEditToolCatalog));
        OnPropertyChanged(nameof(HasEditMissingTools));
    }

    /// <summary>Drafts the whole routine from one sentence, the way the persona editor drafts a persona.</summary>
    [RelayCommand]
    private async Task GenerateDraftAsync()
    {
        if (string.IsNullOrWhiteSpace(EditDescription) || _textOptimization is null) return;

        IsDrafting = true;
        StatusMessage = null;
        try
        {
            var offered = OfferableDraftTools();
            var draft = await _textOptimization.GenerateRoutineDraftAsync(
                EditDescription, offered, EditProvider?.Id);

            // Text fills only what is still blank, so re-drafting cannot clobber what the user typed.
            if (string.IsNullOrWhiteSpace(EditName) && draft.Name is { } name) EditName = name;
            if (string.IsNullOrWhiteSpace(EditQuery) && draft.Goal is { } goal) EditQuery = ApplyWebSearchGuard(goal, draft);

            // The schedule fills only while it still holds what StartCreate set: a draft must not move a time
            // the user has already chosen, and there is no "blank" for a typed field to test.
            if (_pickersUntouched)
            {
                _applyingDraft = true;
                try
                {
                    // Not drafted, decided: a blank start opens on AgentTask, and an AgentTask with an empty
                    // grant list is remapped by the launcher to its write_file default — so a drafted routine
                    // advertising no grants could write files. Research grants exactly what it says.
                    EditKind = ScheduledJobKind.Research;
                    if (draft.Recurrence is { } recurrence) EditRecurrence = recurrence;
                    if (draft.DayOfWeek is { } day) EditDayOfWeek = day;
                    if (draft.TimeOfDay is { } time) EditTimeOfDay = time.ToString("HH\\:mm");
                    if (draft.Effort is { } effort)
                        EditEffort = EffortChoices.FirstOrDefault(e => e.Value == effort) ?? EditEffort;
                }
                finally
                {
                    _applyingDraft = false;
                }

                // A grant HAS a blank state, so it needs no latch of its own: anything already ticked is the
                // user's, and a card's empty list is a deliberate "this one only reads".
                if (_editGrantSelection.Count == 0)
                    ResetEditTools(AcceptableDraftTools(draft.Tools, offered));

                // The draft has now chosen them, so a second draft must not move them again.
                _pickersUntouched = false;
            }

            _logger.LogInformation("Drafted a routine from a description (web search needed: {Needed})",
                draft.NeedsWebSearch);
            _logger.SensitiveDebug("Routine draft from {Description} produced name: {Name} goal: {Goal}",
                EditDescription, EditName, EditQuery);
        }
        catch (Exception ex)
        {
            // The persona assist lets this escape; here the provider failure has a place to land.
            _logger.LogWarning(ex, "Drafting a routine failed");
            StatusMessage = _localization["Routines_Draft_Failed"];
        }
        finally
        {
            IsDrafting = false;
        }
    }

    /// <summary>What the drafting model may pick from: one entry per distinct tool name this device offers,
    /// minus the names a create-time grant is refused anyway — no point offering what would be dropped.</summary>
    private List<RoutineDraftTool> OfferableDraftTools()
    {
        IReadOnlyList<ToolCatalogEntry> catalog;
        try
        {
            catalog = _plugins.GetToolCatalog();
        }
        catch (Exception ex)
        {
            // Same degradation as the picker: offer nothing rather than fail the draft.
            _logger.LogWarning(ex, "Could not read the tool catalog for the routine draft");
            return [];
        }

        return [.. catalog
            .Where(e => !ToolPermissionService.IsPresumedExternalDeleteLike(e.ToolName))
            .Where(e => !e.ServerDeclaredDestructive)
            .GroupBy(e => e.ToolName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new RoutineDraftTool(g.Key, g.Select(e => e.Description).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d))))];
    }

    /// <summary>The drafted grant list, filtered to what may actually be stored. A name the catalog does not
    /// offer is dropped rather than kept as an orphan row: the user did not ask for it, and it would travel
    /// to every other device on the next sync.</summary>
    private List<string> AcceptableDraftTools(IReadOnlyList<string>? proposed, List<RoutineDraftTool> offered)
    {
        if (proposed is null || proposed.Count == 0) return [];

        var allowed = new HashSet<string>(offered.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
        var accepted = new List<string>();
        var refused = new List<string>();
        foreach (var name in proposed)
        {
            var trimmed = name.Trim();
            if (trimmed.Length == 0) continue;
            if (accepted.Contains(trimmed, StringComparer.OrdinalIgnoreCase)) continue;

            if (allowed.Contains(trimmed)) accepted.Add(trimmed);
            else refused.Add(trimmed);
        }

        if (refused.Count > 0)
            _logger.LogInformation("A routine draft proposed {Count} tool(s) this device does not offer", refused.Count);
        if (accepted.Count > 0)
            _logger.LogInformation("A routine draft selected {Count} tool grant(s)", accepted.Count);
        _logger.SensitiveDebug("Draft tools accepted: {Accepted} refused: {Refused}",
            string.Join(", ", accepted), string.Join(", ", refused));

        return accepted;
    }

    /// <summary>A drafted goal carries no guard of its own, so a provider that cannot search would answer a
    /// web question from memory — the same failure the blueprint guard exists for.</summary>
    private string ApplyWebSearchGuard(string goal, RoutineDraft draft) =>
        draft.NeedsWebSearch
            ? $"{goal} {_localization[RoutineBlueprintCatalog.WebSearchGuardKey]}"
            : goal;

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

        // A meeting routine has no goal to write — the link is the instruction — so the goal requirement is
        // relaxed to a name, and the two meeting-only fields are required in its place.
        if (string.IsNullOrWhiteSpace(EditName) || (!EditorIsMeeting && string.IsNullOrWhiteSpace(EditQuery)))
        {
            StatusMessage = _localization["Settings_ScheduledJobs_Validation_NameAndGoal"];
            return;
        }

        if (EditorIsMeeting)
        {
            if (!Services.MeetingAttendee.TeamsMeetingUrl.IsLikelyTeamsUrl(EditMeetingUrl))
            {
                StatusMessage = _localization["Routines_Validation_MeetingUrl"];
                return;
            }

            if (!EditMeetingConsent)
            {
                StatusMessage = _localization["Routines_Validation_MeetingConsent"];
                return;
            }
        }

        if (!TimeOnly.TryParseExact(EditTimeOfDay.Trim(), "HH\\:mm", out var timeOfDay))
        {
            StatusMessage = _localization["Settings_ScheduledJobs_Validation_Time"];
            return;
        }

        // From the selection list, not the rows: row order is display order, and the stored column is a JSON
        // array. Trimming and deduping already happened on the way in, so the invariant holds by construction.
        var grants = _editGrantSelection.ToList();

        // Each field is carried only for the recurrences that read it; a value left behind on a job switched to
        // another recurrence is inert, and is what lets the editor remember the old day if the user switches back.
        var specificDate = EditRecurrence == RecurrenceType.Once ? EditSpecificDate : null;
        var dayOfWeek = EditorWantsDayOfWeek ? EditDayOfWeek : (DayOfWeek?)null;
        var dayOfMonth = EditorWantsDayOfMonth ? EditDayOfMonth : (int?)null;
        var month = EditorWantsMonth ? EditMonth : (int?)null;

        // "Use the default" means NO pin. On update that has to be Guid.Empty — null means "leave unchanged",
        // so sending it made the default row a silent no-op. Create takes the null: it stores providerId as-is.
        var providerId = EditProvider?.Id;
        var personaId = EditPersona?.Id;
        var effort = EditEffort?.Value;

        // Query is NOT NULL and carries the vault title a saved transcript is named after, so a meeting with
        // no goal text reuses its name rather than storing a blank.
        var query = EditorIsMeeting && string.IsNullOrWhiteSpace(EditQuery) ? EditName.Trim() : EditQuery.Trim();
        var meetingUrl = EditorIsMeeting ? EditMeetingUrl.Trim() : null;
        // Re-stamped on every save of a consented meeting: the acknowledgement is about the meeting as it
        // stands now, not about whatever it was when first created.
        var meetingConsentAt = EditorIsMeeting && EditMeetingConsent ? DateTime.Now : (DateTime?)null;

        IsBusy = true;
        try
        {
            if (EditingJobId is { } id)
            {
                await _jobs.UpdateAsync(id,
                    name: EditName.Trim(),
                    query: query,
                    recurrence: EditRecurrence,
                    timeOfDay: timeOfDay,
                    dayOfWeek: dayOfWeek,
                    dayOfMonth: dayOfMonth,
                    month: month,
                    providerId: providerId ?? Guid.Empty,
                    grantedTools: grants,
                    specificDate: specificDate,
                    kind: EditKind,
                    quietOnSuccess: EditQuietOnSuccess,
                    personaId: personaId ?? Guid.Empty,
                    reasoningEffort: effort,
                    clearReasoningEffort: effort is null,
                    meetingUrl: meetingUrl,
                    meetingConsentAckAt: meetingConsentAt);

                _logger.LogInformation("Updated scheduled job {Id} from the routines view", id);
                _logger.SensitiveDebug("Updated scheduled job {Id} name: {Name} goal: {Goal} persona: {Persona}",
                    id, EditName, EditQuery, EditPersona?.Name);
            }
            else
            {
                var created = await _jobs.CreateAsync(EditName.Trim(), query, EditRecurrence,
                    timeOfDay, dayOfWeek: dayOfWeek, dayOfMonth: dayOfMonth, month: month,
                    specificDate: specificDate, providerId: providerId,
                    grantedTools: grants, kind: EditKind, quietOnSuccess: EditQuietOnSuccess,
                    personaId: personaId, reasoningEffort: effort, blueprintKey: _editBlueprintKey,
                    meetingUrl: meetingUrl, meetingConsentAckAt: meetingConsentAt);

                _logger.LogInformation("Created scheduled job {Id} from the routines view ({Kind})",
                    created.Id, EditKind);
                _logger.SensitiveDebug("Created scheduled job {Id} name: {Name} goal: {Goal} persona: {Persona}",
                    created.Id, EditName, EditQuery, EditPersona?.Name);
                EditingJobId = created.Id;
            }

            IsEditorOpen = false;
            // Otherwise the catalog renders over the save until the reload re-selects the row.
            IsCatalogOpen = false;
            StatusMessage = null;

            // Pull Chromium down NOW rather than at the join: the first-ever meeting would otherwise spend
            // its whole join window downloading a browser. Idempotent, so every later save is a no-op.
            if (EditorIsMeeting)
                _browserProvisioner.EnsureChromiumAsync(null).SafeFireAndForget(_logger);
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
        await RefreshAsync(savedId);
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

// Every ToString below is what a screen reader announces for the ComboBox item: the item peer reads the bound
// object, not the DisplayMemberPath text, so the generated record ToString would spell out the field dump.

/// <summary>One provider the editor may pin a job to; <see cref="Id"/> null is "use the default".</summary>
public sealed record RoutineProviderChoice(Guid? Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>One persona the editor may pin a job to; <see cref="Id"/> null is "use the active persona".
/// <see cref="IsUnavailable"/> marks a saved pin nothing resolves.</summary>
public sealed record RoutinePersonaChoice(Guid? Id, string Name, bool IsUnavailable = false)
{
    public override string ToString() => Name;
}

/// <summary>A reasoning effort and the label a user should see for it; <see cref="Value"/> null is "inherit the
/// persona's".</summary>
public sealed record RoutineEffortChoice(ReasoningEffort? Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A job type and the label a user should see for it.</summary>
public sealed record RoutineKindChoice(ScheduledJobKind Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A repeat interval and the label a user should see for it.</summary>
public sealed record RoutineRecurrenceChoice(RecurrenceType Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A weekday and the label a user should see for it.</summary>
public sealed record RoutineDayOfWeekChoice(DayOfWeek Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A month number and the label a user should see for it.</summary>
public sealed record RoutineMonthChoice(int Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One fillable slot of the card the editor came from, with its text already localized. Notification is
/// hand-rolled: an ObservableObject in this namespace has to end in "ViewModel".</summary>
public sealed class RoutineSlotRow : INotifyPropertyChanged
{
    private readonly Action _valueChanged;
    private string _value;

    public RoutineSlotRow(string name, string label, string help, string? initial, Action valueChanged)
    {
        Name = name;
        Label = label;
        Help = help;
        _valueChanged = valueChanged;
        _value = initial ?? string.Empty;
    }

    public string Name { get; }
    public string Label { get; }
    public string Help { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>USER CONTENT once edited. Blank falls back to the slot's default in the fill engine.</summary>
    public string Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
            _valueChanged();
        }
    }
}

/// <summary>One blueprint as its card renders it. Public because WPF's binding cannot read an internal type.</summary>
public sealed record RoutineBlueprintCard(
    string Key,
    string Title,
    string Description,
    string KindLabel,
    string RecurrenceLabel,
    string TimeLabel,
    string Category,
    bool RequiresWebSearch)
{
    public string AutomationId => $"Routines_Blueprint_{Key}";
}

/// <summary>Notification is hand-rolled: an ObservableObject in this namespace has to end in "ViewModel".</summary>
public sealed class RoutineBlueprintGroup : INotifyPropertyChanged
{
    private bool _isExpanded;

    public RoutineBlueprintGroup(string key, string title, string subtitle)
    {
        Key = key;
        Title = title;
        Subtitle = subtitle;
    }

    public string Key { get; }
    public string Title { get; }
    public string Subtitle { get; }

    public ObservableCollection<RoutineBlueprintCard> Cards { get; } = [];

    public string AutomationId => $"Routines_Category_{Key}";

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }
}

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

    public Guid? PersonaId { get; init; }

    /// <summary>The pinned persona's name, or the "no longer available" text when the pin does not resolve;
    /// empty when nothing is pinned. USER CONTENT.</summary>
    public required string PersonaLabel { get; init; }

    public bool HasPersonaPin => PersonaId is not null;

    public ReasoningEffort? ReasoningEffort { get; init; }
    public required string EffortLabel { get; init; }
    public bool HasEffortPin => ReasoningEffort is not null;

    /// <summary>The stored list, raw — what the editor seeds its ticks from, NOT what the pane renders.</summary>
    public required IReadOnlyList<string> GrantedToolNames { get; init; }

    /// <summary>What this routine may actually use at fire time, its kind included. An agent routine storing
    /// nothing still names write_file: hiding the line let the pane imply it could not write at all.</summary>
    public required string ToolsSummary { get; init; }

    /// <summary>This job's successes are not announced (device-local; failures still are).</summary>
    public required bool QuietOnSuccess { get; init; }

    /// <summary>The Teams link a meeting routine joins, and when recording was acknowledged for it. Both
    /// device-local; the link never leaves this machine.</summary>
    public string? MeetingUrl { get; init; }
    public DateTime? MeetingConsentAckAt { get; init; }

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
