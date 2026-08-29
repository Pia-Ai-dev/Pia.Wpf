using Pia.ViewModels.Models;
using Wpf.Ui.Controls;

namespace Pia.Views.Dialogs;

public partial class AiFeedbackContentDialog : ContentDialog
{
    public AiFeedbackEditModel EditModel { get; }

    public AiFeedbackContentDialog(ContentDialogHost dialogHost, AiFeedbackEditModel editModel)
        : base(dialogHost)
    {
        EditModel = editModel;
        DataContext = EditModel;
        InitializeComponent();
    }
}
