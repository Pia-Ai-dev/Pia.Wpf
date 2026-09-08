using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Pia.ViewModels;

namespace Pia.Controls.Assistant;

public partial class PiaChatTitleChip : UserControl
{
    public PiaChatTitleChip() => InitializeComponent();

    // Read the POPUP, not the flag (and see the Closed handlers): a dismissal the flag misses leaves
    // the next press toggling a stale value and opening nothing.
    private void ChipButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ChatTitleChipViewModel vm)
            vm.IsFlyoutOpen = !FlyoutPopup.IsOpen;
    }

    private void FlyoutPopup_Opened(object? sender, EventArgs e)
    {
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
    }

    private void FlyoutPopup_Closed(object? sender, EventArgs e)
    {
        if (DataContext is ChatTitleChipViewModel vm)
            vm.IsFlyoutOpen = false;
    }

    private void WorkingDirButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ChatTitleChipViewModel vm)
            vm.IsPickerOpen = !WorkingDirPopup.IsOpen;
    }

    // When the drill-down opens, move keyboard focus into the folder list so the arrow keys
    // navigate folders instead of falling through to the chat-history list below the pill. Posted
    // only here: the popup's content is still being connected when Opened fires.
    private void WorkingDirPopup_Opened(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(WorkingDirPicker.FocusEntries));

    private void WorkingDirPopup_Closed(object? sender, EventArgs e)
    {
        if (DataContext is ChatTitleChipViewModel vm)
            vm.IsPickerOpen = false;
    }

    private void WorkingDirPicker_CloseRequested(object? sender, EventArgs e)
    {
        if (DataContext is ChatTitleChipViewModel vm)
        {
            vm.IsPickerOpen = false;
            WorkingDirButton.Focus();
        }
    }
}
