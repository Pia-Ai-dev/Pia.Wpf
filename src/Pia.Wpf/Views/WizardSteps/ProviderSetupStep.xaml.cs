using System.Windows;
using System.Windows.Controls;
using Pia.ViewModels;

namespace Pia.Views.WizardSteps;

public partial class ProviderSetupStep : UserControl
{
    public ProviderSetupStep()
    {
        InitializeComponent();

        // PasswordBox cannot be data-bound in WPF — sync via event handler
        ApiKeyPasswordBox.PasswordChanged += OnApiKeyPasswordChanged;
    }

    private void OnApiKeyPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is FirstRunWizardViewModel vm)
            vm.ProviderApiKey = ApiKeyPasswordBox.Password;
    }
}
