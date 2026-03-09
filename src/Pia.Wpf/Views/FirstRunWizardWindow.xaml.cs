using System.Windows;
using Pia.ViewModels;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace Pia.Views;

public partial class FirstRunWizardWindow : FluentWindow
{
    private readonly FirstRunWizardViewModel _viewModel;

    public FirstRunWizardWindow(
        FirstRunWizardViewModel viewModel,
        IContentDialogService contentDialogService,
        ISnackbarService snackbarService)
    {
        _viewModel = viewModel;
        DataContext = viewModel;

        InitializeComponent();

        contentDialogService.SetDialogHost(RootContentDialogPresenter);
        snackbarService.SetSnackbarPresenter(RootSnackbarPresenter);

        _viewModel.WizardCompleted += OnWizardCompleted;
    }

    private void OnWizardCompleted()
    {
        _viewModel.WizardCompleted -= OnWizardCompleted;
        Dispatcher.Invoke(() => DialogResult = true);
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.WizardCompleted -= OnWizardCompleted;
        base.OnClosed(e);
    }
}
