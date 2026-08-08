using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Pia.ViewModels;

/// <summary>Read-only row for one delegated CHILD run — the drill-down target. Expanding a row loads THAT run's
/// own trace, per run and never merged into the parent's.</summary>
public sealed partial class ChildRunRowViewModel : ObservableObject
{
    private readonly Action<ChildRunRowViewModel> _requestTimeline;

    /// <param name="requestTimeline">Starts this row's trace load. An <c>Action</c> and not a
    /// <c>Func&lt;Task&gt;</c> on purpose: the fire-and-forget belongs to the owner, which has the logger — a row
    /// that swallowed its own faults would need a logger of its own for no other reason.</param>
    public ChildRunRowViewModel(Guid runId, string title, Action<ChildRunRowViewModel> requestTimeline)
    {
        RunId = runId;
        Title = title;
        _requestTimeline = requestTimeline;
    }

    public Guid RunId { get; }

    /// <summary>The child's goal. SENSITIVE user/model content — bound, never logged.</summary>
    public string Title { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFinished))]
    private RunProgressState _state;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTokens))]
    private long _inputTokens;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTokens))]
    private long _outputTokens;

    /// <summary>Input PLUS output, matching <see cref="StepRowViewModel.TokensLabel"/> exactly — the two render
    /// in visually identical mono columns on the same card, so they must not measure the word differently.</summary>
    [ObservableProperty]
    private string? _tokensLabel;

    public bool HasTokens => InputTokens + OutputTokens > 0;

    /// <summary>See <c>RunProgressViewModel.RefreshThemeBrushes</c>.</summary>
    internal void RefreshThemeBrushes() => OnPropertyChanged(nameof(State));

    /// <summary>Whether this child will still change. Drives the parent's "N of M finished" count only.</summary>
    public bool IsFinished => State is RunProgressState.Completed or RunProgressState.TruncatedCompleted
        or RunProgressState.Failed;

    [ObservableProperty]
    private bool _isExpanded;

    public ObservableCollection<TimelineRowViewModel> Timeline { get; } = [];

    [ObservableProperty]
    private bool _hasNoTimeline = true;

    [ObservableProperty]
    private bool _hasTimelineReadError;

    /// <summary>The in-flight (or last) trace load, exposed so a fact can await the fire-and-forget the expand
    /// starts rather than racing it.</summary>
    internal Task? TimelineLoadTask { get; set; }

    partial void OnIsExpandedChanged(bool value)
    {
        if (!value) return;

        // Re-read on EVERY expand: a trace read while the child was still working would otherwise keep claiming
        // "nothing recorded" for the rest of the session.
        _requestTimeline(this);
    }
}
