using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Mvvm.DependencyInjection;
using Pia.Behaviors;
using Pia.Helpers;
using Pia.ViewModels;

namespace Pia.Views;

public partial class OptimizeView : UserControl
{
    private OptimizeViewModel? ViewModel => DataContext as OptimizeViewModel;

    // Both are resolved again in OnUnloaded otherwise, and by then DataContext is cleared and the view has left
    // the tree — so neither hook came off and an app-lifetime Window kept this view alive.
    private OptimizeViewModel? _subscribedViewModel;
    private Window? _subscribedWindow;

    public OptimizeView()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Loaded repeats without an Unloaded on re-parenting, so drop the previous hooks before taking new ones.
        Detach();

        _subscribedViewModel = ViewModel;
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged += OnPropertyChanged;
            _subscribedViewModel.FocusInputRequested += OnFocusInputRequested;

            if (_subscribedViewModel.ShouldFocusInput)
            {
                _subscribedViewModel.RequestFocus();
            }
        }

        _subscribedWindow = Window.GetWindow(this);
        if (_subscribedWindow is not null)
        {
            _subscribedWindow.Activated += OnParentWindowActivated;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Detach();

    private void Detach()
    {
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged -= OnPropertyChanged;
            _subscribedViewModel.FocusInputRequested -= OnFocusInputRequested;
            _subscribedViewModel = null;
        }

        if (_subscribedWindow is not null)
        {
            _subscribedWindow.Activated -= OnParentWindowActivated;
            _subscribedWindow = null;
        }
    }

    private void OnParentWindowActivated(object? sender, EventArgs e)
    {
        if (ViewModel is not null && string.IsNullOrEmpty(ViewModel.InputText) && !ViewModel.IsComparisonView)
        {
            ViewModel.RequestFocus();
        }
    }

    private void OnFocusInputRequested(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            InputTextBox.Focus();
            InputTextBox.CaretIndex = InputTextBox.Text.Length;
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (ViewModel is null)
            return;

        if (e.PropertyName == nameof(ViewModel.IsComparisonView))
        {
            InputViewGrid.Visibility = ViewModel.IsComparisonView ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
            ComparisonViewGrid.Visibility = ViewModel.IsComparisonView ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        }

        if (e.PropertyName == nameof(ViewModel.ShouldFocusInput) && ViewModel.ShouldFocusInput)
        {
            ViewModel.RequestFocus();
        }
    }

    private void OnWindowDragDelta(object? sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (Window.GetWindow(this) is Window window)
        {
            window.DragMove();
        }
    }

    private void InputTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (ViewModel?.OptimizeCommand.CanExecute(null) == true)
            {
                ViewModel.OptimizeCommand.Execute(null);
                e.Handled = true;
            }
        }
    }

    private void OptimizeView_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control && ViewModel?.IsComparisonView == true)
        {
            if (ViewModel.AcceptCommand.CanExecute(null))
            {
                ViewModel.AcceptCommand.Execute(null);
                e.Handled = true;
            }
        }
    }

    private void SendToButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.ContextMenu is not null)
        {
            element.ContextMenu.PlacementTarget = element;
            element.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
            element.ContextMenu.IsOpen = true;
        }
    }

    private void AttachFileButton_Click(object sender, RoutedEventArgs e)
    {
        var accepted = FileDropBehavior.GetAcceptedExtensions(RootGrid);
        var files = FilePicker.PickFiles(accepted);
        if (files.Count == 0) return;

        if (ViewModel?.HandleFilesDroppedCommand.CanExecute(files) == true)
            ViewModel.HandleFilesDroppedCommand.Execute(files);
    }
}
