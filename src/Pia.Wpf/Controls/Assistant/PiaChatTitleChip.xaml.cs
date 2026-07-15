using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Pia.ViewModels;

namespace Pia.Controls.Assistant;

public partial class PiaChatTitleChip : UserControl
{
    public PiaChatTitleChip() => InitializeComponent();

    private void ChipButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ChatTitleChipViewModel vm)
            vm.IsFlyoutOpen = !vm.IsFlyoutOpen;
    }

    private void FlyoutPopup_Opened(object? sender, EventArgs e)
    {
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
    }

    private void WorkingDirButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ChatTitleChipViewModel vm)
            vm.IsPickerOpen = !vm.IsPickerOpen;
    }

    // When the drill-down opens, move keyboard focus into the folder list so the arrow keys
    // navigate folders instead of falling through to the chat-history list below the pill.
    private void WorkingDirPopup_Opened(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(FocusFirstEntry));

    // Header "+ folder" button: toggle the inline creation row. Opening it moves focus into the
    // name box; the second click (while creating) cancels.
    private void NewFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ChatTitleChipViewModel vm) return;
        var picker = vm.WorkingDirectoryPicker;
        if (picker.IsCreatingFolder)
        {
            picker.CancelCreateFolderCommand.Execute(null);
            return;
        }

        picker.BeginCreateFolderCommand.Execute(null);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            NewFolderTextBox.Focus();
            Keyboard.Focus(NewFolderTextBox);
        }));
    }

    // Enter confirms (when the name is valid), Escape cancels. Both may collapse the input row, so
    // focus is parked on the header button FIRST (see ParkFocusThenSettle) — never left on a
    // collapsing element, which would dismiss the StaysOpen=False popup.
    private void NewFolderTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not ChatTitleChipViewModel vm) return;
        var picker = vm.WorkingDirectoryPicker;

        switch (e.Key)
        {
            case Key.Enter:
                if (picker.ConfirmCreateFolderCommand.CanExecute(null))
                    ParkFocusThenSettle(() => picker.ConfirmCreateFolderCommand.Execute(null), selectCreated: true);
                e.Handled = true;
                break;

            case Key.Escape:
                ParkFocusThenSettle(() => picker.CancelCreateFolderCommand.Execute(null), selectCreated: false);
                e.Handled = true;
                break;
        }
    }

    // Click fires BEFORE the bound command runs (WPF raises Click, then executes the command), so
    // parking focus on the header button here moves it off the ✓/✕ button before the command
    // collapses the row. Then settle focus (failure → back to the textbox; success → highlight).
    private void ConfirmCreateFolderButton_Click(object sender, RoutedEventArgs e)
    {
        NewFolderButton.Focus();
        SettleFocusAfterCreate(selectCreated: true);
    }

    private void CancelCreateFolderButton_Click(object sender, RoutedEventArgs e)
    {
        NewFolderButton.Focus();
        SettleFocusAfterCreate(selectCreated: false);
    }

    // Park focus on the always-visible header button, THEN run the state change, THEN settle. Doing
    // the focus move synchronously before the command means the input row (textbox/✓/✕) never holds
    // focus while it collapses — the collapse-time focus gap is what would dismiss the popup.
    private void ParkFocusThenSettle(Action stateChange, bool selectCreated)
    {
        NewFolderButton.Focus();
        stateChange();
        SettleFocusAfterCreate(selectCreated);
    }

    // Runs after the command has applied. On a rejected create the VM keeps the row open, so return
    // focus to the textbox for amending; otherwise the row collapsed and focus stays on the header
    // button (already parked), and a created folder is selected/scrolled into view.
    private void SettleFocusAfterCreate(bool selectCreated)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (DataContext is not ChatTitleChipViewModel vm) return;
            var picker = vm.WorkingDirectoryPicker;

            if (picker.IsCreatingFolder)
            {
                // Create was rejected (service returned null): keep the user in the name box.
                NewFolderTextBox.Focus();
                Keyboard.Focus(NewFolderTextBox);
                return;
            }

            if (selectCreated && picker.LastCreatedFolder is { } leaf)
            {
                WorkingDirEntries.UpdateLayout();
                WorkingDirEntries.SelectedItem = leaf;
                if (WorkingDirEntries.SelectedItem is not null)
                    WorkingDirEntries.ScrollIntoView(WorkingDirEntries.SelectedItem);
            }

            // Belt-and-suspenders: focus was parked here synchronously before the collapse; re-assert
            // it in case selection/layout shifted it.
            NewFolderButton.Focus();
        }));
    }

    // Enter/Space drills into the highlighted folder; Backspace ascends one level; Escape closes
    // the picker. Arrow keys fall through to the ListBox's own selection navigation.
    private void WorkingDirEntries_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (WorkingDirEntries.DataContext is not WorkingDirectoryPickerViewModel picker)
            return;

        switch (e.Key)
        {
            case Key.Enter:
            case Key.Space:
                if (WorkingDirEntries.SelectedItem is string name && picker.EnterCommand.CanExecute(name))
                {
                    picker.EnterCommand.Execute(name);
                    e.Handled = true;
                    Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(FocusFirstEntry));
                }
                break;

            case Key.Back:
                // Crumbs = root + one per path segment; > 1 means we're below root and can ascend.
                if (picker.Crumbs.Count > 1)
                {
                    picker.JumpToCrumbCommand.Execute(picker.Crumbs.Count - 2);
                    e.Handled = true;
                    Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(FocusFirstEntry));
                }
                break;

            case Key.Escape:
                if (DataContext is ChatTitleChipViewModel vm)
                {
                    vm.IsPickerOpen = false;
                    WorkingDirButton.Focus();
                    e.Handled = true;
                }
                break;
        }
    }

    // Single click on a folder row drills into it (preserves the pre-ListBox mouse behaviour;
    // a plain ListBox would only select on a single click).
    private void WorkingDirEntries_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (WorkingDirEntries.DataContext is not WorkingDirectoryPickerViewModel picker)
            return;

        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is string name && picker.EnterCommand.CanExecute(name))
        {
            picker.EnterCommand.Execute(name);
            e.Handled = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(FocusFirstEntry));
        }
    }

    // Highlight + focus the first folder row so arrows work immediately. Re-run after every
    // drill/ascend because the entry list is rebuilt each time.
    private void FocusFirstEntry()
    {
        WorkingDirEntries.UpdateLayout();
        if (WorkingDirEntries.Items.Count == 0)
        {
            // Empty folder: keep focus on the list itself so Backspace/Escape still reach the
            // key handler.
            WorkingDirEntries.Focus();
            return;
        }

        WorkingDirEntries.SelectedIndex = 0;
        WorkingDirEntries.ScrollIntoView(WorkingDirEntries.SelectedItem);
        if (WorkingDirEntries.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem container)
            container.Focus();
        else
            WorkingDirEntries.Focus();
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null && current is not T)
            current = VisualTreeHelper.GetParent(current);
        return current as T;
    }
}
