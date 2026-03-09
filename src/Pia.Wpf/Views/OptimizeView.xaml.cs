using System.Windows;
using System.Windows.Controls;
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
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.PropertyChanged -= OnPropertyChanged;
            ViewModel.FocusInputRequested -= OnFocusInputRequested;
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
