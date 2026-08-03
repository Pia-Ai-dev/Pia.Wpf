using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pia.Controls.Cards;

namespace Pia.Models;

public enum ActionCardState
{
    Pending,
    Accepted,
    Declined
}

public enum ActionCardCategory
{
    Memory,
    Todo,
    Reminder,
    Files,
    Git,

    /// <summary>An external (MCP) plugin tool — server-defined, gated per-call (Phase 2 MCP gate).</summary>
    Mcp,

    /// <summary>
    /// The built-in scheduled-job tools (plugin <c>scheduled-research</c>). APPENDED (Batch 04): these cards
    /// used to fall through to <see cref="Mcp"/>, so a built-in scheduling tool was titled "External tool",
    /// offered an "Always allow" button the gate then silently ignored, and had its key/value details parsed
    /// as JSON. Not persisted — but appended anyway, because renumbering a UI enum is a bad habit to keep.
    /// </summary>
    Scheduled
}

public record ActionCardDetail(string Label, string Value);

/// <summary>How a single line in a write-file preview diff changed.</summary>
public enum DiffLineKind
{
    Context,
    Added,
    Removed,

    /// <summary>A synthetic "diff truncated" marker row (no old/new line number, non-collapsible).</summary>
    TruncationNotice
}

/// <summary>
/// One rendered line of a write-file old→new diff (LCS-based). <see cref="OldLineNumber"/> /
/// <see cref="NewLineNumber"/> carry the 1-based source/target line for the dual gutter; a side that
/// does not exist for the row (added → no old, removed → no new, truncation notice → neither) is null.
/// The trailing optional numbers keep every existing 2-arg construction site compiling.
/// </summary>
public record DiffLine(DiffLineKind Kind, string Text, int? OldLineNumber = null, int? NewLineNumber = null)
{
    /// <summary>
    /// A unified-diff gutter marker so the add/remove distinction survives the loss of color
    /// (high-contrast themes, red-green colorblindness): '+' added, '-' removed, ' ' context.
    /// </summary>
    public string Gutter => Kind switch
    {
        DiffLineKind.Added => "+",
        DiffLineKind.Removed => "-",
        _ => " "
    };

    /// <summary>The gutter marker followed by the line text, for the diff card's monospace rows.</summary>
    public string Display => $"{Gutter} {Text}";
}

public partial class ActionCardInfo : ObservableObject
{
    public required string Title { get; init; }
    public required string Summary { get; init; }
    public required ActionCardCategory Category { get; init; }
    public required string ToolName { get; init; }

    /// <summary>The plugin that owns the gated tool (for the per-(PluginId, ToolName) grant). UI-carried only.</summary>
    public Guid PluginId { get; init; }

    /// <summary>
    /// Eligibility hint set by the builder from <c>IToolPermissionService.IsAutoApproveEligible</c>:
    /// drives the triad-vs-pair button set only. The authoritative eligibility check is re-done at the
    /// gate (never trusted from the card). Distinct from <see cref="IsDestructive"/> — design §8.
    /// </summary>
    public bool IsAutoApprovable { get; init; }

    /// <summary>
    /// hermes #15. May this card offer the SESSION tier ("allow until Pia closes")? Set by the builder from
    /// <c>ToolAutonomy.IsSessionGrantOfferable</c> — the same function the gate mints with, so the offer and
    /// the authority cannot drift. Independent of <see cref="IsAutoApprovable"/>: <c>write_file</c> is
    /// session-grantable and never standing-grantable, which is the gap this tier exists to close, while a
    /// delete-like or work-discarding tool is neither.
    /// </summary>
    public bool IsSessionGrantable { get; init; }

    /// <summary>True when the card was built pre-resolved by a standing grant (bypass render). UI-only.</summary>
    public bool IsAutoApproved { get; init; }

    public bool IsDestructive { get; init; }
    public string? WarningText { get; init; }

    public ObservableCollection<ActionCardDetail> Details { get; init; } = [];
    public ObservableCollection<ActionCardDetail> OldValueDetails { get; init; } = [];

    /// <summary>
    /// Line-level old→new diff for write_file cards. Populated only for the files category;
    /// when present the card renders the first-class <c>FileDiffCard</c> instead of the Label/Value rows.
    /// </summary>
    public ObservableCollection<DiffLine> DiffLines { get; init; } = [];

    /// <summary>The detokenized, sandbox-relative target path shown in the diff card header. UI-only.</summary>
    public string FilePath { get; init; } = "";

    public bool HasDetails => Details.Count > 0;
    public bool HasOldValueDetails => OldValueDetails.Count > 0;
    public bool HasDiff => DiffLines.Count > 0;

    /// <summary>Added/removed line tallies for the diff header's +N/−N stats (the truncation marker counts as neither).</summary>
    public int AddedCount => DiffLines.Count(d => d.Kind == DiffLineKind.Added);
    public int RemovedCount => DiffLines.Count(d => d.Kind == DiffLineKind.Removed);

    private ObservableCollection<object>? _diffRows;

    /// <summary>
    /// The hunk-collapsed view rows (a mix of <see cref="DiffLine"/> and <c>CollapsedDiffRun</c>) rendered
    /// by the diff card. Built lazily from the already-detokenized <see cref="DiffLines"/>, so the
    /// PII detokenization applied at build time is preserved for free.
    /// </summary>
    public ObservableCollection<object> DiffRows => _diffRows ??= DiffHunkBuilder.Build(DiffLines);

    /// <summary>
    /// Whether the diff card body is expanded. Independent of the decision <see cref="IsExpanded"/>:
    /// a pending diff shows expanded; resolving auto-collapses it, and it stays re-expandable afterwards.
    /// </summary>
    [ObservableProperty]
    private bool _isDiffExpanded = true;

    [ObservableProperty]
    private ActionCardState _state = ActionCardState.Pending;

    [ObservableProperty]
    private bool _isExpanded;

    public bool IsPending => State == ActionCardState.Pending;
    public bool IsResolved => State != ActionCardState.Pending;

    public string AcceptedStatusText { get; init; } = string.Empty;
    public string DeclinedStatusText { get; init; } = string.Empty;

    /// <summary>The resolved status shown when the card was auto-approved by a standing grant.</summary>
    public string AutoApprovedStatusText { get; init; } = string.Empty;

    // The decision-bar labels are passed in (a Model cannot inject ILocalizationService — LayerDependencyTests).
    // ActionCardBuilder.Build sets these from ActionCard_Decline / ActionCard_AllowOnce / ActionCard_AlwaysAllow.
    public string DeclineLabel { get; init; } = string.Empty;
    public string AllowOnceLabel { get; init; } = string.Empty;
    public string AlwaysAllowLabel { get; init; } = string.Empty;

    /// <summary>hermes #15. "Allow for this session" — set from <c>ActionCard_AllowForSession</c>.</summary>
    public string AllowForSessionLabel { get; init; } = string.Empty;

    /// <summary>
    /// The footer rendered as a shared <see cref="CardDecisionBar"/> (design §7/§8). Decline first, then
    /// Allow once, then the two GRANT tiers in ascending durability — each keyed off its OWN offerability
    /// flag, never off <see cref="IsDestructive"/>:
    /// <list type="bullet">
    /// <item><see cref="IsSessionGrantable"/> → "Allow for this session" (hermes #15, process-scoped)</item>
    /// <item><see cref="IsAutoApprovable"/> → "Always allow" (persisted; an ineligible-yet-non-destructive
    /// tool like <c>write_file</c> must NOT offer it)</item>
    /// </list>
    /// So: allowlisted/external → four buttons, <c>write_file</c> → three, a delete-like or work-discarding
    /// tool → the pair [Decline (Default), Allow once (Danger when destructive, else Primary)].
    /// </summary>
    public IReadOnlyList<DecisionButton> Decisions
    {
        get
        {
            var buttons = new List<DecisionButton>(4)
            {
                new()
                {
                    Label = DeclineLabel,
                    Emphasis = DecisionEmphasis.Default,
                    Command = DeclineCommand,
                },
                new()
                {
                    // Allow once stays Primary whenever a grant tier is offered beside it; on the bare pair it
                    // carries the destructive styling. Keyed off IsAutoApprovable for that, unchanged from
                    // design §8 — a session-grantable card is by construction not destructive, so the two
                    // conditions cannot disagree in a way that would drop the Danger emphasis.
                    Label = AllowOnceLabel,
                    Emphasis = !IsAutoApprovable && IsDestructive
                        ? DecisionEmphasis.Danger
                        : DecisionEmphasis.Primary,
                    Command = AllowOnceCommand,
                },
            };

            if (IsSessionGrantable)
            {
                buttons.Add(new DecisionButton
                {
                    Label = AllowForSessionLabel,
                    Emphasis = DecisionEmphasis.Default,
                    Command = AllowForSessionCommand,
                });
            }

            if (IsAutoApprovable)
            {
                buttons.Add(new DecisionButton
                {
                    Label = AlwaysAllowLabel,
                    Emphasis = DecisionEmphasis.Default,
                    Command = AlwaysAllowCommand,
                });
            }

            return buttons;
        }
    }

    public string ResolvedStatusText
    {
        get
        {
            if (IsAutoApproved)
                return AutoApprovedStatusText;

            return State == ActionCardState.Accepted ? AcceptedStatusText : DeclinedStatusText;
        }
    }

    private readonly TaskCompletionSource<ToolDecision> _tcs = new();

    partial void OnStateChanged(ActionCardState value)
    {
        OnPropertyChanged(nameof(IsPending));
        OnPropertyChanged(nameof(IsResolved));
        OnPropertyChanged(nameof(ResolvedStatusText));
    }

    public Task<ToolDecision> WaitForUserDecisionAsync() => _tcs.Task;

    [RelayCommand]
    private void AllowOnce()
    {
        if (State != ActionCardState.Pending) return;
        State = ActionCardState.Accepted;
        IsExpanded = false;
        IsDiffExpanded = false;
        _tcs.TrySetResult(ToolDecision.AllowOnce);
    }

    /// <summary>
    /// hermes #15. Same first-press-wins guard and the same resolved state as the other two allow arms — the
    /// TIER is carried by the decision value alone, and only the gate may act on it (the card never touches
    /// the grant store: a Model cannot take a service, and the authoritative offerability check is the gate's).
    /// </summary>
    [RelayCommand]
    private void AllowForSession()
    {
        if (State != ActionCardState.Pending) return;
        State = ActionCardState.Accepted;
        IsExpanded = false;
        IsDiffExpanded = false;
        _tcs.TrySetResult(ToolDecision.AllowForSession);
    }

    [RelayCommand]
    private void AlwaysAllow()
    {
        if (State != ActionCardState.Pending) return;
        State = ActionCardState.Accepted;
        IsExpanded = false;
        IsDiffExpanded = false;
        _tcs.TrySetResult(ToolDecision.AlwaysAllow);
    }

    [RelayCommand]
    private void Decline()
    {
        if (State != ActionCardState.Pending) return;
        State = ActionCardState.Declined;
        IsExpanded = false;
        IsDiffExpanded = false;
        _tcs.TrySetResult(ToolDecision.Decline);
    }

    [RelayCommand]
    private void Cancel()
    {
        if (State != ActionCardState.Pending) return;
        State = ActionCardState.Declined;
        IsExpanded = false;
        IsDiffExpanded = false;
        _tcs.TrySetCanceled();
    }

    [RelayCommand]
    private void ToggleExpand()
    {
        if (IsPending)
            IsExpanded = !IsExpanded;
    }

    /// <summary>
    /// Toggles the diff card body. Deliberately has no <see cref="IsPending"/> gate — the diff stays
    /// reviewable (and re-expandable) after the decision is resolved.
    /// </summary>
    [RelayCommand]
    private void ToggleDiffExpand() => IsDiffExpanded = !IsDiffExpanded;
}
