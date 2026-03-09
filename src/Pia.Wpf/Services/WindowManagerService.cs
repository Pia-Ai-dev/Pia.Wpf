using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Pia.Models;
using Pia.Navigation;
using Pia.Services.Interfaces;
using Pia.ViewModels;

namespace Pia.Services;

public class WindowManagerService : IWindowManagerService
{
    private readonly IServiceProvider _rootProvider;
    private readonly Dictionary<WindowMode, ManagedWindow> _windows = new();
    private bool _isShuttingDown;
    private double _lastWindowLeft = double.NaN;
    private double _lastWindowTop = double.NaN;
    private const double PositionOffset = 30;

    public bool HasOpenWindows => _windows.Values.Any(w => w.Window.Visibility == Visibility.Visible);

    public event EventHandler<ManagedWindow>? WindowOpened;
    public event EventHandler<ManagedWindow>? WindowClosed;
    public event EventHandler? WindowVisibilityChanged;

    public WindowManagerService(IServiceProvider rootProvider)
    {
        _rootProvider = rootProvider;
    }

    public void ShowWindow(WindowMode mode)
    {
        if (_windows.TryGetValue(mode, out var existing))
        {
            existing.Window.Show();
            existing.Window.Visibility = Visibility.Visible;
            existing.Window.WindowState = WindowState.Normal;
            existing.Window.Topmost = true;
            existing.Window.Activate();
            existing.Window.Focus();
            existing.Window.Topmost = false;
            WindowVisibilityChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var scope = _rootProvider.CreateScope();
        var window = scope.ServiceProvider.GetRequiredService<MainWindow>();
        var viewModel = scope.ServiceProvider.GetRequiredService<MainWindowViewModel>();
        viewModel.Mode = mode;
        window.DataContext = viewModel;

        var managed = new ManagedWindow(mode, window, scope);
        _windows[mode] = managed;

        window.Closing += (_, e) =>
        {
            if (_isShuttingDown)
                return;

            e.Cancel = true;
            HideWindow(mode);
        };

        window.StateChanged += (_, _) =>
        {
            if (_isShuttingDown)
                return;

            if (window.WindowState == WindowState.Minimized)
            {
                HideWindow(mode);
            }
        };

        if (_windows.Values.Any(w => w != managed && w.Window.Visibility == Visibility.Visible))
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            var workArea = SystemParameters.WorkArea;
            var newLeft = _lastWindowLeft + PositionOffset;
            var newTop = _lastWindowTop + PositionOffset;

            if (double.IsNaN(newLeft) || double.IsNaN(newTop)
                || newLeft + window.Width > workArea.Right
                || newTop + window.Height > workArea.Bottom)
            {
                newLeft = workArea.Left + PositionOffset;
                newTop = workArea.Top + PositionOffset;
            }

            window.Left = newLeft;
            window.Top = newTop;
        }

        window.Show();
        window.Topmost = true;
        window.Activate();
        window.Focus();
        window.Topmost = false;

        _lastWindowLeft = window.Left;
        _lastWindowTop = window.Top;

        WindowOpened?.Invoke(this, managed);
        WindowVisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ShowWindowWithText(WindowMode mode, string text)
    {
        ShowWindow(mode);

        if (!_windows.TryGetValue(mode, out var managed))
            return;

        var navigationService = managed.Scope.ServiceProvider.GetRequiredService<INavigationService>();

        switch (mode)
        {
            case WindowMode.Assistant:
                navigationService.NavigateTo<AssistantViewModel, string>(text);
                break;
            case WindowMode.Research:
                navigationService.NavigateTo<ResearchViewModel, string>(text);
                break;
        }
    }

    public void HideWindow(WindowMode mode)
    {
        if (!_windows.TryGetValue(mode, out var managed))
            return;

        managed.Window.WindowState = WindowState.Normal;
        managed.Window.Visibility = Visibility.Hidden;
        WindowVisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    public void HideAllWindows()
    {
        foreach (var mode in _windows.Keys.ToList())
        {
            HideWindow(mode);
        }
    }

    public void CloseAndDisposeAll()
    {
        _isShuttingDown = true;

        foreach (var (_, managed) in _windows)
        {
            managed.Window.PrepareForExit();
            managed.Window.Close();
            WindowClosed?.Invoke(this, managed);
            managed.Dispose();
        }

        _windows.Clear();
    }

    public bool IsVisible(WindowMode mode)
    {
        return _windows.TryGetValue(mode, out var managed)
            && managed.Window.Visibility == Visibility.Visible;
    }
}
