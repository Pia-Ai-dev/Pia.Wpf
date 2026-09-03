using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Pia.Helpers;

namespace Pia.Views;

public partial class RoutinesView : UserControl
{
    private INotifyPropertyChanged? _watched;

    public RoutinesView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    // A TextBox colours a substring only through its selection, so the view has to place it. Re-hooked on every
    // DataContext change rather than once: a re-hosted view keeps the old view model's subscription otherwise.
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_watched is not null) _watched.PropertyChanged -= OnViewModelPropertyChanged;
        _watched = e.NewValue as INotifyPropertyChanged;
        if (_watched is not null) _watched.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != "IsGoalEditing") return;
        if (DataContext is not ViewModels.RoutinesViewModel { IsGoalEditing: true }) return;

        // The click landed on the preview, which is about to be collapsed, so the caret has to be handed on or
        // the user is left typing into nothing. Deferred: the box is still collapsed when this fires.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            EditorGoalBox.Focus();
            Keyboard.Focus(EditorGoalBox);
            EditorGoalBox.CaretIndex = EditorGoalBox.Text.Length;
        }), DispatcherPriority.Input);
    }

    // The blueprint card that opened the editor leaves the tree with the placeholder, so a keyboard user would be
    // left on nothing. Deferred because IsVisible has not reached the name box yet when the pane's own event fires.
    private void EditorPane_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true) return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            EditorNameBox.Focus();
            Keyboard.Focus(EditorNameBox);
        }), DispatcherPriority.Input);
    }

    // The capped tool list sits inside the editor's own ScrollViewer and would otherwise swallow the wheel at
    // its extent, stranding the user mid-pane. Hand it on once this list has nothing left to give that way.
    private void ToolPickerScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var inner = (ScrollViewer)sender;
        var atExtent = e.Delta < 0
            ? inner.VerticalOffset >= inner.ScrollableHeight
            : inner.VerticalOffset <= 0;
        if (!atExtent) return;

        e.Handled = true;
        VisualTreeHelper.GetParent(inner).FindAncestor<ScrollViewer>()?.RaiseEvent(
            new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = MouseWheelEvent,
                Source = inner,
            });
    }
}
