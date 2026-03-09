using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace Pia.Views.Dialogs;

public partial class TranscribingContentDialog : ContentDialog
{
    public TranscribingContentDialog(
        ContentPresenter contentPresenter)
        : base(contentPresenter)
    {
        InitializeComponent();
    }
}
