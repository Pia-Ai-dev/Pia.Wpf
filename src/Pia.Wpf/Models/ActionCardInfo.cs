using System.Collections.ObjectModel;
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
    Files
}

public record ActionCardDetail(string Label, string Value);

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

    public bool HasDetails => Details.Count > 0;
    public bool HasOldValueDetails => OldValueDetails.Count > 0;

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
    public IReadOnlyList<DecisionButton> Decisions =>
        IsAutoApprovable
        ?
        [
            new DecisionButton
            {
                Label = DeclineLabel,
                Emphasis = DecisionEmphasis.Default,
                Command = DeclineCommand,
            },
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
        ]
        :
        [
            new DecisionButton
            {
                Label = DeclineLabel,
                Emphasis = DecisionEmphasis.Default,
                Command = DeclineCommand,
            },
            new DecisionButton
            {
                Label = AllowOnceLabel,
                Emphasis = IsDestructive ? DecisionEmphasis.Danger : DecisionEmphasis.Primary,
                Command = AllowOnceCommand,
            },
        ];

    public string ResolvedStatusText
    {
        get
        {
            if (IsAutoApproved) return AutoApprovedStatusText;
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
        _tcs.TrySetResult(ToolDecision.AllowOnce);
    }

    [RelayCommand]
    private void AlwaysAllow()
    {
        if (State != ActionCardState.Pending) return;
        State = ActionCardState.Accepted;
        IsExpanded = false;
        _tcs.TrySetResult(ToolDecision.AlwaysAllow);
    }

    [RelayCommand]
    private void Decline()
    {
        if (State != ActionCardState.Pending) return;
        State = ActionCardState.Declined;
        IsExpanded = false;
        _tcs.TrySetResult(ToolDecision.Decline);
    }

    [RelayCommand]
    private void Cancel()
    {
        if (State != ActionCardState.Pending) return;
        State = ActionCardState.Declined;
        IsExpanded = false;
        _tcs.TrySetCanceled();
    }

    [RelayCommand]
    private void ToggleExpand()
    {
        if (IsPending)
            IsExpanded = !IsExpanded;
    }
}
