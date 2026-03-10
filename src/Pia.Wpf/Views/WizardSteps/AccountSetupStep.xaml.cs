using System.Windows.Controls;
using Pia.ViewModels;

namespace Pia.Views.WizardSteps;

public partial class AccountSetupStep : UserControl
{
    public AccountSetupStep()
    {
        InitializeComponent();
    }

    private void LoginPasswordBox_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is FirstRunWizardViewModel vm)
            vm.LoginPassword = ((PasswordBox)sender).Password;
    }
}
