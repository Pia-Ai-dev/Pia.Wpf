using System.Collections.Specialized;
using System.Windows.Controls;
using Pia.ViewModels;

namespace Pia.Views;

/// <summary>
/// Thin copy of <see cref="LiveTranscriptionOverlay"/> typed to <see cref="MeetingAttendeeViewModel"/>.
/// A separate control (rather than reusing the live-transcription overlay) because the overlay's
/// code-behind casts its <c>DataContext</c> to a concrete ViewModel for the auto-scroll wiring; the
/// bubble template and converters it uses are App.xaml-global resources, so they work unchanged here.
/// </summary>
public partial class MeetingAttendeeOverlay : UserControl
{
    public MeetingAttendeeOverlay()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MeetingAttendeeViewModel oldVm)
            ((INotifyCollectionChanged)oldVm.Bubbles).CollectionChanged -= OnBubblesChanged;

        if (e.NewValue is MeetingAttendeeViewModel newVm)
            ((INotifyCollectionChanged)newVm.Bubbles).CollectionChanged += OnBubblesChanged;
    }

    private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is MeetingAttendeeViewModel vm)
            ((INotifyCollectionChanged)vm.Bubbles).CollectionChanged -= OnBubblesChanged;
        DataContextChanged -= OnDataContextChanged;
        Unloaded -= OnUnloaded;
    }

    private void OnBubblesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        Dispatcher.BeginInvoke(new Action(() => BubbleScroll.ScrollToEnd()));
    }
}
