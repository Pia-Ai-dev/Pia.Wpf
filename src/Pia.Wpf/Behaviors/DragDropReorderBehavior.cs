using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Pia.Behaviors;

/// <summary>
/// Attached behavior that enables drag-and-drop reordering on an ItemsControl.
/// Calls a Func&lt;int, int, Task&gt; callback when items are reordered.
/// </summary>
public static class DragDropReorderBehavior
{
    private static readonly TimeSpan HoldThreshold = TimeSpan.FromMilliseconds(150);

    // Attached property: IsEnabled
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(DragDropReorderBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    // Attached property: ReorderCallback (Func<int, int, Task>)
    public static readonly DependencyProperty ReorderCallbackProperty =
        DependencyProperty.RegisterAttached("ReorderCallback", typeof(Func<int, int, Task>), typeof(DragDropReorderBehavior));

    public static Func<int, int, Task>? GetReorderCallback(DependencyObject obj) => (Func<int, int, Task>?)obj.GetValue(ReorderCallbackProperty);
    public static void SetReorderCallback(DependencyObject obj, Func<int, int, Task>? value) => obj.SetValue(ReorderCallbackProperty, value);

    private static Point _startPoint;
    private static int _dragIndex = -1;
    private static bool _isDragging;
    private static DispatcherTimer? _holdTimer;
    private static UIElement? _pressedElement;

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

        // Don't start drag if clicking on a CheckBox
        if (e.OriginalSource is DependencyObject source && FindAncestor<CheckBox>(source) is not null)
            return;

        var container = FindItemContainer(itemsControl, e.OriginalSource as DependencyObject);
        if (container is null) return;

        _startPoint = e.GetPosition(itemsControl);
        _dragIndex = itemsControl.ItemContainerGenerator.IndexFromContainer(container);
        _pressedElement = container;

        _holdTimer = new DispatcherTimer { Interval = HoldThreshold };
        _holdTimer.Tick += (_, _) =>
        {
            _holdTimer.Stop();
        };
        _holdTimer.Start();
    }

    private static void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragIndex < 0 || sender is not ItemsControl itemsControl) return;
        if (_holdTimer is { IsEnabled: true }) return;

        var currentPos = e.GetPosition(itemsControl);
        var diff = _startPoint - currentPos;

        if (e.LeftButton == MouseButtonState.Pressed && !_isDragging
            && (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance
                || Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance))
        {
            _isDragging = true;

            if (_pressedElement is not null)
                _pressedElement.Opacity = 0.4;

            var data = new DataObject("DragIndex", _dragIndex);
            DragDrop.DoDragDrop(itemsControl, data, DragDropEffects.Move);

            if (_pressedElement is not null)
                _pressedElement.Opacity = 1.0;
            ResetState();
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
        if (!e.Data.GetDataPresent("DragIndex")) return;

        var oldIndex = (int)e.Data.GetData("DragIndex")!;
        var dropContainer = FindItemContainer(itemsControl, e.OriginalSource as DependencyObject);
        if (dropContainer is null) return;

        var newIndex = itemsControl.ItemContainerGenerator.IndexFromContainer(dropContainer);
        if (newIndex < 0 || oldIndex == newIndex) return;

        var callback = GetReorderCallback(itemsControl);
        callback?.Invoke(oldIndex, newIndex);
    }

    private static void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _holdTimer?.Stop();
        if (!_isDragging) ResetState();
    }

    private static void ResetState()
    {
        _dragIndex = -1;
        _isDragging = false;
        _pressedElement = null;
        _holdTimer?.Stop();
        _holdTimer = null;
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
