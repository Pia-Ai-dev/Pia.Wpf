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
    Git
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

    /// <summary>
    /// The footer rendered as a shared <see cref="CardDecisionBar"/> (design §7/§8). The button set is keyed
    /// off <see cref="IsAutoApprovable"/> — never <see cref="IsDestructive"/> (an ineligible-yet-non-destructive
    /// tool like <c>write_file</c> must NOT offer "Always allow"). Eligible → triad
    /// [Decline (Default), Allow once (Primary), Always allow (Default)]; ineligible → pair
    /// [Decline (Default), Allow once (Danger when destructive, else Primary)].
    /// </summary>
    public IReadOnlyList<DecisionButton> Decisions
    {
        get
        {
            var decline = new DecisionButton
            {
                Label = DeclineLabel,
                Emphasis = DecisionEmphasis.Default,
                Command = DeclineCommand,
            };

            // Eligible → triad: Allow once stays Primary (Always allow is offered as the
            // standing-grant option). Ineligible → pair: Allow once carries the destructive
            // styling. The button set keys off IsAutoApprovable, never IsDestructive (design §8).
            if (IsAutoApprovable)
            {
                return
                [
                    decline,
                    new DecisionButton
                    {
                        Label = AllowOnceLabel,
                        Emphasis = DecisionEmphasis.Primary,
                        Command = AllowOnceCommand,
                    },
                    new DecisionButton
                    {
                        Label = AlwaysAllowLabel,
                        Emphasis = DecisionEmphasis.Default,
                        Command = AlwaysAllowCommand,
                    },
                ];
            }

            return
            [
                decline,
                new DecisionButton
                {
                    Label = AllowOnceLabel,
                    Emphasis = IsDestructive ? DecisionEmphasis.Danger : DecisionEmphasis.Primary,
                    Command = AllowOnceCommand,
                },
            ];
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
