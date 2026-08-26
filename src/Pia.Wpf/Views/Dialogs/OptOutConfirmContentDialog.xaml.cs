using CommunityToolkit.Mvvm.ComponentModel;
using Wpf.Ui.Controls;

namespace Pia.Views.Dialogs;

/// <summary>A confirm dialog whose "don't ask again" tick is returned alongside the answer, so the caller
/// decides where the suppression is stored.</summary>
public partial class OptOutConfirmContentDialog : ContentDialog
{
    public OptOutConfirmContentDialog(
        ContentDialogHost dialogHost, string title, string message, string confirmText)
        : base(dialogHost)
    {
        DataContext = new OptOutConfirmDialogViewModel(title, message, confirmText);
        InitializeComponent();
    }

    public bool DontAskAgain => ((OptOutConfirmDialogViewModel)DataContext).DontAskAgain;

    private sealed partial class OptOutConfirmDialogViewModel : ObservableObject
    {
        public OptOutConfirmDialogViewModel(string title, string message, string confirmText)
        {
            Title = title;
            Message = message;
            ConfirmText = confirmText;
        }

        public string Title { get; }

        public string Message { get; }

        public string ConfirmText { get; }

        [ObservableProperty]
        private bool _dontAskAgain;
    }
}
