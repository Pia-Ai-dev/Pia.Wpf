using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Pia.Views.SettingsViews;

public partial class TemplatesView : UserControl
{
    public TemplatesView()
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
}
