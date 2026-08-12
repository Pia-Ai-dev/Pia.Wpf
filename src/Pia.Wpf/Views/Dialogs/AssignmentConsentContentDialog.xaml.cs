using Pia.ViewModels;
using Wpf.Ui.Controls;

namespace Pia.Views.Dialogs;

public partial class AssignmentConsentContentDialog : ContentDialog
{
    public AssignmentConsentViewModel ViewModel { get; }

    public AssignmentConsentContentDialog(ContentDialogHost dialogHost, AssignmentConsentViewModel viewModel)
        : base(dialogHost)
    {
        ViewModel = viewModel;
        DataContext = ViewModel;
        InitializeComponent();
    }
}
