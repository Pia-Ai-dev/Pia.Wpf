using Pia.ViewModels.Models;
using Wpf.Ui.Controls;

namespace Pia.Views.Dialogs;

public partial class AnswerExportContentDialog : ContentDialog
{
    public AnswerExportEditModel EditModel { get; }

    public AnswerExportContentDialog(ContentDialogHost dialogHost, AnswerExportEditModel editModel)
        : base(dialogHost)
    {
        EditModel = editModel;
        DataContext = EditModel;
        InitializeComponent();
    }
}
