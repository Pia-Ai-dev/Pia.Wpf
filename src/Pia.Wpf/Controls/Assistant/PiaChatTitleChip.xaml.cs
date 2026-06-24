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
