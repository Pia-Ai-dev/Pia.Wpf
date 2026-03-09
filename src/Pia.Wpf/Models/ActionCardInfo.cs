using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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
    Reminder
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
