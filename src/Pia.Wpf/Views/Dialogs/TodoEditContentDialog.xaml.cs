using Pia.ViewModels.Models;
using Wpf.Ui.Controls;

namespace Pia.Views.Dialogs;

public partial class TodoEditContentDialog : ContentDialog
{
    public TodoEditModel EditModel { get; }

    public TodoEditContentDialog(ContentDialogHost dialogHost, TodoEditModel editModel)
        : base(dialogHost)
    {
        EditModel = editModel;
        DataContext = EditModel;
        InitializeComponent();

        Closing += OnClosing;
    }

    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (args.Result != ContentDialogResult.Primary)
            return;

        if (string.IsNullOrWhiteSpace(EditModel.Title))
            args.Cancel = true;
    }
}
