using System.Collections.Specialized;
using System.Windows.Controls;
using Pia.ViewModels;

namespace Pia.Views;

public partial class LiveTranscriptionOverlay : UserControl
{
    public LiveTranscriptionOverlay()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is LiveTranscriptionViewModel oldVm)
            ((INotifyCollectionChanged)oldVm.Bubbles).CollectionChanged -= OnBubblesChanged;

        if (e.NewValue is LiveTranscriptionViewModel newVm)
            ((INotifyCollectionChanged)newVm.Bubbles).CollectionChanged += OnBubblesChanged;
    }

    private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is LiveTranscriptionViewModel vm)
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
