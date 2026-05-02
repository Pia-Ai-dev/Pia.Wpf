using Wpf.Ui.Controls;

namespace Pia.Views.Dialogs;

public partial class MissedScheduledJobDialog : ContentDialog
{
    public string Body { get; }

    public MissedScheduledJobDialog(ContentDialogHost dialogHost, string body)
        : base(dialogHost)
    {
        Body = body;
        DataContext = this;
        InitializeComponent();
    }
}
