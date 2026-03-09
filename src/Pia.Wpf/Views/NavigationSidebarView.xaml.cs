using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Pia.Views;

public partial class NavigationSidebarView : UserControl
{
    public NavigationSidebarView()
    {
        InitializeComponent();

        NewWindowNavItem.PreviewMouseLeftButtonUp += OnNewWindowNavItemClick;
    }

    private void OnNewWindowNavItemClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element && element.ContextMenu is not null)
        {
            element.ContextMenu.PlacementTarget = element;
            element.ContextMenu.Placement = PlacementMode.Right;
            element.ContextMenu.IsOpen = true;
            e.Handled = true;
        }
    }
}
