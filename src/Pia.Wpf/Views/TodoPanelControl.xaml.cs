using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Microsoft.Extensions.DependencyInjection;
using Pia.Models;
using Pia.Navigation;
using Pia.ViewModels;

namespace Pia.Views;

public partial class TodoPanelControl : UserControl
{
    public TodoPanelControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        var window = Window.GetWindow(this);
        if (window is null)
            return;

        var scopedProvider = ViewModelLocator.GetScopedServiceProvider(window);
        if (scopedProvider is null)
            return;

        var vm = scopedProvider.GetService<TodoViewModel>();
        DataContext = vm;

        // Wire drag-and-drop reorder callback
        if (FindName("PendingTodosList") is ItemsControl todoList && vm is not null)
        {
            Behaviors.DragDropReorderBehavior.SetReorderCallback(todoList,
                async (oldIndex, newIndex) => await vm.ReorderTodosAsync(oldIndex, newIndex));
        }

        if (vm is not null && vm.PendingCount == 0)
            await vm.LoadTodosAsync();
    }

    private async void OnTodoCheckBoxChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox || checkBox.Tag is not TodoItem todo)
            return;

        // Find the parent border (the todo item card)
        var itemBorder = FindAncestor<Border>(checkBox);
        if (itemBorder is null) return;

        // Find the strikethrough line and title
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

        // Fade out
        var fadeAnim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300))
        {
            BeginTime = TimeSpan.FromMilliseconds(150)
        };

        // Row collapse
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

        // Execute the complete command
        if (DataContext is TodoViewModel vm)
        {
            var pendingCountBefore = vm.PendingTodos.Count;
            await vm.CompleteTodoCommand.ExecuteAsync(todo);

            // Check if the completion actually succeeded
            if (vm.PendingTodos.Contains(todo) || vm.PendingTodos.Count == pendingCountBefore)
            {
                // Revert animation on failure
                itemBorder.BeginAnimation(UIElement.OpacityProperty, null);
                itemBorder.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
                itemBorder.BeginAnimation(FrameworkElement.MarginProperty, null);
                itemBorder.Opacity = 1;
                itemBorder.Margin = new Thickness(8, 0, 8, 3);
                if (strikethrough is not null)
                    strikethrough.Visibility = Visibility.Collapsed;
                checkBox.IsChecked = false;
            }
        }
    }

    private static T? FindAncestor<T>(DependencyObject? obj) where T : DependencyObject
    {
        while (obj is not null)
        {
            obj = VisualTreeHelper.GetParent(obj);
            if (obj is T target) return target;
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
