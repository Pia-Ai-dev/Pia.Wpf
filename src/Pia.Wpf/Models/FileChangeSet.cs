using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Pia.Models;

/// <summary>
/// The accepted file-diff cards of one run step, rolled up behind a single folded header so a
/// twenty-file step reads as one line instead of twenty cards. A projection over
/// <c>AssistantMessage.ActionCards</c> — it holds the same card instances, never copies them.
/// </summary>
public partial class FileChangeSet : ObservableObject
{
    /// <summary>The card count at which a step's diffs are worth rolling up.</summary>
    public const int MinimumCards = 2;

    /// <summary>Per-set identity for automation ids, mirroring <see cref="ActionCardInfo.Id"/>. UI-only.</summary>
    public Guid Id { get; } = Guid.NewGuid();

    public ObservableCollection<ActionCardInfo> Cards { get; } = [];

    public FileChangeSet()
    {
        Cards.CollectionChanged += OnCardsChanged;
    }

    /// <summary>Distinct target paths — a file written twice in one step still counts as one file.</summary>
    public int FileCount =>
        Cards.Select(c => c.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    // The card tallies raise no change notification of their own, so these are recomputed
    // from Cards rather than relied on to self-notify.
    public int TotalAdded => Cards.Sum(c => c.AddedCount);

    public int TotalRemoved => Cards.Sum(c => c.RemovedCount);

    public bool IsAutoApproved => Cards.Count > 0 && Cards.All(c => c.IsAutoApproved);

    /// <summary>
    /// The shared status line, or empty when the cards were approved under different tiers — a set
    /// mixing always-allow with a session grant would otherwise pick one arbitrarily, so the icon
    /// carries the state alone.
    /// </summary>
    public string ResolvedStatusText
    {
        get
        {
            if (Cards.Count == 0) return string.Empty;
            var first = Cards[0].ResolvedStatusText;
            return Cards.All(c => c.ResolvedStatusText == first) ? first : string.Empty;
        }
    }

    public bool HasResolvedStatusText => !string.IsNullOrEmpty(ResolvedStatusText);

    [ObservableProperty]
    private bool _isExpanded;

    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;

    private void OnCardsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(FileCount));
        OnPropertyChanged(nameof(TotalAdded));
        OnPropertyChanged(nameof(TotalRemoved));
        OnPropertyChanged(nameof(IsAutoApproved));
        OnPropertyChanged(nameof(ResolvedStatusText));
        OnPropertyChanged(nameof(HasResolvedStatusText));
    }
}
