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
    Reminder,
    Meeting
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

    public IReadOnlyList<ActionCardChoice>? Choices { get; init; }
    public bool IsMultiChoice => Choices is { Count: > 0 };

    public ObservableCollection<ActionCardDetail> Details { get; init; } = [];
    public ObservableCollection<ActionCardDetail> OldValueDetails { get; init; } = [];

    public bool HasDetails => Details.Count > 0;
    public bool HasOldValueDetails => OldValueDetails.Count > 0;

    [ObservableProperty]
    private ActionCardState _state = ActionCardState.Pending;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private string? _chosenKey;

    public bool IsPending => State == ActionCardState.Pending;
    public bool IsResolved => State != ActionCardState.Pending;

    public string AcceptedStatusText { get; init; } = string.Empty;
    public string DeclinedStatusText { get; init; } = string.Empty;

    public string ResolvedStatusText => State == ActionCardState.Accepted
        ? AcceptedStatusText
        : DeclinedStatusText;

    private readonly TaskCompletionSource<string?> _choiceTcs = new();

    partial void OnStateChanged(ActionCardState value)
    {
        OnPropertyChanged(nameof(IsPending));
        OnPropertyChanged(nameof(IsResolved));
        OnPropertyChanged(nameof(ResolvedStatusText));
    }

    public async Task<bool> WaitForUserDecisionAsync()
    {
        var key = await _choiceTcs.Task;
        return key == "accept";
    }

    public Task<string?> WaitForChoiceAsync() => _choiceTcs.Task;

    [RelayCommand]
    private void Accept()
    {
        if (State != ActionCardState.Pending) return;
        State = ActionCardState.Accepted;
        IsExpanded = false;
        ChosenKey = "accept";
        _choiceTcs.TrySetResult("accept");
    }

    [RelayCommand]
    private void Decline()
    {
        if (State != ActionCardState.Pending) return;
        State = ActionCardState.Declined;
        IsExpanded = false;
        ChosenKey = "decline";
        _choiceTcs.TrySetResult("decline");
    }

    [RelayCommand]
    private void Cancel()
    {
        if (State != ActionCardState.Pending) return;
        State = ActionCardState.Declined;
        IsExpanded = false;
        _choiceTcs.TrySetCanceled();
    }

    [RelayCommand]
    private void Choose(string? key)
    {
        if (State != ActionCardState.Pending || string.IsNullOrEmpty(key)) return;
        State = ActionCardState.Accepted;
        IsExpanded = false;
        ChosenKey = key;
        _choiceTcs.TrySetResult(key);
    }

    [RelayCommand]
    private void ToggleExpand()
    {
        if (IsPending)
            IsExpanded = !IsExpanded;
    }
}
