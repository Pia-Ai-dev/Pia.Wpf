using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Pia.ViewModels;

namespace Pia.Views.Controls;

public partial class ImageViewerOverlay : UserControl
{
    private ImageViewerViewModel? _viewModel;
    private bool _backdropPressed;
    private Point? _panStart;
    private Point _panStartOffset;

    public ImageViewerOverlay()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public async Task ShowAsync(ImageViewerViewModel viewModel)
    {
        var previousFocus = Keyboard.FocusedElement;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCloseRequested(object? sender, EventArgs e) => closed.TrySetResult();

        _viewModel = viewModel;
        viewModel.CloseRequested += OnCloseRequested;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        DataContext = viewModel;
        Visibility = Visibility.Visible;
        UpdateLayout();
        UpdateFitScale();
        Keyboard.Focus(this);

        try
        {
            await closed.Task;
        }
        finally
        {
            viewModel.CloseRequested -= OnCloseRequested;
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            EndPan();
            Visibility = Visibility.Collapsed;
            DataContext = null;
            _viewModel = null;

            if (previousFocus != null)
                Keyboard.Focus(previousFocus);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ImageViewerViewModel.CurrentIndex))
            UpdateFitScale();
    }

    private void OnScrollerSizeChanged(object sender, SizeChangedEventArgs e) => UpdateFitScale();

    private void UpdateFitScale()
    {
        if (_viewModel is not { CurrentWidth: > 0, CurrentHeight: > 0 } vm
            || ImageScroller.ViewportWidth <= 0 || ImageScroller.ViewportHeight <= 0)
            return;

        vm.FitScale = Math.Min(1.0, Math.Min(
            ImageScroller.ViewportWidth / vm.CurrentWidth,
            ImageScroller.ViewportHeight / vm.CurrentHeight));
    }

    private void OnScrollerPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_viewModel is null)
            return;

        Execute(e.Delta > 0 ? _viewModel.ZoomInCommand : _viewModel.ZoomOutCommand);
        e.Handled = true;
    }

    private void OnImageMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_viewModel is null || _viewModel.IsFitToWindow)
            return;

        _panStart = e.GetPosition(ImageScroller);
        _panStartOffset = new Point(ImageScroller.HorizontalOffset, ImageScroller.VerticalOffset);
        ViewedImage.CaptureMouse();
    }

    private void OnImageMouseMove(object sender, MouseEventArgs e)
    {
        if (_panStart is not { } start)
            return;

        var delta = e.GetPosition(ImageScroller) - start;
        ImageScroller.ScrollToHorizontalOffset(_panStartOffset.X - delta.X);
        ImageScroller.ScrollToVerticalOffset(_panStartOffset.Y - delta.Y);
    }

    private void OnImageMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        EndPan();
        e.Handled = true;
    }

    private void EndPan()
    {
        _panStart = null;
        if (ViewedImage.IsMouseCaptured)
            ViewedImage.ReleaseMouseCapture();
    }

    private void OnRootMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        _backdropPressed = IsBackdrop(e.OriginalSource);

    private void OnRootMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_backdropPressed && IsBackdrop(e.OriginalSource))
            Execute(_viewModel?.CloseCommand);
        _backdropPressed = false;
    }

    private bool IsBackdrop(object source)
    {
        for (var node = source as DependencyObject; node != null; node = ParentOf(node))
        {
            if (ReferenceEquals(node, ViewedImage) || node is ButtonBase or ScrollBar)
                return false;
            if (ReferenceEquals(node, this))
                return true;
        }
        return false;
    }

    private static DependencyObject? ParentOf(DependencyObject node) =>
        node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel is not { } vm)
            return;

        var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        ICommand? command = e.Key switch
        {
            Key.Escape => vm.CloseCommand,
            Key.Left => vm.PreviousCommand,
            Key.Right => vm.NextCommand,
            Key.OemPlus or Key.Add => vm.ZoomInCommand,
            Key.OemMinus or Key.Subtract => vm.ZoomOutCommand,
            Key.D0 or Key.NumPad0 => vm.FitToWindowCommand,
            Key.D1 or Key.NumPad1 => vm.ActualSizeCommand,
            Key.C when ctrl => vm.CopyCommand,
            Key.S when ctrl => vm.SaveAsCommand,
            _ => null,
        };

        if (command is null)
            return;

        Execute(command);
        e.Handled = true;
    }

    private static void Execute(ICommand? command)
    {
        if (command?.CanExecute(null) == true)
            command.Execute(null);
    }
}
