using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Pia.ViewModels;

/// <summary>
/// Read-only row for one delegated CHILD run (Batch 07 D17) — the drill-down target. <see cref="Title"/> is the
/// child's goal and is SENSITIVE: bound to UI only, never logged, exactly like <c>StepRowViewModel.Title</c>.
/// <para>
/// Expanding a row loads THAT run's own trace, per run and never merged into the parent's — see
/// <c>RunProgressViewModel.Children</c> for why interleaving is not implementable.
/// </para>
/// </summary>
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

    /// <summary>The localized "N tokens" figure the owner composes from input PLUS output, matching
    /// <see cref="StepRowViewModel.TokensLabel"/> exactly — the two render in visually identical mono columns
    /// on the same card, so the card must not print two different measures of the same word.</summary>
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

    /// <summary>This child run's own tool-decision trace. Loaded on each expand, like the parent's.</summary>
    public ObservableCollection<TimelineRowViewModel> Timeline { get; } = [];

    [ObservableProperty]
    private bool _hasNoTimeline = true;

    [ObservableProperty]
    private bool _hasTimelineReadError;

    /// <summary>The in-flight (or last) trace load, exposed so a fact can await the fire-and-forget the expand
    /// starts rather than racing it — the same affordance the parent's <c>TimelineLoadTask</c> is.</summary>
    internal Task? TimelineLoadTask { get; set; }

    partial void OnIsExpandedChanged(bool value)
    {
        if (!value) return;

        // Re-read on EVERY expand, for the reason the parent's own expander records: a trace read while the
        // child was still working would otherwise keep claiming "nothing recorded" for the rest of the session.
        _requestTimeline(this);
    }
}
