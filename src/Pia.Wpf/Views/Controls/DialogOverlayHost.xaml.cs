using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Pia.Views.Controls;

public partial class DialogOverlayHost : UserControl
{
    private IInputElement? _previousFocus;

    public DialogOverlayHost()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public async Task<TResult> ShowAsync<TResult>(OverlayDialogPanel panel, CancellationToken ct = default)
    {
        _previousFocus = Keyboard.FocusedElement;

        DialogContentPresenter.Content = panel;
        Visibility = Visibility.Visible;

        await RunShowAnimationAsync();

        Keyboard.Focus(panel);

        var tcs = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnResultChosen(object result)
        {
            tcs.TrySetResult((TResult)result);
        }

        panel.ResultChosen += OnResultChosen;

        CancellationTokenRegistration ctReg = default;
        if (ct.CanBeCanceled)
        {
            ctReg = ct.Register(() =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    tcs.TrySetCanceled();
                });
            });
        }

        TResult result;
        try
        {
            result = await tcs.Task;
        }
        catch (OperationCanceledException)
        {
            result = default!;
        }
        finally
        {
            panel.ResultChosen -= OnResultChosen;
            await ctReg.DisposeAsync();
        }

        await RunHideAnimationAsync();

        DialogContentPresenter.Content = null;
        Visibility = Visibility.Collapsed;

        if (_previousFocus != null)
        {
            Keyboard.Focus(_previousFocus);
            _previousFocus = null;
        }

        return result;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DialogContentPresenter.Content is OverlayDialogPanel panel)
        {
            panel.OnEscapePressed();
            e.Handled = true;
        }
    }

    private Task RunShowAnimationAsync()
    {
        var tcs = new TaskCompletionSource();
        var storyboard = new Storyboard();

        // Backdrop: opacity 0 → 1 over 150ms
        var backdropFade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150));
        Storyboard.SetTarget(backdropFade, Backdrop);
        Storyboard.SetTargetProperty(backdropFade, new PropertyPath(OpacityProperty));
        storyboard.Children.Add(backdropFade);

        // Content: opacity 0 → 1 over 220ms
        var contentFade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(contentFade, DialogContentPresenter);
        Storyboard.SetTargetProperty(contentFade, new PropertyPath(OpacityProperty));
        storyboard.Children.Add(contentFade);

        // Content: ScaleX 0.95 → 1.0 over 220ms
        var scaleX = new DoubleAnimation(0.95, 1.0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(scaleX, DialogContentPresenter);
        Storyboard.SetTargetProperty(scaleX, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleX)"));
        storyboard.Children.Add(scaleX);

        // Content: ScaleY 0.95 → 1.0 over 220ms
        var scaleY = new DoubleAnimation(0.95, 1.0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(scaleY, DialogContentPresenter);
        Storyboard.SetTargetProperty(scaleY, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleY)"));
        storyboard.Children.Add(scaleY);

        storyboard.Completed += (_, _) => tcs.SetResult();
        storyboard.Begin();

        return tcs.Task;
    }

    private Task RunHideAnimationAsync()
    {
        var tcs = new TaskCompletionSource();
        var storyboard = new Storyboard();

        // Backdrop: opacity → 0 over 150ms
        var backdropFade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
        Storyboard.SetTarget(backdropFade, Backdrop);
        Storyboard.SetTargetProperty(backdropFade, new PropertyPath(OpacityProperty));
        storyboard.Children.Add(backdropFade);

        // Content: opacity → 0 over 150ms
        var contentFade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(contentFade, DialogContentPresenter);
        Storyboard.SetTargetProperty(contentFade, new PropertyPath(OpacityProperty));
        storyboard.Children.Add(contentFade);

        // Content: ScaleX → 0.95 over 150ms
        var scaleX = new DoubleAnimation(0.95, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(scaleX, DialogContentPresenter);
        Storyboard.SetTargetProperty(scaleX, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleX)"));
        storyboard.Children.Add(scaleX);

        // Content: ScaleY → 0.95 over 150ms
        var scaleY = new DoubleAnimation(0.95, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(scaleY, DialogContentPresenter);
        Storyboard.SetTargetProperty(scaleY, new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleY)"));
        storyboard.Children.Add(scaleY);

        storyboard.Completed += (_, _) => tcs.SetResult();
        storyboard.Begin();

        return tcs.Task;
    }
}
