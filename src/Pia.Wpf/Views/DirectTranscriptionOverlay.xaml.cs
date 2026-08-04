using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Controls;
using Pia.Models;
using Pia.ViewModels;

namespace Pia.Views;

/// <summary>
/// Code-behind for the direct-transcription overlay. Holds only the auto-scroll wiring — everything
/// else (state, commands, chips, stats) lives in <see cref="DirectTranscriptionViewModel"/>. The
/// auto-scroll rules are the same as <c>MeetingAttendeeOverlay</c>'s, deliberately: an unconditional
/// <c>ScrollToEnd</c> on every insert yanked the viewport away from a user who had scrolled up to
/// re-read something, and following only collection changes stopped following a speaker who kept
/// talking (a same-speaker utterance <c>Append</c>s to the existing tail bubble, which raises a
/// property change, not a collection change).
/// </summary>
public partial class DirectTranscriptionOverlay : UserControl
{
    /// <summary>Slack (in DIPs) allowed between the viewer offset and the bottom while still
    /// counting as "pinned to bottom"; absorbs sub-pixel layout rounding.</summary>
    private const double AtBottomEpsilon = 1.0;

    /// <summary>Bubbles whose <see cref="INotifyPropertyChanged"/> we are currently observing, so growth
    /// of an existing bubble keeps the view pinned. Tracked explicitly so unsubscription is symmetric
    /// with the collection's front-trim (<c>RemoveAt(0)</c> past the bubble cap) and no handler leaks for
    /// the session's life.</summary>
    private readonly HashSet<TranscriptBubble> _trackedBubbles = new();

    public DirectTranscriptionOverlay()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is DirectTranscriptionViewModel oldVm)
        {
            ((INotifyCollectionChanged)oldVm.Bubbles).CollectionChanged -= OnBubblesChanged;
            UntrackAllBubbles();
        }

        if (e.NewValue is DirectTranscriptionViewModel newVm)
            ((INotifyCollectionChanged)newVm.Bubbles).CollectionChanged += OnBubblesChanged;
    }

    private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is DirectTranscriptionViewModel vm)
            ((INotifyCollectionChanged)vm.Bubbles).CollectionChanged -= OnBubblesChanged;
        UntrackAllBubbles();
        DataContextChanged -= OnDataContextChanged;
        Unloaded -= OnUnloaded;
    }

    private void OnBubblesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (TranscriptBubble bubble in e.OldItems)
                Untrack(bubble);
        }

        // A Reset (a Clear, or the journal rebuild a revoke triggers) carries no OldItems, so drop every
        // tracked handler.
        if (e.Action == NotifyCollectionChangedAction.Reset)
            UntrackAllBubbles();

        if (e.NewItems is not null)
        {
            foreach (TranscriptBubble bubble in e.NewItems)
                Track(bubble);
        }

        if (e.Action == NotifyCollectionChangedAction.Add && IsPinnedToBottom())
            Dispatcher.BeginInvoke(new Action(() => BubbleScroll.ScrollToEnd()));
    }

    private void Track(TranscriptBubble bubble)
    {
        if (_trackedBubbles.Add(bubble))
            bubble.PropertyChanged += OnBubblePropertyChanged;
    }

    private void Untrack(TranscriptBubble bubble)
    {
        if (_trackedBubbles.Remove(bubble))
            bubble.PropertyChanged -= OnBubblePropertyChanged;
    }

    private void UntrackAllBubbles()
    {
        foreach (var bubble in _trackedBubbles)
            bubble.PropertyChanged -= OnBubblePropertyChanged;
        _trackedBubbles.Clear();
    }

    /// <summary>
    /// Same-speaker utterances inside the rolling window <c>Append</c> to the existing tail bubble, which
    /// raises a <c>Text</c>/<c>EndTimestamp</c> property change rather than a collection add. Re-pin on
    /// that growth too, subject to the same was-the-user-already-at-the-bottom test.
    /// </summary>
    private void OnBubblePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(TranscriptBubble.Text) or nameof(TranscriptBubble.EndTimestamp)))
            return;

        if (IsPinnedToBottom())
            Dispatcher.BeginInvoke(new Action(() => BubbleScroll.ScrollToEnd()));
    }

    /// <summary>
    /// Whether the viewer is at (or within <see cref="AtBottomEpsilon"/> of) the bottom. Read
    /// SYNCHRONOUSLY from the change notification, before WPF's layout pass grows the extent, so it
    /// answers "was the user pinned to the bottom?" rather than "is the new content already visible?".
    /// </summary>
    private bool IsPinnedToBottom()
        => BubbleScroll.VerticalOffset >= BubbleScroll.ScrollableHeight - AtBottomEpsilon;
}
