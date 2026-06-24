using System.Windows.Controls;
using System.Windows.Input;
using Pia.ViewModels;

namespace Pia.Controls.Assistant;

public partial class PiaChatTitleChip : UserControl
{
    public PiaChatTitleChip() => InitializeComponent();

    private void ChipButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is ChatTitleChipViewModel vm)
            vm.IsFlyoutOpen = !vm.IsFlyoutOpen;
    }

    private void FlyoutPopup_Opened(object? sender, EventArgs e)
    {
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
    }

    private void WorkingDirButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is ChatTitleChipViewModel vm)
            vm.IsPickerOpen = !vm.IsPickerOpen;
    }
}
