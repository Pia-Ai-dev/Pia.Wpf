using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Pia.Behaviors;

/// <summary>
/// Holds every scroll bar at a translucent hint until it is used, lights it up while the user scrolls, hovers
/// or drags it, and fades it back after a short hold.
/// </summary>
public static class ScrollBarAutoFadeBehavior
{
    // Extent and viewport changes arrive as a scroll of 0, but a remeasure can still nudge the offset by a
    // fraction — without this every page would flash its bar on the way in.
    private const double MinimalChange = 1.0;
    private const int HoldMs = 1100;
    private static readonly Duration ShowDuration = TimeSpan.FromMilliseconds(90);
    private static readonly Duration FadeDuration = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMilliseconds(150);

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(ScrollBarAutoFadeBehavior),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    /// <summary>What the bar fades back to. 0 hides it outright.</summary>
    public static readonly DependencyProperty IdleOpacityProperty =
        DependencyProperty.RegisterAttached("IdleOpacity", typeof(double), typeof(ScrollBarAutoFadeBehavior),
            new FrameworkPropertyMetadata(0.28, FrameworkPropertyMetadataOptions.Inherits));

    public static double GetIdleOpacity(DependencyObject obj) => (double)obj.GetValue(IdleOpacityProperty);

    public static void SetIdleOpacity(DependencyObject obj, double value) => obj.SetValue(IdleOpacityProperty, value);

    private static readonly DependencyProperty VerticalBarProperty =
        DependencyProperty.RegisterAttached("VerticalBar", typeof(ScrollBar), typeof(ScrollBarAutoFadeBehavior),
            new PropertyMetadata(null));

    private static readonly DependencyProperty HorizontalBarProperty =
        DependencyProperty.RegisterAttached("HorizontalBar", typeof(ScrollBar), typeof(ScrollBarAutoFadeBehavior),
            new PropertyMetadata(null));

    private static readonly DependencyProperty IsManagedProperty =
        DependencyProperty.RegisterAttached("IsManaged", typeof(bool), typeof(ScrollBarAutoFadeBehavior),
            new PropertyMetadata(false));

    private static readonly DependencyProperty IsLitProperty =
        DependencyProperty.RegisterAttached("IsLit", typeof(bool), typeof(ScrollBarAutoFadeBehavior),
            new PropertyMetadata(false));

    private static readonly DependencyProperty LastActivityProperty =
        DependencyProperty.RegisterAttached("LastActivity", typeof(long), typeof(ScrollBarAutoFadeBehavior),
            new PropertyMetadata(0L));

    // Weak, and swept only while something is lit: scroll bars churn with every page and list container, and a
    // per-bar timer in a closure would keep each one alive.
    private static readonly List<WeakReference<ScrollBar>> Lit = [];
    private static DispatcherTimer? _sweep;
    private static bool _installed;

    /// <summary>Applies the fade to every scroll bar in the app. A class handler cannot be unregistered, so this is idempotent.</summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;

        // ScrollChanged, not Loaded: WPF only broadcasts Loaded into subtrees that hold an *instance* handler,
        // so a class handler for it is never called. This one also fires once during the first arrange, which
        // is where the initial fade comes from.
        EventManager.RegisterClassHandler(typeof(ScrollViewer), ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(OnScrollChanged));
        EventManager.RegisterClassHandler(typeof(ScrollBar), UIElement.MouseEnterEvent,
            new MouseEventHandler(OnBarEntered));
    }

    private static void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // OriginalSource, not sender: a nested viewer's event bubbles through the outer one too.
        if (e.OriginalSource is not ScrollViewer viewer) return;

        if (BarOf(viewer, Orientation.Vertical) is { } vertical && Math.Abs(e.VerticalChange) >= MinimalChange)
            Poke(vertical);

        if (BarOf(viewer, Orientation.Horizontal) is { } horizontal && Math.Abs(e.HorizontalChange) >= MinimalChange)
            Poke(horizontal);
    }

    private static void OnBarEntered(object sender, MouseEventArgs e)
    {
        if (sender is ScrollBar bar && (bool)bar.GetValue(IsManagedProperty)) Poke(bar);
    }

    private static ScrollBar? BarOf(ScrollViewer viewer, Orientation orientation)
    {
        var slot = orientation == Orientation.Vertical ? VerticalBarProperty : HorizontalBarProperty;
        if (viewer.GetValue(slot) is ScrollBar known) return known;

        viewer.ApplyTemplate();
        var part = orientation == Orientation.Vertical ? "PART_VerticalScrollBar" : "PART_HorizontalScrollBar";
        if (viewer.Template?.FindName(part, viewer) is not ScrollBar bar) return null;

        viewer.SetValue(slot, bar);
        bar.SetValue(IsManagedProperty, true);
        if (GetIsEnabled(bar)) Animate(bar, GetIdleOpacity(bar), TimeSpan.Zero);
        return bar;
    }

    private static void Poke(ScrollBar bar)
    {
        if (!GetIsEnabled(bar)) return;

        bar.SetValue(LastActivityProperty, Environment.TickCount64);
        if ((bool)bar.GetValue(IsLitProperty)) return;

        bar.SetValue(IsLitProperty, true);
        Lit.Add(new WeakReference<ScrollBar>(bar));
        Animate(bar, 1.0, ShowDuration);

        _sweep ??= new DispatcherTimer(SweepInterval, DispatcherPriority.Background, OnSweep, bar.Dispatcher);
        _sweep.Start();
    }

    private static void OnSweep(object? sender, EventArgs e)
    {
        var now = Environment.TickCount64;

        for (var i = Lit.Count - 1; i >= 0; i--)
        {
            if (!Lit[i].TryGetTarget(out var bar))
            {
                Lit.RemoveAt(i);
                continue;
            }

            // A thumb held still raises no scroll, so the capture is what keeps a stalled drag lit.
            if (bar.IsMouseOver || bar.IsMouseCaptureWithin)
            {
                bar.SetValue(LastActivityProperty, now);
                continue;
            }

            if (now - (long)bar.GetValue(LastActivityProperty) < HoldMs) continue;

            bar.SetValue(IsLitProperty, false);
            Lit.RemoveAt(i);
            if (GetIsEnabled(bar)) Animate(bar, GetIdleOpacity(bar), FadeDuration);
        }

        if (Lit.Count == 0) _sweep?.Stop();
    }

    private static void Animate(ScrollBar bar, double to, Duration duration) =>
        bar.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(to, duration) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
}
