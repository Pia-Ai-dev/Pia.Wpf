using System.Windows.Controls;
using Pia.ViewModels;

namespace Pia.Views.SettingsViews;

public partial class SyncView : UserControl
{
    public SyncView()
    {
        InitializeComponent();
    }

    private void LoginPasswordBox_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
            vm.LoginPassword = ((PasswordBox)sender).Password;
    }
}
