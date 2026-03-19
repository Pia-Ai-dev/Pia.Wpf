using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Pia.Models;
using Pia.ViewModels;

namespace Pia.Views;

public partial class TodoView : UserControl
{
    public TodoView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        if (DataContext is TodoViewModel vm && FindName("PendingTodosList") is ItemsControl todoList)
        {
            Behaviors.DragDropReorderBehavior.SetReorderCallback(todoList,
                async (oldIndex, newIndex) => await vm.ReorderTodosAsync(oldIndex, newIndex));
        }
    }

    private async void OnTodoCheckBoxChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox || checkBox.Tag is not TodoItem todo)
            return;

        checkBox.IsEnabled = false;

        try
        {
            var itemBorder = FindAncestorByName<Border>(checkBox, "TodoItemBorder");
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
                var pendingCountBefore = vm.PendingTodos.Count;
                await vm.CompleteTodoCommand.ExecuteAsync(todo);

                if (vm.PendingTodos.Any(t => t.Id == todo.Id) || vm.PendingTodos.Count == pendingCountBefore)
                {
                    itemBorder.BeginAnimation(UIElement.OpacityProperty, null);
                    itemBorder.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
                    itemBorder.BeginAnimation(FrameworkElement.MarginProperty, null);
                    itemBorder.Opacity = 1;
                    itemBorder.Margin = new Thickness(0, 0, 0, 4);
                    if (strikethrough is not null)
                        strikethrough.Visibility = Visibility.Collapsed;
                    checkBox.IsChecked = false;
                    checkBox.IsEnabled = true;
                }
            }
        }
        catch (Exception)
        {
            checkBox.IsEnabled = true;
            checkBox.IsChecked = false;
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
