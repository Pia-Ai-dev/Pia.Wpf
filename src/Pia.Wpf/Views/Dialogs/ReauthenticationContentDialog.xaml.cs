using Pia.ViewModels;
using Wpf.Ui.Controls;

namespace Pia.Views.Dialogs;

public partial class ReauthenticationContentDialog : ContentDialog
{
    public ReauthenticationViewModel ViewModel { get; }

    public ReauthenticationContentDialog(ContentDialogHost dialogHost, ReauthenticationViewModel viewModel)
        : base(dialogHost)
    {
        ViewModel = viewModel;
        DataContext = ViewModel;
        InitializeComponent();

        // PasswordBox.Password is not a dependency property, so it cannot be bound.
        PasswordInput.Password = ViewModel.Password;
        PasswordInput.PasswordChanged += (_, _) => ViewModel.Password = PasswordInput.Password;
    }
}
