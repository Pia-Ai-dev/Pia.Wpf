using Wpf.Ui.Controls;

namespace Pia.Views.Dialogs;

public partial class TranscribingContentDialog : ContentDialog
{
    public TranscribingContentDialog(
        ContentDialogHost dialogHost)
        : base(dialogHost)
    {
        InitializeComponent();
    }
}
