using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Models;
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

    /// <summary>Rendered only when non-null (no price table populates it in Phase 1 — F6/OQ4).</summary>
    [ObservableProperty]
    private double? _costUsd;

    [ObservableProperty]
    private long _wallClockMs;

    public string LedgerSummary => FormatLedger();

    public RunProgressViewModel(IAgentRunService runService, Guid runId, ILocalizationService localization,
        IAgentRunResumeService resumeService, ILogger logger)
    {
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
    }

    private void Project(AgentRun run)
    {
        (State, IsTruncated) = MapState(run);
        SyncSteps(run.Plan);
        CurrentActivity = ComputeActivity(run);

        var ledger = TryParseLedger(run.LedgerJson);
        if (ledger is not null)
        {
            TotalInputTokens = ledger.InputTokens;
            TotalOutputTokens = ledger.OutputTokens;
            CostUsd = ledger.CostUsd; // TODO Phase 2: price table populates cost
            WallClockMs = ledger.WallClockMs;
            ApplyPerStepLedger(ledger);
            OnPropertyChanged(nameof(LedgerSummary));
        }
    }

    // R12 mapping. Verifying intentionally folds into Running here (keeps the spinner lit) while
    // ComputeActivity supplies its own "Checking the work…" line; WaitingForInput/Paused now render
    // as their own distinct (non-spinning) states with a Continue affordance. Cancelled folds into
    // the Failed-family visual.
    private static (RunProgressState, bool) MapState(AgentRun run) => run.State switch
    {
        AgentRunState.Planning => (RunProgressState.Planning, false),
        AgentRunState.Running => (RunProgressState.Running, false),
        AgentRunState.Failed => (RunProgressState.Failed, false),
        AgentRunState.Cancelled => (RunProgressState.Failed, false),
        AgentRunState.WaitingForInput => (RunProgressState.WaitingForInput, false), // budget pause — offer Continue
        AgentRunState.Paused => (RunProgressState.Paused, false),                   // reserved user pause (Phase 4)
        AgentRunState.Completed => ReadTruncated(run)
            ? (RunProgressState.TruncatedCompleted, true)
            : (RunProgressState.Completed, false),
        _ => (RunProgressState.Running, false), // Verifying folds to Running (spinner)
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

    // Truncated-Completed marker lives in ExtraJson as {truncated:true,reason} (IAgentRunService.CompleteAsync).
    private static bool ReadTruncated(AgentRun run)
    {
        if (string.IsNullOrEmpty(run.ExtraJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(run.ExtraJson);
            return doc.RootElement.TryGetProperty("truncated", out var t)
                && t.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
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
        if (CostUsd is { } cost)
            parts.Add($"${cost:0.##}");
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
        public double? CostUsd { get; set; }
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
