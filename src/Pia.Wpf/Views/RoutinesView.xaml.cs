using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Pia.Helpers;

namespace Pia.Views;

public partial class RoutinesView : UserControl
{
    public RoutinesView()
    {
        InitializeComponent();
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
