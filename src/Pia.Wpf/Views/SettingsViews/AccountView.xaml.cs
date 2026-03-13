using System.Windows.Controls;
using Pia.ViewModels;

namespace Pia.Views.SettingsViews;

public partial class AccountView : UserControl
{
    public AccountView()
    {
        InitializeComponent();
    }

    private void LoginPasswordBox_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is AccountSettingsViewModel vm)
            vm.LoginPassword = ((PasswordBox)sender).Password;
    }
}
