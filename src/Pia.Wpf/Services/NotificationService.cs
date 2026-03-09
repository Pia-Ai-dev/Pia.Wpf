using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Pia.Services.Interfaces;

namespace Pia.Services;

public class NotificationService : INotificationService
{
    private readonly DispatcherTimer _hideTimer;
    private Border? _currentNotification;

    public NotificationService()
    {
        _hideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _hideTimer.Tick += OnHideTimerTick;
    }

    public void ShowToast(string message, int durationMs = 3000)
    {
        ShowNotification(message, Brushes.Gray, durationMs);
    }

    public void ShowError(string message, int durationMs = 5000)
    {
        ShowNotification(message, Brushes.Red, durationMs);
    }

    public void ShowSuccess(string message, int durationMs = 3000)
    {
        ShowNotification(message, Brushes.Green, durationMs);
    }

    private void ShowNotification(string message, Brush backgroundBrush, int durationMs)
    {
        if (Application.Current.MainWindow is null)
            return;

        var mainWindow = Application.Current.MainWindow;

        var notification = new Border
        {
            Background = backgroundBrush,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(16, 16, 16, 16),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = message,
                Foreground = Brushes.White,
                FontSize = 14
            }
        };

        var dropShadow = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = Colors.Black,
            Direction = 270,
            ShadowDepth = 2,
            Opacity = 0.3,
            BlurRadius = 8
        };
        notification.Effect = dropShadow;

        if (mainWindow.Content is Grid grid)
        {
            if (_currentNotification is not null)
            {
                grid.Children.Remove(_currentNotification);
            }

            grid.Children.Add(notification);
            _currentNotification = notification;

            Panel.SetZIndex(notification, 1000);

            _hideTimer.Interval = TimeSpan.FromMilliseconds(durationMs);
            _hideTimer.Start();
        }
    }

    private void OnHideTimerTick(object? sender, EventArgs e)
    {
        _hideTimer.Stop();

        if (_currentNotification is not null && Application.Current.MainWindow?.Content is Grid grid)
        {
            grid.Children.Remove(_currentNotification);
            _currentNotification = null;
        }
    }
}
