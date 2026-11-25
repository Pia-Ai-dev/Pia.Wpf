using Pia.ViewModels;
using Wpf.Ui.Controls;

namespace Pia.Views.Dialogs;

public partial class ConsentCopiesContentDialog : ContentDialog
{
    public ConsentCopiesViewModel ViewModel { get; }

    public ConsentCopiesContentDialog(ContentDialogHost dialogHost, ConsentCopiesViewModel viewModel)
        : base(dialogHost)
    {
        ViewModel = viewModel;
        DataContext = ViewModel;
        InitializeComponent();
    }
}
