using System.Windows;
using System.Windows.Controls;
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

    // A Popup does not follow its PlacementTarget once it is open, so scrolling the editor would leave both
    // pickers floating over unrelated fields.
    private void EditorScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0 && e.HorizontalChange == 0) return;

        EmojiPopup.IsOpen = false;
        ColorPopup.IsOpen = false;
    }

    // Picking a swatch inside a popup fires the bound command (which sets the value) and bubbles
    // here so the popup closes — StaysOpen=False only dismisses on clicks *outside* the popup.
    private void OnEmojiPicked(object sender, RoutedEventArgs e) => EmojiPopup.IsOpen = false;

    private void OnAccentPicked(object sender, RoutedEventArgs e) => ColorPopup.IsOpen = false;
}
