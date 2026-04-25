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
            ((INotifyCollectionChanged)oldVm.Utterances).CollectionChanged -= OnUtterancesChanged;

        if (e.NewValue is LiveTranscriptionViewModel newVm)
            ((INotifyCollectionChanged)newVm.Utterances).CollectionChanged += OnUtterancesChanged;
    }

    private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is LiveTranscriptionViewModel vm)
            ((INotifyCollectionChanged)vm.Utterances).CollectionChanged -= OnUtterancesChanged;
        DataContextChanged -= OnDataContextChanged;
        Unloaded -= OnUnloaded;
    }

    private void OnUtterancesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        Dispatcher.BeginInvoke(new Action(() => BubbleScroll.ScrollToEnd()));
    }
}
