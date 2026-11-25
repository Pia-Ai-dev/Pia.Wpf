using System.Collections.ObjectModel;
using Pia.Models;

namespace Pia.ViewModels;

public sealed partial class StepRowViewModel
{
    /// <summary>The chat replies this step wrote, shown inside the row instead of as bubbles.</summary>
    public ObservableCollection<AssistantMessage> Replies { get; } = [];

    public bool HasReplies => Replies.Count > 0;

    internal void SetReplies(IReadOnlyList<AssistantMessage> replies)
    {
        if (replies.SequenceEqual(Replies))
            return;

        Replies.Clear();
        foreach (var reply in replies)
            Replies.Add(reply);
        OnPropertyChanged(nameof(HasReplies));
    }
}
