using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Pia.Models;
using Pia.ViewModels;
using Pia.ViewModels.Models;

namespace Pia.Views;

public partial class TodoView : UserControl
{
    private const double MinColumnWidth = 200;
    private const double MaxColumnWidth = 600;

    public TodoView()
    {
        InitializeComponent();
    }

    private void OnColumnResizeThumbDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is not Thumb thumb || thumb.DataContext is not KanbanColumnViewModel columnVm)
            return;

        var newWidth = columnVm.Width + e.HorizontalChange;
        if (newWidth < MinColumnWidth) newWidth = MinColumnWidth;
        if (newWidth > MaxColumnWidth) newWidth = MaxColumnWidth;
        columnVm.Width = newWidth;
    }

    private void OnColumnTodoListLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ItemsControl itemsControl || DataContext is not TodoViewModel vm)
            return;

        var columnVm = itemsControl.DataContext as KanbanColumnViewModel;
        if (columnVm is null) return;

        Behaviors.KanbanDragDropBehavior.SetReorderCallback(itemsControl,
            async (oldIndex, newIndex) => await vm.ReorderWithinColumnAsync(columnVm.Id, oldIndex, newIndex));

        Behaviors.KanbanDragDropBehavior.SetMoveToColumnCallback(itemsControl,
            async (dragItem, targetColumnId, dropIndex) =>
            {
                if (dragItem is TodoItem todo && Guid.TryParse(targetColumnId, out var targetGuid))
                    await vm.MoveTodoToColumnAsync(todo, targetGuid, dropIndex);
            });
    }

    private void OnTodoCheckBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkBox && checkBox.Tag is TodoItem todo)
            checkBox.IsChecked = todo.Status == TodoStatus.Completed;
    }

    private async void OnTodoCheckBoxUnchecked(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox || checkBox.Tag is not TodoItem todo)
            return;

        // Skip if already pending (e.g., initial load for non-completed items)
        if (todo.Status == TodoStatus.Pending)
            return;

        if (DataContext is TodoViewModel vm)
            await vm.UncompleteTodoCommand.ExecuteAsync(todo);
    }

    private async void OnTodoCheckBoxChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox || checkBox.Tag is not TodoItem todo)
            return;

        // Skip if already completed (e.g., initial load setting IsChecked in closed column)
        if (todo.Status == TodoStatus.Completed)
            return;

        checkBox.IsEnabled = false;

        try
        {
            var itemBorder = FindAncestorByName<Border>(checkBox, "TodoCardBorder");
            if (itemBorder is null) return;

            var strikethrough = FindChild<Line>(itemBorder, "StrikethroughLine");
            var titleBlock = FindChild<TextBlock>(itemBorder, "TodoTitle");

            if (strikethrough is not null && titleBlock is not null)
            {
                strikethrough.Visibility = Visibility.Visible;
                var titleWidth = titleBlock.ActualWidth;

                var strikeAnim = new DoubleAnimation(0, titleWidth, TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                strikethrough.BeginAnimation(Line.X2Property, strikeAnim);

                await Task.Delay(200);
            }

            var fadeAnim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300))
            {
                BeginTime = TimeSpan.FromMilliseconds(150)
            };

            var currentHeight = itemBorder.ActualHeight;
            var collapseAnim = new DoubleAnimation(currentHeight, 0, TimeSpan.FromMilliseconds(250))
            {
                BeginTime = TimeSpan.FromMilliseconds(150),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };

            var marginAnim = new ThicknessAnimation(itemBorder.Margin, new Thickness(0), TimeSpan.FromMilliseconds(250))
            {
                BeginTime = TimeSpan.FromMilliseconds(150),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };

            var tcs = new TaskCompletionSource();
            collapseAnim.Completed += (_, _) => tcs.SetResult();

            itemBorder.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
            itemBorder.BeginAnimation(FrameworkElement.MaxHeightProperty, collapseAnim);
            itemBorder.BeginAnimation(FrameworkElement.MarginProperty, marginAnim);

            await tcs.Task;

            if (DataContext is TodoViewModel vm)
            {
                await vm.CompleteTodoCommand.ExecuteAsync(todo);
            }
        }
        catch (Exception)
        {
            checkBox.IsEnabled = true;
            checkBox.IsChecked = false;
        }
    }

    private void OnClosedColumnClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is KanbanColumnViewModel columnVm)
            columnVm.IsExpanded = true;
    }

    private static bool TryGetDragTodo(DragEventArgs e, out TodoItem todo, out string sourceColumnId)
    {
        todo = null!;
        sourceColumnId = string.Empty;
        if (!e.Data.GetDataPresent("DragItem") || !e.Data.GetDataPresent("SourceColumnId"))
            return false;
        if (e.Data.GetData("DragItem") is not TodoItem t) return false;
        todo = t;
        sourceColumnId = (string)e.Data.GetData("SourceColumnId")!;
        return true;
    }

    private void OnClosedCollapsedDragEnter(object sender, DragEventArgs e)
    {
        if (sender is Border border && TryGetDragTodo(e, out _, out var sourceId)
            && border.Tag is KanbanColumnViewModel columnVm
            && columnVm.Id.ToString() != sourceId)
        {
            border.BorderBrush = (Brush)FindResource("PiaAccentBrush");
            border.Background = (Brush)FindResource("SurfaceBrush");
            e.Effects = DragDropEffects.Move;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void OnClosedCollapsedDragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetDragTodo(e, out _, out _) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnClosedCollapsedDragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border border)
        {
            border.BorderBrush = (Brush)FindResource("BorderBrush_");
            border.Background = (Brush)FindResource("SurfaceMutedBrush");
        }
        e.Handled = true;
    }

    private async void OnClosedCollapsedDrop(object sender, DragEventArgs e)
    {
        if (sender is not Border border) return;

        border.BorderBrush = (Brush)FindResource("BorderBrush_");
        border.Background = (Brush)FindResource("SurfaceMutedBrush");
        e.Handled = true;

        if (!TryGetDragTodo(e, out var todo, out var sourceId)) return;
        if (border.Tag is not KanbanColumnViewModel columnVm) return;
        if (columnVm.Id.ToString() == sourceId) return;
        if (DataContext is not TodoViewModel vm) return;

        await vm.MoveTodoToColumnAsync(todo, columnVm.Id, columnVm.Todos.Count);
    }

    private void OnCollapseClosedClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is KanbanColumnViewModel columnVm)
            columnVm.IsExpanded = false;
    }

    private void OnColumnMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.ContextMenu is not null)
        {
            fe.ContextMenu.PlacementTarget = fe;
            fe.ContextMenu.IsOpen = true;
        }
    }

    private async void OnSetDefaultColumnClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: KanbanColumnViewModel columnVm }
            && DataContext is TodoViewModel vm)
            await vm.SetDefaultViewColumnCommand.ExecuteAsync(columnVm);
    }

    private async void OnDeleteColumnClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: KanbanColumnViewModel columnVm }
            && DataContext is TodoViewModel vm)
            await vm.DeleteColumnCommand.ExecuteAsync(columnVm);
    }

    private async void OnRenameColumnClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: KanbanColumnViewModel columnVm }
            && DataContext is TodoViewModel vm)
            await vm.RenameColumnCommand.ExecuteAsync(columnVm);
    }

    private static T? FindAncestorByName<T>(DependencyObject? obj, string name) where T : FrameworkElement
    {
        while (obj is not null)
        {
            obj = VisualTreeHelper.GetParent(obj);
            if (obj is T fe && fe.Name == name) return fe;
        }
        return null;
    }

    private static T? FindChild<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T fe && fe.Name == name) return fe;
            var result = FindChild<T>(child, name);
            if (result is not null) return result;
        }
        return null;
    }
}
