using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pia.Models;

namespace Pia.ViewModels;

public sealed partial class RunProgressViewModel
{
    [ObservableProperty]
    private string? _stoppedNote;

    private IReadOnlyList<RunReply> _replies = [];

    /// <summary>Replies whose step a re-plan replaced.</summary>
    public ObservableCollection<AssistantMessage> EarlierAttemptReplies { get; } = [];

    public bool HasEarlierAttempts => EarlierAttemptReplies.Count > 0;

    public string EarlierAttemptsLabel => _localization.Format("RunReplies_EarlierAttempts", EarlierAttemptReplies.Count);

    [ObservableProperty]
    private bool _isEarlierAttemptsExpanded;

    /// <summary>False until the first projection; until then the host keeps the replies as bubbles.</summary>
    [ObservableProperty]
    private bool _hasProjected;

    [RelayCommand]
    private void ToggleEarlierAttempts() => IsEarlierAttemptsExpanded = !IsEarlierAttemptsExpanded;

    internal void SetReplies(IReadOnlyList<RunReply> replies)
    {
        _replies = replies;
        ApplyReplies();
    }

    private void ApplyReplies()
    {
        var stepIds = new HashSet<Guid>(Steps.Select(s => s.StepId));
        foreach (var row in Steps)
            row.SetReplies([.. _replies.Where(r => r.StepId == row.StepId).Select(r => r.Message)]);

        var earlier = _replies.Where(r => r.StepId is not { } id || !stepIds.Contains(id)).Select(r => r.Message).ToList();
        if (!earlier.SequenceEqual(EarlierAttemptReplies))
        {
            EarlierAttemptReplies.Clear();
            foreach (var message in earlier)
                EarlierAttemptReplies.Add(message);
            OnPropertyChanged(nameof(HasEarlierAttempts));
            OnPropertyChanged(nameof(EarlierAttemptsLabel));
        }

        HasProjected = true;
    }
}
