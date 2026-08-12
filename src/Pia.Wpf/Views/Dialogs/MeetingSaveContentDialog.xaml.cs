using Pia.ViewModels.Models;
using Wpf.Ui.Controls;

namespace Pia.Views.Dialogs;

public partial class MeetingSaveContentDialog : ContentDialog
{
    public MeetingSaveEditModel EditModel { get; }

    public MeetingSaveContentDialog(ContentDialogHost dialogHost, MeetingSaveEditModel editModel)
        : base(dialogHost)
    {
        EditModel = editModel;
        DataContext = EditModel;
        InitializeComponent();
    }
}
