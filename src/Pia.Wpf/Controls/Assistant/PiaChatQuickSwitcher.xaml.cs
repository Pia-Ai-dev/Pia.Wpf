using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Pia.ViewModels;

namespace Pia.Controls.Assistant;

public partial class PiaChatQuickSwitcher : UserControl
{
    public PiaChatQuickSwitcher() => InitializeComponent();

    private ChatTitleChipViewModel? ViewModel => DataContext as ChatTitleChipViewModel;

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            QueryBox.Focus();
            Keyboard.Focus(QueryBox);
            QueryBox.SelectAll();
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        var vm = ViewModel;
        if (vm is null) return;

        switch (e.Key)
        {
            case Key.Down:
                vm.MoveSelectionCommand.Execute(1);
                e.Handled = true;
                break;
            case Key.Up:
                vm.MoveSelectionCommand.Execute(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                vm.ConfirmSelectionCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                vm.CloseQuickSwitcherCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private void OnMatchesDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ViewModel?.ConfirmSelectionCommand.Execute(null);
    }

    private void OnBackdropMouseDown(object sender, MouseButtonEventArgs e)
    {
        ViewModel?.CloseQuickSwitcherCommand.Execute(null);
    }

    private void OnPanelMouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }
}
