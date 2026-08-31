using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace Pia.Views.SettingsViews;

public partial class PersonasView : UserControl
{
    public PersonasView()
    {
        InitializeComponent();
    }

    // The button that opened the editor leaves the tree with the pane it sat in, so a keyboard user would be
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

    private void EditorScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0 && e.HorizontalChange == 0) return;

        Track(EmojiPopup, EmojiToggle);
        Track(ColorPopup, ColorToggle);
    }

    // A Popup does not follow its PlacementTarget once open. Nudging the offset is what forces WPF to
    // re-place it; only a target scrolled clear out of the pane closes, because closing on every scroll
    // would also lose the popup to the bring-into-view the opening click itself can trigger.
    private void Track(Popup popup, FrameworkElement target)
    {
        if (!popup.IsOpen) return;

        if (!target.IsDescendantOf(EditorScroller) || !IsInViewport(target))
        {
            popup.IsOpen = false;
            return;
        }

        var offset = popup.HorizontalOffset;
        popup.HorizontalOffset = offset + 1;
        popup.HorizontalOffset = offset;
    }

    private bool IsInViewport(FrameworkElement target) =>
        new Rect(EditorScroller.RenderSize).IntersectsWith(
            target.TransformToAncestor(EditorScroller).TransformBounds(new Rect(target.RenderSize)));

    // Picking a swatch inside a popup fires the bound command (which sets the value) and bubbles
    // here so the popup closes — StaysOpen=False only dismisses on clicks *outside* the popup.
    private void OnEmojiPicked(object sender, RoutedEventArgs e) => EmojiPopup.IsOpen = false;

    private void OnAccentPicked(object sender, RoutedEventArgs e) => ColorPopup.IsOpen = false;
}
