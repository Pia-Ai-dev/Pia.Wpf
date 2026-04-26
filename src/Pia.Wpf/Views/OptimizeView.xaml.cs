using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Mvvm.DependencyInjection;
using Pia.ViewModels;

namespace Pia.Views;

public partial class OptimizeView : UserControl
{
    private OptimizeViewModel? ViewModel => DataContext as OptimizeViewModel;

    public OptimizeView()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.PropertyChanged += OnPropertyChanged;
            ViewModel.FocusInputRequested += OnFocusInputRequested;

            if (ViewModel.ShouldFocusInput)
            {
                ViewModel.RequestFocus();
            }
        }

        var parentWindow = Window.GetWindow(this);
        if (parentWindow is not null)
        {
            parentWindow.Activated += OnParentWindowActivated;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.PropertyChanged -= OnPropertyChanged;
            ViewModel.FocusInputRequested -= OnFocusInputRequested;
        }

        var parentWindow = Window.GetWindow(this);
        if (parentWindow is not null)
        {
            parentWindow.Activated -= OnParentWindowActivated;
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

    private void SendToButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.ContextMenu is not null)
        {
            element.ContextMenu.PlacementTarget = element;
            element.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
            element.ContextMenu.IsOpen = true;
        }
    }
}
