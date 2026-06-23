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

    // The decision-bar labels are passed in (a Model cannot inject ILocalizationService — LayerDependencyTests).
    // ActionCardBuilder.Build sets these from ActionCard_Decline / ActionCard_Accept.
    public string DeclineLabel { get; init; } = string.Empty;
    public string AcceptLabel { get; init; } = string.Empty;

    /// <summary>
    /// The footer rendered as a shared <see cref="CardDecisionBar"/> (design §6): a binary Decline/Accept
    /// pair bound to the existing <see cref="DeclineCommand"/>/<see cref="AcceptCommand"/>. Accept renders
    /// as <see cref="DecisionEmphasis.Danger"/> for destructive actions, otherwise <see cref="DecisionEmphasis.Primary"/>.
    /// </summary>
    public IReadOnlyList<DecisionButton> Decisions =>
    [
        new DecisionButton
        {
            Label = DeclineLabel,
            Emphasis = DecisionEmphasis.Default,
            Command = DeclineCommand,
        },
        new DecisionButton
        {
            Label = AcceptLabel,
            Emphasis = IsDestructive ? DecisionEmphasis.Danger : DecisionEmphasis.Primary,
            Command = AcceptCommand,
        },
    ];

    public string ResolvedStatusText => State == ActionCardState.Accepted
        ? AcceptedStatusText
        : DeclinedStatusText;

    private readonly TaskCompletionSource<bool> _tcs = new();

    partial void OnStateChanged(ActionCardState value)
    {
        OnPropertyChanged(nameof(IsPending));
        OnPropertyChanged(nameof(IsResolved));
        OnPropertyChanged(nameof(ResolvedStatusText));
    }

    public Task<bool> WaitForUserDecisionAsync() => _tcs.Task;

    [RelayCommand]
    private void Accept()
    {
        if (State != ActionCardState.Pending) return;
        State = ActionCardState.Accepted;
        IsExpanded = false;
        _tcs.TrySetResult(true);
    }

    [RelayCommand]
    private void Decline()
    {
        if (State != ActionCardState.Pending) return;
        State = ActionCardState.Declined;
        IsExpanded = false;
        _tcs.TrySetResult(false);
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
