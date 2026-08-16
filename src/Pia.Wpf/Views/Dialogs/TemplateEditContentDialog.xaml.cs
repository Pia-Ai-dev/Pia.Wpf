using Pia.ViewModels.Models;
using Wpf.Ui.Controls;

namespace Pia.Views.Dialogs;

public partial class TemplateEditContentDialog : ContentDialog
{
    public TemplateEditModel EditModel { get; }

    public TemplateEditContentDialog(ContentDialogHost dialogHost, TemplateEditModel template)
        : base(dialogHost)
    {
        EditModel = template;
        DataContext = EditModel;
        InitializeComponent();
    }
}
