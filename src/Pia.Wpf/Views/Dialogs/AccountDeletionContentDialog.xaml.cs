using Pia.ViewModels;
using Wpf.Ui.Controls;

namespace Pia.Views.Dialogs;

public partial class AccountDeletionContentDialog : ContentDialog
{
    public AccountDeletionViewModel ViewModel { get; }

    public AccountDeletionContentDialog(ContentDialogHost dialogHost, AccountDeletionViewModel viewModel)
        : base(dialogHost)
    {
        ViewModel = viewModel;
        DataContext = ViewModel;
        InitializeComponent();

        // PasswordBox.Password is not a dependency property, so it cannot be bound.
        PasswordInput.PasswordChanged += (_, _) => ViewModel.Password = PasswordInput.Password;
    }
}
