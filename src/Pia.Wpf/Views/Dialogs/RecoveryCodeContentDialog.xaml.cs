using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pia.Services.Interfaces;
using Wpf.Ui.Controls;

namespace Pia.Views.Dialogs;

public partial class RecoveryCodeContentDialog : ContentDialog
{
    public RecoveryCodeContentDialog(ContentDialogHost dialogHost, string recoveryCode, IOutputService outputService)
        : base(dialogHost)
    {
        DataContext = new RecoveryCodeDialogViewModel(recoveryCode, outputService);
        InitializeComponent();

        Closing += OnClosing;
    }

    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (DataContext is RecoveryCodeDialogViewModel vm && !vm.HasConfirmedRecoveryCode)
        {
            args.Cancel = true;
        }
    }

    private sealed partial class RecoveryCodeDialogViewModel : ObservableObject
    {
        private readonly IOutputService _outputService;

        public RecoveryCodeDialogViewModel(string recoveryCode, IOutputService outputService)
        {
            RecoveryCode = recoveryCode;
            _outputService = outputService;
        }

        public string RecoveryCode { get; }

        [ObservableProperty]
        private bool _hasConfirmedRecoveryCode;

        [RelayCommand]
        private Task CopyRecoveryCodeAsync() => _outputService.CopyToClipboardAsync(RecoveryCode);
    }
}
