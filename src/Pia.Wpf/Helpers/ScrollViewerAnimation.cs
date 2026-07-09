using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Pia.Helpers;

/// <summary>
/// Smoothly scrolls a <see cref="ScrollViewer"/> so a descendant element is brought into view, with a short
/// eased tween. <see cref="ScrollViewer.VerticalOffset"/> is read-only and not directly animatable, so an
/// attached proxy DP is animated and its change-callback drives <see cref="ScrollViewer.ScrollToVerticalOffset"/>.
/// </summary>
public static class ScrollViewerAnimation
{
    private static readonly DependencyProperty AnimatedOffsetProperty =
        DependencyProperty.RegisterAttached(
            "AnimatedOffset", typeof(double), typeof(ScrollViewerAnimation),
            new PropertyMetadata(0.0, OnAnimatedOffsetChanged));

    private static void OnAnimatedOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ScrollViewer sv)
        {
            sv.ScrollToVerticalOffset((double)e.NewValue);
        }
    }

    /// <summary>
    /// Animate <paramref name="scrollViewer"/> so <paramref name="element"/> is fully visible. No-ops when
    /// the element is already in view; falls back to an instant <see cref="FrameworkElement.BringIntoView()"/>
    /// on any geometry error (e.g. the element is not yet arranged).
    /// </summary>
    public static void SmoothScrollIntoView(this ScrollViewer scrollViewer, FrameworkElement element)
    {
        try
        {
            var transform = element.TransformToAncestor(scrollViewer);
            var top = transform.Transform(new Point(0, 0)).Y + scrollViewer.VerticalOffset;
            var bottom = top + element.ActualHeight;

            var viewTop = scrollViewer.VerticalOffset;
            var viewBottom = viewTop + scrollViewer.ViewportHeight;
            const double margin = 12;

            double to;
            if (top < viewTop + margin)
            {
                to = top - margin;                                  // element above the viewport → scroll up
            }
            else if (bottom > viewBottom - margin)
            {
                to = bottom - scrollViewer.ViewportHeight + margin; // element below the viewport → scroll down
            }
            else
            {
                return;                                             // already fully visible
            }

            to = Math.Max(0, Math.Min(to, scrollViewer.ScrollableHeight));
            Animate(scrollViewer, to);
        }
        catch (InvalidOperationException)
        {
            element.BringIntoView();
        }
    }

    private static void Animate(ScrollViewer sv, double to)
    {
        var from = sv.VerticalOffset;
        if (Math.Abs(from - to) < 0.5)
        {
            return;
        }

        // Cancel any in-flight animation and rebase the proxy DP to the CURRENT real offset, so a stale
        // clock cannot fight a fresh scroll or a manual one.
        sv.BeginAnimation(AnimatedOffsetProperty, null);
        sv.SetValue(AnimatedOffsetProperty, from);

        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(260),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
            FillBehavior = FillBehavior.HoldEnd,
        };
        sv.BeginAnimation(AnimatedOffsetProperty, animation);
    }
}
