using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;

namespace Pia.ViewModels;

/// <summary>View-facing run states (R12). The four rendered states, the distinct truncated-Completed
/// variant, and — new in Phase 2 — the budget-pause WaitingForInput state (a "continue?" affordance)
/// plus the reserved user-initiated Paused state. Verifying renders as the Running chip (via the
/// MapState default) plus a "Checking the work…" current-activity line.</summary>
public enum RunProgressState
{
    Planning,
    Running,
    Completed,
    TruncatedCompleted,
    Failed,
    WaitingForInput, // budget-paused, awaiting the user's Continue
    Paused,          // reserved: user-initiated pause (Phase 4) — rendered, never driven this round
}

/// <summary>
/// Read-only projection of a live/selected <see cref="AgentRun"/> for the run-progress panel (§15.1/15.2).
/// The FIRST consumer of <see cref="IAgentRunService.RunChanged"/> (dormant since 1.1): that event may fire
/// off the UI thread (the orchestrator uses ConfigureAwait(false) + SafeFireAndForget), so every handler
/// marshals to the captured UI <see cref="SynchronizationContext"/> before touching bound collections (G3).
/// Constructed on the UI thread by <see cref="AssistantViewModel"/>, not DI-registered (mirrors LiveTurnExecutor).
/// </summary>
public sealed partial class RunProgressViewModel : ObservableObject, IDisposable
{
    // The writer (AgentRunService) serializes the ledger camelCase (F5) — match it here.
    private static readonly JsonSerializerOptions LedgerJsonOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IAgentRunService _runService;
    private readonly Guid _runId;
    private readonly SynchronizationContext _uiContext;
    private readonly ILocalizationService _localization;
    private readonly IAgentRunResumeService _resumeService;
    private readonly IAgentTimelineService? _timelineService;
    private readonly IRunWorkspaceService? _workspaces;
    private readonly ILogger _logger;
    private bool _disposed;

    public Guid RunId => _runId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanContinue))]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand))]
    private RunProgressState _state;

    /// <summary>True while a resume is being launched — gates the Continue button against a double-click
    /// (the CAS in the resume service is the hard guard; this is the UI-visible affordance).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanContinue))]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand))]
    private bool _isResuming;

    /// <summary>The budget-pause Continue affordance is the sanctioned Phase-2 exception to the otherwise
    /// read-only panel — enabled only while the run sits WaitingForInput and no resume is in flight.</summary>
    public bool CanContinue => State == RunProgressState.WaitingForInput && !IsResuming;

    [ObservableProperty]
    private bool _isTruncated;

    /// <summary>
    /// Localized chip text explaining WHY a Completed run is not a clean completion — read from the
    /// truncation <c>reason</c> in <c>ExtraJson</c>, never assumed. Since the budget cap parks the run
    /// (<see cref="RunProgressState.WaitingForInput"/>) instead of truncating it, the only reason the
    /// current code produces is <c>"unverified"</c> (the verify pass exhausted its replans); the
    /// budget wording survives only for runs persisted before that change. Null when not truncated.
    /// Always rendered MUTED, never in the danger brush (R5).
    /// </summary>
    [ObservableProperty]
    private string? _truncationNote;

    /// <summary>
    /// The current-activity line (design D1): the running step's title while Running, or a "building a
    /// plan" note while Planning; null (line hidden) otherwise. The live per-tool micro-status
    /// (<c>StatusText</c>) stays on the adjacent streaming transcript by design — this panel is
    /// plan-level, the transcript is token-level. Step title is SENSITIVE — bound to UI only, never logged.
    /// </summary>
    [ObservableProperty]
    private string? _currentActivity;

    public bool HasCurrentActivity => !string.IsNullOrEmpty(CurrentActivity);

    partial void OnCurrentActivityChanged(string? value) => OnPropertyChanged(nameof(HasCurrentActivity));

    public ObservableCollection<StepRowViewModel> Steps { get; } = [];

    [ObservableProperty]
    private long _totalInputTokens;

    [ObservableProperty]
    private long _totalOutputTokens;

    [ObservableProperty]
    private long _wallClockMs;

    public string LedgerSummary => FormatLedger();

    /// <summary>
    /// Rows of the run's tool-decision trace (Batch 03). Loaded ON EACH EXPAND, and never on
    /// <c>RunChanged</c>: the timeline deliberately does not participate in live projection, which is what
    /// keeps ~500 emits per run off the projection path.
    /// </summary>
    public ObservableCollection<TimelineRowViewModel> Timeline { get; } = [];

    [ObservableProperty]
    private bool _isTimelineExpanded;

    [ObservableProperty]
    private bool _isTimelineTruncated;

    [ObservableProperty]
    private string? _timelineNote;

    /// <summary>
    /// Drives the "nothing recorded" line. A BOOL the VM owns, not an inverse converter: the panel already
    /// uses <c>BooleanToVisibilityConverter</c>, and an unresolved <c>StaticResource</c> inside a
    /// <c>DataTemplate</c> throws at TEMPLATE INSTANTIATION — i.e. the first time a user expands this — which
    /// no test in the suite reaches.
    /// <para>
    /// "Nothing was recorded" is a POSITIVE claim about the run, so it is only ever made about a read that
    /// SUCCEEDED. A read that faulted sets <see cref="HasTimelineReadError"/> instead.
    /// </para>
    /// </summary>
    [ObservableProperty]
    private bool _hasNoTimeline = true;

    /// <summary>Drives the "could not be read" line — the other half of <see cref="HasNoTimeline"/>. A trace
    /// the store refused to hand over is not a run that recorded nothing.</summary>
    [ObservableProperty]
    private bool _hasTimelineReadError;

    /// <summary>
    /// Batch 06 G4 / plan D3: a settled run whose isolated workspace still holds files nobody promoted — i.e.
    /// a FAILED or CANCELLED run, because a clean one promotes automatically before it is marked Completed.
    /// Drives the offer line and the Publish button.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPublish))]
    [NotifyCanExecuteChangedFor(nameof(PublishCommand))]
    private bool _hasUnpublishedFiles;

    /// <summary>True while a publish is in flight — gates the button against a double-click, exactly as
    /// <see cref="IsResuming"/> gates Continue.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPublish))]
    [NotifyCanExecuteChangedFor(nameof(PublishCommand))]
    private bool _isPublishing;

    /// <summary>The localized result line of the last publish attempt; null when there is nothing to say.</summary>
    [ObservableProperty]
    private string? _publishNote;

    /// <summary>
    /// Worktree mode only (plan D5b): the run branch its output lives on. The panel must SAY this, or the
    /// honest user question after a run that "worked" is "where is my file?" — there is deliberately no merge
    /// and therefore no conflict handling on an unattended path.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutputBranch))]
    [NotifyPropertyChangedFor(nameof(OutputBranchNote))]
    private string? _outputBranchName;

    public bool HasOutputBranch => !string.IsNullOrEmpty(OutputBranchName);

    /// <summary>The rendered branch line. Formatted HERE rather than by a converter in the panel: the format
    /// argument is app-owned, the string is localized, and the layer rule keeps this kind of decision in the
    /// ViewModel.</summary>
    public string? OutputBranchNote =>
        HasOutputBranch ? _localization.Format("Run_Output_Branch", OutputBranchName!) : null;

    public bool CanPublish => HasUnpublishedFiles && !IsPublishing;

    /// <summary>
    /// The in-flight (or last) load, exposed so a fact can await the fire-and-forget the expand kicks off.
    /// The read itself is off-thread now, so a test that set <see cref="IsTimelineExpanded"/> and asserted
    /// immediately would race it.
    /// </summary>
    internal Task? TimelineLoadTask { get; private set; }

    partial void OnIsTimelineExpandedChanged(bool value)
    {
        if (!value) return;

        // Re-read on EVERY expand, not once. A load-once latch made the panel state a falsehood: a trace read
        // while step 1 was still planning would keep rendering "no tool decisions were recorded" for the rest
        // of the session, because nothing else in this VM ever re-reads it (RunChanged deliberately does not),
        // AssistantViewModel.SyncRunProgress keeps the same VM for the run's whole life, and there is no
        // refresh command. One indexed read per user click is the cheaper mistake.
        var load = LoadTimelineAsync();
        TimelineLoadTask = load;
        load.SafeFireAndForget(_logger);
    }

    public RunProgressViewModel(IAgentRunService runService, Guid runId, ILocalizationService localization,
        IAgentRunResumeService resumeService, ILogger logger,
        // Batch 03. Trailing and defaulted because this type is hand-constructed with a POSITIONAL argument
        // list in production and in its tests; null ⇒ the trace renders as empty and reads nothing.
        IAgentTimelineService? timelineService = null,
        // Batch 06 G4, same discipline for the same reason — LAST, and defaulted. Null ⇒ no publish
        // affordance and no branch line, i.e. the panel is byte-identical to the pre-Batch-06 one.
        IRunWorkspaceService? workspaces = null)
    {
        _timelineService = timelineService;
        _workspaces = workspaces;
        _runService = runService;
        _runId = runId;
        _localization = localization;
        _resumeService = resumeService;
        _logger = logger;
        // Captured on the construction (UI) thread; may be null in a headless test → run inline.
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();
        _runService.RunChanged += OnRunChanged;
        RefreshAsync().SafeFireAndForget(_logger); // initial projection
    }

    private void OnRunChanged(object? sender, AgentRunChangedEventArgs e)
    {
        if (e.RunId != _runId) return;              // filter to our run id
        RefreshAsync().SafeFireAndForget(_logger);   // the read may run off-thread; Project marshals (G3)
    }

    /// <summary>Re-reads the run and projects it onto the bound collections on the UI thread.</summary>
    internal async Task RefreshAsync()
    {
        var run = await _runService.GetAsync(_runId);
        if (run is null) return;
        _uiContext.Post(_ => Project(run), null); // marshal the mutation to the UI thread (G3)

        // Batch 06 G4: the workspace outcome is read in its OWN terminal-only branch, deliberately not folded
        // into Project above. DescribeAsync does a file read plus a directory enumeration, and RunChanged
        // fires on every step, every state flip and every ledger write — putting that on the projection path
        // would pay for it dozens of times per run to answer a question only a settled run can be asked.
        if (_workspaces is not null && IsTerminal(run.State))
            await LoadWorkspaceOutcomeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// A run that will not change again. Written as an explicit set rather than "not one of the live states"
    /// so a state a future build appends is treated as NON-terminal — the direction that merely defers the
    /// publish offer instead of offering to publish a workspace a live run is still writing into.
    /// </summary>
    private static bool IsTerminal(AgentRunState state) =>
        state is AgentRunState.Completed or AgentRunState.Failed or AgentRunState.Cancelled;

    /// <summary>
    /// Read the settled run's workspace outcome (plan D3/D5b) OFF the dispatcher and apply it through
    /// <see cref="_uiContext"/>, the same mechanism <see cref="ApplyTimelineAsync"/> uses. Failure-isolated:
    /// a fault leaves the panel with no offer and no branch line, which is the pre-Batch-06 panel.
    /// </summary>
    private async Task LoadWorkspaceOutcomeAsync()
    {
        RunWorkspaceOutcome? outcome = null;
        try
        {
            // DescribeAsync does its own filesystem work inside a Task.Run and never throws by contract; the
            // catch is here because "never throws" is a promise about today's implementation, not a type rule.
            outcome = await _workspaces!.DescribeAsync(_runId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Run {RunId} workspace outcome could not be read", _runId);
        }

        await ApplyWorkspaceOutcomeAsync(outcome).ConfigureAwait(false);
    }

    /// <summary>The ONE place the publish affordance's bound state is set, always on the UI thread (G3).</summary>
    private Task ApplyWorkspaceOutcomeAsync(RunWorkspaceOutcome? outcome)
    {
        var done = new TaskCompletionSource();
        _uiContext.Post(_ =>
        {
            try
            {
                // Never re-arm an offer the user already answered in this session: a publish clears
                // HasUnpublishedFiles and tears the workspace down, and the next RunChanged would otherwise
                // read a describe that raced the teardown.
                if (!IsPublishing)
                    HasUnpublishedFiles = outcome?.HasUnpublishedFiles ?? false;
                OutputBranchName = outcome?.BranchName;
            }
            finally
            {
                done.TrySetResult();
            }
        }, null);

        return done.Task;
    }

    /// <summary>
    /// Publish what a settled run left in its workspace (plan D3, the "else offer to publish" half). Declining
    /// is doing nothing: the workspace is retained and then swept by the launcher's terminal retention rule,
    /// so an unanswered offer cannot pin a workspace forever. Worktree mode never reaches here — its output is
    /// a branch, and <see cref="HasUnpublishedFiles"/> is false for it (plan D5b).
    /// <para>
    /// <see cref="RunPromotionResult.Skipped"/> is deliberately NOT surfaced: it is the byte-identical no-op
    /// case, and there is nothing to tell the user about a file that was already correct.
    /// </para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPublish))]
    private async Task Publish()
    {
        if (_workspaces is null) return;

        IsPublishing = true;
        try
        {
            var result = await _workspaces.PromoteAsync(_runId, CancellationToken.None);
            if (result is null)
            {
                // Nothing was promoted and the workspace is intact — the offer stays standing so the user can
                // retry after fixing whatever the service logged (a relocated assistant folder, typically).
                PublishNote = _localization["Run_Publish_Failed"];
                return;
            }

            await _workspaces.TearDownAsync(_runId, CancellationToken.None);
            HasUnpublishedFiles = false;

            var note = _localization.Format("Run_Publish_Done", result.Promoted);
            if (result.Conflicts > 0)
                note += " " + _localization.Format("Run_Publish_Conflicts", result.Conflicts);
            PublishNote = note;
        }
        catch (Exception ex)
        {
            // Run id only — a promotion's paths are user content and this logger is release-attachable.
            _logger.LogWarning(ex, "Run {RunId} publish failed from panel", _runId);
            PublishNote = _localization["Run_Publish_Failed"];
        }
        finally
        {
            IsPublishing = false;
        }
    }

    private void Project(AgentRun run)
    {
        var truncation = ReadTruncation(run);
        (State, IsTruncated) = MapState(run, truncation.Truncated);
        TruncationNote = IsTruncated ? DescribeTruncation(truncation.Reason) : null;
        SyncSteps(run.Plan);
        CurrentActivity = ComputeActivity(run);

        var ledger = TryParseLedger(run.LedgerJson);
        if (ledger is not null)
        {
            TotalInputTokens = ledger.InputTokens;
            TotalOutputTokens = ledger.OutputTokens;
            WallClockMs = ledger.WallClockMs;
            ApplyPerStepLedger(ledger);
            OnPropertyChanged(nameof(LedgerSummary));
        }
    }

    // R12 mapping. Verifying intentionally folds into Running here (keeps the spinner lit) while
    // ComputeActivity supplies its own "Checking the work…" line; WaitingForInput/Paused now render
    // as their own distinct (non-spinning) states with a Continue affordance. Cancelled folds into
    // the Failed-family visual.
    private static (RunProgressState, bool) MapState(AgentRun run, bool truncated) => run.State switch
    {
        AgentRunState.Planning => (RunProgressState.Planning, false),
        AgentRunState.Running => (RunProgressState.Running, false),
        AgentRunState.Failed => (RunProgressState.Failed, false),
        AgentRunState.Cancelled => (RunProgressState.Failed, false),
        AgentRunState.WaitingForInput => (RunProgressState.WaitingForInput, false), // budget pause — offer Continue
        AgentRunState.Paused => (RunProgressState.Paused, false),                   // reserved user pause (Phase 4)
        AgentRunState.Completed => truncated
            ? (RunProgressState.TruncatedCompleted, true)
            : (RunProgressState.Completed, false),
        _ => (RunProgressState.Running, false), // Verifying folds to Running (spinner)
    };

    // The truncation vocabulary is written by the run loop, not by a user or a model, so it is a fixed
    // set of app-owned tokens (never user content). An unknown/absent reason must NOT fall back to the
    // budget wording: that is exactly the lie this mapping removes — say "ended early" instead.
    private string DescribeTruncation(string? reason) => reason switch
    {
        "unverified" => _localization["Run_Unverified"],                             // verify pass never passed
        "budget" or "step-cap" or "wall-clock" => _localization["Run_StoppedAtBudget"], // pre-pause legacy rows
        _ => _localization["Run_EndedEarly"],
    };

    // Current-activity line (D1): the active step's title while Running (falls back to a generic
    // "working" note if no step is marked Running yet), a "building a plan" note while Planning, and
    // nothing on a terminal state (the header state chip already carries it).
    private string? ComputeActivity(AgentRun run) => run.State switch
    {
        AgentRunState.Planning => _localization["Run_Activity_Planning"],
        AgentRunState.Running =>
            run.Plan.FirstOrDefault(s => s.Status == AgentStepStatus.Running)?.Title
            ?? _localization["Run_Activity_Working"],
        AgentRunState.Verifying => _localization["Run_Activity_Verifying"],
        AgentRunState.WaitingForInput => _localization["Run_Activity_WaitingAtBudget"], // "Stopped at budget — continue?"
        _ => null, // Paused / terminal — the state chip already carries it
    };

    /// <summary>
    /// Resume a budget-paused run (§7.2 — the sanctioned Phase-2 mutation on the otherwise read-only
    /// panel). The resume service CAS-claims internally, so a double-click or a panel+Flow race is safe;
    /// a real resume flips State→Running via RunChanged, which clears CanContinue. Logs the run id only.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanContinue))]
    private async Task Continue()
    {
        IsResuming = true;
        try
        {
            await _resumeService.ResumeAsync(_runId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Run {RunId} resume failed from panel", _runId);
        }
        finally
        {
            IsResuming = false;
        }
    }

    /// <summary>
    /// Read the run's tool-decision trace and project it. <c>internal</c> so the facts can await it directly
    /// rather than racing the <c>_uiContext.Post</c> the collection fill is marshaled through (G3, by the same
    /// mechanism <see cref="RefreshAsync"/> already uses — not a new one).
    /// </summary>
    internal async Task LoadTimelineAsync()
    {
        if (_timelineService is null)
        {
            await ApplyTimelineAsync(rows: null, readFailed: false);
            return;
        }

        IReadOnlyList<AgentTimelineEvent> rows;
        try
        {
            // OFF the caller's thread. GetForRunAsync's own first await does not suspend when the writer tail
            // is already complete — the normal case for a finished run — so without this hop the store's
            // connection lock and the mapping of up to 501 rows would run on the dispatcher.
            rows = await Task.Run(() => _timelineService.GetForRunAsync(_runId));
        }
        catch (Exception ex)
        {
            // A trace that cannot be read says so; it never claims the run recorded nothing, and it never
            // breaks the panel.
            _logger.LogWarning(ex, "Run {RunId} timeline could not be read", _runId);
            await ApplyTimelineAsync(rows: null, readFailed: true);
            return;
        }

        await ApplyTimelineAsync(rows, readFailed: false);
    }

    /// <summary>
    /// The ONE place this VM mutates the trace's bound state, and it always runs on the UI thread (G3) —
    /// including the null-service and read-failure arms, which used to assign straight from whatever thread
    /// the load happened to be on.
    /// </summary>
    private Task ApplyTimelineAsync(IReadOnlyList<AgentTimelineEvent>? rows, bool readFailed)
    {
        var done = new TaskCompletionSource();
        _uiContext.Post(_ =>
        {
            try
            {
                Timeline.Clear();
                IsTimelineTruncated = false;
                TimelineNote = null;
                HasTimelineReadError = readFailed;

                foreach (var row in rows ?? [])
                {
                    if (row.Kind == AgentTimelineEventKind.TraceTruncated)
                    {
                        // A statement about the TRACE, not a tool call — surfaced as a note, never as a row.
                        IsTimelineTruncated = true;
                        TimelineNote = _localization.Format("Run_Timeline_Truncated", AgentTimelineService.MaxEventsPerRun);
                        continue;
                    }

                    Timeline.Add(Project(row));
                }

                HasNoTimeline = !readFailed && Timeline.Count == 0;
            }
            finally
            {
                done.TrySetResult();
            }
        }, null);

        return done.Task;
    }

    private TimelineRowViewModel Project(AgentTimelineEvent row) => new()
    {
        ToolName = row.ToolName,
        DecisionLabel = _localization[DecisionLabelKey(row.Decision)],
        OutcomeSuffix = row.Outcome == AgentTimelineOutcome.Error
            ? _localization["Run_Timeline_Outcome_Failed"]
            : null,
        StepLabel = row.StepId is { } stepId && Steps.Any(s => s.StepId == stepId)
            ? _localization.Format("Run_Timeline_Step", Steps.IndexOf(Steps.First(s => s.StepId == stepId)) + 1)
            : null,
        TimeLabel = row.CreatedAt.ToLocalTime().ToString("t"),
    };

    /// <summary>
    /// Eleven persisted decision ordinals collapse to five user-facing categories — the DB stays precise, the
    /// panel stays readable. Written as a switch with an explicit default arm, never an array index, so an
    /// ordinal from a future build renders as "unknown" instead of throwing (the append-only rule's other
    /// half).
    /// </summary>
    internal static string DecisionLabelKey(ToolGateDecision decision) => decision switch
    {
        ToolGateDecision.AutoApprovedStandingGrant or ToolGateDecision.AutoApprovedPolicy
            or ToolGateDecision.GrantedByName or ToolGateDecision.AutoApprovedAllowlist
            => "Run_Timeline_Decision_AutoApproved",
        ToolGateDecision.ApprovedOnce or ToolGateDecision.ApprovedAlways
            => "Run_Timeline_Decision_Approved",
        ToolGateDecision.DeclinedByUser or ToolGateDecision.CardCancelled
            or ToolGateDecision.DeniedNotGranted or ToolGateDecision.UnknownTool
            => "Run_Timeline_Decision_Denied",
        ToolGateDecision.DeniedDestructiveFloor => "Run_Timeline_Decision_Blocked",
        _ => "Run_Timeline_Decision_Unknown",
    };

    // Truncated-Completed marker lives in ExtraJson as {truncated:true,reason} (IAgentRunService.CompleteAsync).
    // Both halves are read in one parse: the flag drives the state, the reason drives the chip copy.
    // A malformed/absent envelope degrades to "not truncated" (the panel stays quiet rather than guessing).
    private static (bool Truncated, string? Reason) ReadTruncation(AgentRun run)
    {
        if (string.IsNullOrEmpty(run.ExtraJson)) return (false, null);
        try
        {
            using var doc = JsonDocument.Parse(run.ExtraJson);
            if (!doc.RootElement.TryGetProperty("truncated", out var t) || t.ValueKind != JsonValueKind.True)
                return (false, null);
            var reason = doc.RootElement.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString()
                : null;
            return (true, reason);
        }
        catch
        {
            return (false, null);
        }
    }

    // Diff by step Id so the Running highlight moves without rebuilding the whole list.
    private void SyncSteps(IReadOnlyList<AgentStep> plan)
    {
        // Drop rows no longer in the plan.
        for (var i = Steps.Count - 1; i >= 0; i--)
        {
            if (!plan.Any(s => s.Id == Steps[i].StepId))
                Steps.RemoveAt(i);
        }

        for (var ordinal = 0; ordinal < plan.Count; ordinal++)
        {
            var step = plan[ordinal];
            var existing = Steps.FirstOrDefault(r => r.StepId == step.Id);
            if (existing is null)
            {
                if (ordinal <= Steps.Count)
                    Steps.Insert(ordinal, StepRowViewModel.From(step));
                else
                    Steps.Add(StepRowViewModel.From(step));
            }
            else
            {
                existing.Status = step.Status; // move the highlight / update the glyph
            }
        }
    }

    private void ApplyPerStepLedger(Ledger ledger)
    {
        foreach (var entry in ledger.PerStep)
        {
            if (!Guid.TryParse(entry.StepId, out var id)) continue;
            var row = Steps.FirstOrDefault(r => r.StepId == id);
            if (row is null) continue;
            row.InputTokens = entry.InputTokens;
            row.OutputTokens = entry.OutputTokens;
        }
    }

    private static Ledger? TryParseLedger(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<Ledger>(json, LedgerJsonOptions); }
        catch { return null; }
    }

    private string FormatLedger()
    {
        var parts = new List<string> { $"{TotalInputTokens + TotalOutputTokens:N0} Tokens" };
        if (WallClockMs > 0)
            parts.Add($"{WallClockMs / 1000.0:0.#}s");
        return string.Join(" · ", parts);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _runService.RunChanged -= OnRunChanged;
    }

    // Mirrors AgentRunService's private Ledger/StepLedger DTOs (camelCase JSON).
    private sealed class Ledger
    {
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long WallClockMs { get; set; }
        public List<StepLedgerEntry> PerStep { get; set; } = [];
    }

    private sealed class StepLedgerEntry
    {
        public string StepId { get; set; } = string.Empty;
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
    }
}

/// <summary>Read-only row for one <see cref="AgentStep"/>. Title is SENSITIVE — bound to UI only, never logged.</summary>
public sealed partial class StepRowViewModel : ObservableObject
{
    public Guid StepId { get; init; }

    public string Title { get; init; } = string.Empty;

    /// <summary>Null in Phase 1 (single persona) → the avatar falls back to the run persona / Pia glyph.</summary>
    public Guid? AssignedPersonaId { get; init; }

    [ObservableProperty]
    private AgentStepStatus _status;

    [ObservableProperty]
    private long _inputTokens;

    [ObservableProperty]
    private long _outputTokens;

    public bool IsRunning => Status == AgentStepStatus.Running;

    partial void OnStatusChanged(AgentStepStatus value) => OnPropertyChanged(nameof(IsRunning));

    public static StepRowViewModel From(AgentStep step) => new()
    {
        StepId = step.Id,
        Title = step.Title,
        AssignedPersonaId = step.AssignedPersonaId,
        Status = step.Status,
    };
}

/// <summary>
/// Read-only row for one recorded tool decision (Batch 03). Everything here is metadata — the store holds no
/// tool arguments, no results and no paths, so there is nothing else to project and nothing here to reveal.
/// A property that named a file or carried a payload would fail the reflection assert in
/// <c>RunProgressViewModelTimelineTests</c>.
/// </summary>
public sealed class TimelineRowViewModel : ObservableObject
{
    /// <summary>Schema, not user content: a built-in constant or an MCP server's declared tool name.</summary>
    public string ToolName { get; init; } = string.Empty;

    /// <summary>One of five localized categories over the eleven persisted decision ordinals.</summary>
    public string DecisionLabel { get; init; } = string.Empty;

    /// <summary>Localized "failed" when the authorized call threw; null otherwise.</summary>
    public string? OutcomeSuffix { get; init; }

    /// <summary>"Step N" when the row's step is still in the projected plan; null when it is not (a replan
    /// deletes step rows, and the trail deliberately outlives them).</summary>
    public string? StepLabel { get; init; }

    public string TimeLabel { get; init; } = string.Empty;
}
