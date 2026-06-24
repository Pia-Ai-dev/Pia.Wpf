using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Controls;
using Pia.Models;
using Pia.ViewModels;

namespace Pia.Views;

/// <summary>
/// Thin copy of <c>LiveTranscriptionOverlay</c> typed to <see cref="MeetingAttendeeViewModel"/>.
/// A separate control (rather than reusing the live-transcription overlay) because the overlay's
/// code-behind casts its <c>DataContext</c> to a concrete ViewModel for the auto-scroll wiring; the
/// bubble template and converters it uses are App.xaml-global resources, so they work unchanged here.
/// </summary>
public partial class MeetingAttendeeOverlay : UserControl
{
    /// <summary>Slack (in DIPs) allowed between the viewer offset and the bottom while still
    /// counting as "pinned to bottom"; absorbs sub-pixel layout rounding.</summary>
    private const double AtBottomEpsilon = 1.0;

    /// <summary>Bubbles whose <see cref="INotifyPropertyChanged"/> we are currently observing,
    /// so growth of an existing bubble (a same-speaker <c>Append</c>) can keep the view pinned to
    /// the bottom. Tracked explicitly to unsubscribe symmetrically with the collection's front-trim
    /// (<c>RemoveAt(0)</c> at &gt; 200 bubbles) and avoid leaking handlers for the session's life.</summary>
    private readonly HashSet<TranscriptBubble> _trackedBubbles = new();

    public MeetingAttendeeOverlay()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MeetingAttendeeViewModel oldVm)
        {
            ((INotifyCollectionChanged)oldVm.Bubbles).CollectionChanged -= OnBubblesChanged;
            UntrackAllBubbles();
        }

        if (e.NewValue is MeetingAttendeeViewModel newVm)
            ((INotifyCollectionChanged)newVm.Bubbles).CollectionChanged += OnBubblesChanged;
    }

    private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is MeetingAttendeeViewModel vm)
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

        // A Reset (e.g. a Clear) carries no OldItems, so drop every tracked handler.
        if (e.Action == NotifyCollectionChangedAction.Reset)
            UntrackAllBubbles();

        if (e.NewItems is not null)
        {
            foreach (TranscriptBubble bubble in e.NewItems)
                Track(bubble);
        }

        // Diarization adds a new bubble on every speaker switch, so re-pin to the latest bubble
        // only when the user was already at the bottom — gauged synchronously here (CollectionChanged
        // fires before WPF's layout pass grows the extent) so reading up history is never hijacked.
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            bool atBottom = BubbleScroll.VerticalOffset >= BubbleScroll.ScrollableHeight - AtBottomEpsilon;
            if (atBottom)
                Dispatcher.BeginInvoke(new Action(() => BubbleScroll.ScrollToEnd()));
        }
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
    /// Same-speaker utterances inside the rolling window <c>Append</c> to the existing tail bubble,
    /// which raises a <c>Text</c>/<c>EndTimestamp</c> <see cref="INotifyPropertyChanged"/> rather than a
    /// collection add. Re-pin to the bottom on that growth, but only when the user was already there —
    /// gauged synchronously (before WPF's layout pass grows the extent) so reading up history is never
    /// hijacked.
    /// </summary>
    private void OnBubblePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(TranscriptBubble.Text) or nameof(TranscriptBubble.EndTimestamp)))
            return;

        // Decide BEFORE the BeginInvoke: PropertyChanged fires before layout grows ScrollableHeight,
        // so this reads the pre-growth state and correctly answers "was the user pinned to the bottom?".
        bool atBottom = BubbleScroll.VerticalOffset >= BubbleScroll.ScrollableHeight - AtBottomEpsilon;
        if (atBottom)
            Dispatcher.BeginInvoke(new Action(() => BubbleScroll.ScrollToEnd()));
    }
}
