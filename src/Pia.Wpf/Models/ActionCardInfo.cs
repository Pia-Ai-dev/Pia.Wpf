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
    Files
}

public record ActionCardDetail(string Label, string Value);

/// <summary>How a single line in a write-file preview diff changed.</summary>
public enum DiffLineKind
{
    Context,
    Added,
    Removed
}

/// <summary>One rendered line of a write-file old→new diff (LCS-based).</summary>
public record DiffLine(DiffLineKind Kind, string Text);

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

    /// <summary>
    /// Line-level old→new diff for write_file cards. Populated only for the files category;
    /// when present the card renders this colour-coded block instead of the Label/Value rows.
    /// </summary>
    public ObservableCollection<DiffLine> DiffLines { get; init; } = [];

    public bool HasDetails => Details.Count > 0;
    public bool HasOldValueDetails => OldValueDetails.Count > 0;
    public bool HasDiff => DiffLines.Count > 0;

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
