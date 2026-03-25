using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Microsoft.Extensions.DependencyInjection;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.ViewModels;
using Pia.ViewModels.Models;

namespace Pia.Views;

public partial class TodoView : UserControl
{
    public TodoView()
    {
        InitializeComponent();
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

    private async void OnTodoCheckBoxChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox || checkBox.Tag is not TodoItem todo)
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

    private void OnCollapseClosedClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is KanbanColumnViewModel columnVm)
            columnVm.IsExpanded = false;
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
        if (sender is not MenuItem { Tag: KanbanColumnViewModel columnVm }
            || DataContext is not TodoViewModel vm)
            return;

        try
        {
            var dialogService = Bootstrapper.ServiceProvider.GetRequiredService<IDialogService>();
            var locService = Bootstrapper.ServiceProvider.GetRequiredService<ILocalizationService>();

            var newName = await dialogService.ShowInputDialogAsync(
                locService["Kanban_RenameColumn"],
                locService["Kanban_ColumnNamePrompt"]);

            if (!string.IsNullOrWhiteSpace(newName))
                await vm.RenameColumnAsync(columnVm, newName);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Rename failed: {ex.Message}");
        }
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
