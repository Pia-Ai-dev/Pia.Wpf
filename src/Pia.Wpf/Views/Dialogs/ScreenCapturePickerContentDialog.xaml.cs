using Pia.ViewModels;
using Wpf.Ui.Controls;

namespace Pia.Views.Dialogs;

public partial class ScreenCapturePickerContentDialog : ContentDialog
{
    public ScreenCapturePickerViewModel ViewModel { get; }

    public ScreenCapturePickerContentDialog(ContentDialogHost dialogHost, ScreenCapturePickerViewModel viewModel)
        : base(dialogHost)
    {
        ViewModel = viewModel;
        DataContext = ViewModel;
        InitializeComponent();
    }
}
