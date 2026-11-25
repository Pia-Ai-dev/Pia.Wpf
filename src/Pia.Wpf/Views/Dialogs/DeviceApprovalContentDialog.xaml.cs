using Pia.Models;
using Wpf.Ui.Controls;

namespace Pia.Views.Dialogs;

public partial class DeviceApprovalContentDialog : ContentDialog
{
    public DeviceApprovalContentDialog(ContentDialogHost dialogHost, DeviceApprovalPrompt prompt)
        : base(dialogHost)
    {
        DataContext = prompt;
        InitializeComponent();
    }
}
