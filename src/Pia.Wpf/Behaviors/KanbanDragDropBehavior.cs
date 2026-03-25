using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Pia.Behaviors;

/// <summary>
/// Attached behavior that enables drag-and-drop on kanban-style boards.
/// Supports both within-column reordering and cross-column moves.
/// </summary>
public static class KanbanDragDropBehavior
{
    private static readonly TimeSpan HoldThreshold = TimeSpan.FromMilliseconds(150);

    private class DragState
    {
        public Point StartPoint;
        public int DragIndex = -1;
        public bool IsDragging;
        public bool HoldCompleted;
        public DispatcherTimer? HoldTimer;
        public UIElement? PressedElement;
    }

    private static readonly DependencyProperty DragStateProperty =
        DependencyProperty.RegisterAttached("DragState", typeof(DragState), typeof(KanbanDragDropBehavior));

    private static DragState GetOrCreateState(DependencyObject obj)
    {
        var state = (DragState?)obj.GetValue(DragStateProperty);
        if (state is null)
        {
            state = new DragState();
            obj.SetValue(DragStateProperty, state);
        }
        return state;
    }

    // Attached property: IsEnabled
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(KanbanDragDropBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    // Attached property: ColumnId
    public static readonly DependencyProperty ColumnIdProperty =
        DependencyProperty.RegisterAttached("ColumnId", typeof(string), typeof(KanbanDragDropBehavior),
            new PropertyMetadata(string.Empty));

    public static string GetColumnId(DependencyObject obj) => (string)obj.GetValue(ColumnIdProperty);
    public static void SetColumnId(DependencyObject obj, string value) => obj.SetValue(ColumnIdProperty, value);

    // Attached property: ReorderCallback (Func<int, int, Task>) — within-column reorder
    public static readonly DependencyProperty ReorderCallbackProperty =
        DependencyProperty.RegisterAttached("ReorderCallback", typeof(Func<int, int, Task>), typeof(KanbanDragDropBehavior));

    public static Func<int, int, Task>? GetReorderCallback(DependencyObject obj) => (Func<int, int, Task>?)obj.GetValue(ReorderCallbackProperty);
    public static void SetReorderCallback(DependencyObject obj, Func<int, int, Task>? value) => obj.SetValue(ReorderCallbackProperty, value);

    // Attached property: MoveToColumnCallback (Func<object, string, int, Task>) — cross-column move
    public static readonly DependencyProperty MoveToColumnCallbackProperty =
        DependencyProperty.RegisterAttached("MoveToColumnCallback", typeof(Func<object, string, int, Task>), typeof(KanbanDragDropBehavior));

    public static Func<object, string, int, Task>? GetMoveToColumnCallback(DependencyObject obj) => (Func<object, string, int, Task>?)obj.GetValue(MoveToColumnCallbackProperty);
    public static void SetMoveToColumnCallback(DependencyObject obj, Func<object, string, int, Task>? value) => obj.SetValue(MoveToColumnCallbackProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl itemsControl) return;

        if ((bool)e.NewValue)
        {
            itemsControl.PreviewMouseLeftButtonDown += OnPreviewMouseDown;
            itemsControl.PreviewMouseMove += OnPreviewMouseMove;
            itemsControl.PreviewMouseLeftButtonUp += OnPreviewMouseUp;
            itemsControl.AllowDrop = true;
            itemsControl.Drop += OnDrop;
            itemsControl.DragOver += OnDragOver;
        }
        else
        {
            itemsControl.PreviewMouseLeftButtonDown -= OnPreviewMouseDown;
            itemsControl.PreviewMouseMove -= OnPreviewMouseMove;
            itemsControl.PreviewMouseLeftButtonUp -= OnPreviewMouseUp;
            itemsControl.Drop -= OnDrop;
            itemsControl.DragOver -= OnDragOver;
        }
    }

    private static void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ItemsControl itemsControl) return;
        var state = GetOrCreateState(itemsControl);

        // Don't start drag if clicking on a CheckBox
        if (e.OriginalSource is DependencyObject source && FindAncestor<CheckBox>(source) is not null)
            return;

        var container = FindItemContainer(itemsControl, e.OriginalSource as DependencyObject);
        if (container is null) return;

        state.StartPoint = e.GetPosition(itemsControl);
        state.DragIndex = itemsControl.ItemContainerGenerator.IndexFromContainer(container);
        state.PressedElement = container;
        state.HoldCompleted = false;

        state.HoldTimer = new DispatcherTimer { Interval = HoldThreshold };
        state.HoldTimer.Tick += (_, _) =>
        {
            state.HoldTimer.Stop();
            state.HoldCompleted = true;
        };
        state.HoldTimer.Start();
    }

    private static void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not ItemsControl itemsControl) return;
        var state = GetOrCreateState(itemsControl);

        if (state.DragIndex < 0) return;
        if (!state.HoldCompleted) return;

        var currentPos = e.GetPosition(itemsControl);
        var diff = state.StartPoint - currentPos;

        if (e.LeftButton == MouseButtonState.Pressed && !state.IsDragging
            && (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance
                || Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance))
        {
            state.IsDragging = true;

            if (state.PressedElement is not null)
                state.PressedElement.Opacity = 0.4;

            var dragItem = itemsControl.Items[state.DragIndex];
            var sourceColumnId = GetColumnId(itemsControl);

            var data = new DataObject();
            data.SetData("DragIndex", state.DragIndex);
            data.SetData("SourceColumnId", sourceColumnId);
            data.SetData("DragItem", dragItem!);

            DragDrop.DoDragDrop(itemsControl, data, DragDropEffects.Move);

            if (state.PressedElement is not null)
                state.PressedElement.Opacity = 1.0;
            ResetState(state);
        }
    }

    private static void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private static void OnDrop(object sender, DragEventArgs e)
    {
        if (sender is not ItemsControl itemsControl) return;
        if (!e.Data.GetDataPresent("DragIndex") || !e.Data.GetDataPresent("SourceColumnId")) return;

        var oldIndex = (int)e.Data.GetData("DragIndex")!;
        var sourceColumnId = (string)e.Data.GetData("SourceColumnId")!;
        var targetColumnId = GetColumnId(itemsControl);

        // Determine drop index from the container at the drop position
        var dropContainer = FindItemContainer(itemsControl, e.OriginalSource as DependencyObject);
        var dropIndex = dropContainer is not null
            ? itemsControl.ItemContainerGenerator.IndexFromContainer(dropContainer)
            : itemsControl.Items.Count;

        if (sourceColumnId == targetColumnId)
        {
            // Within-column reorder
            if (dropIndex < 0 || oldIndex == dropIndex) return;

            var callback = GetReorderCallback(itemsControl);
            callback?.Invoke(oldIndex, dropIndex);
        }
        else
        {
            // Cross-column move
            if (!e.Data.GetDataPresent("DragItem")) return;
            var dragItem = e.Data.GetData("DragItem")!;

            if (dropIndex < 0)
                dropIndex = itemsControl.Items.Count;

            var moveCallback = GetMoveToColumnCallback(itemsControl);
            moveCallback?.Invoke(dragItem, targetColumnId, dropIndex);
        }
    }

    private static void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ItemsControl itemsControl) return;
        var state = GetOrCreateState(itemsControl);

        state.HoldTimer?.Stop();
        ResetState(state);
    }

    private static void ResetState(DragState state)
    {
        state.DragIndex = -1;
        state.IsDragging = false;
        state.HoldCompleted = false;
        state.PressedElement = null;
        state.HoldTimer?.Stop();
        state.HoldTimer = null;
    }

    private static FrameworkElement? FindItemContainer(ItemsControl itemsControl, DependencyObject? source)
    {
        while (source is not null && source != itemsControl)
        {
            if (source is FrameworkElement fe && itemsControl.ItemContainerGenerator.IndexFromContainer(fe) >= 0)
                return fe;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? obj) where T : DependencyObject
    {
        while (obj is not null)
        {
            if (obj is T target) return target;
            obj = VisualTreeHelper.GetParent(obj);
        }
        return null;
    }
}
