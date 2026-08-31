using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Pia.Behaviors;

/// <summary>
/// Holds every scroll bar at a thin translucent hint until it is used, lights it up while the user scrolls,
/// hovers or drags it, and fades it back after a short hold.
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
            new FrameworkPropertyMetadata(0.2, FrameworkPropertyMetadataOptions.Inherits));

    public static double GetIdleOpacity(DependencyObject obj) => (double)obj.GetValue(IdleOpacityProperty);

    public static void SetIdleOpacity(DependencyObject obj, double value) => obj.SetValue(IdleOpacityProperty, value);

    /// <summary>What the bar comes up to in use. Just short of 1, so even the active bar stays a shade soft.</summary>
    public static readonly DependencyProperty ActiveOpacityProperty =
        DependencyProperty.RegisterAttached("ActiveOpacity", typeof(double), typeof(ScrollBarAutoFadeBehavior),
            new FrameworkPropertyMetadata(0.8, FrameworkPropertyMetadataOptions.Inherits));

    public static double GetActiveOpacity(DependencyObject obj) => (double)obj.GetValue(ActiveOpacityProperty);

    public static void SetActiveOpacity(DependencyObject obj, double value) => obj.SetValue(ActiveOpacityProperty, value);

    /// <summary>Fraction of the thumb's drawn thickness kept while idle. 1 leaves the template's width alone.</summary>
    public static readonly DependencyProperty IdleThicknessProperty =
        DependencyProperty.RegisterAttached("IdleThickness", typeof(double), typeof(ScrollBarAutoFadeBehavior),
            new FrameworkPropertyMetadata(0.5, FrameworkPropertyMetadataOptions.Inherits));

    public static double GetIdleThickness(DependencyObject obj) => (double)obj.GetValue(IdleThicknessProperty);

    public static void SetIdleThickness(DependencyObject obj, double value) => obj.SetValue(IdleThicknessProperty, value);

    private static readonly DependencyProperty VerticalBarProperty =
        DependencyProperty.RegisterAttached("VerticalBar", typeof(ScrollBar), typeof(ScrollBarAutoFadeBehavior),
            new PropertyMetadata(null));

    private static readonly DependencyProperty HorizontalBarProperty =
        DependencyProperty.RegisterAttached("HorizontalBar", typeof(ScrollBar), typeof(ScrollBarAutoFadeBehavior),
            new PropertyMetadata(null));

    private static readonly DependencyProperty ThumbProperty =
        DependencyProperty.RegisterAttached("Thumb", typeof(Thumb), typeof(ScrollBarAutoFadeBehavior),
            new PropertyMetadata(null));

    private static readonly DependencyProperty IsManagedProperty =
        DependencyProperty.RegisterAttached("IsManaged", typeof(bool), typeof(ScrollBarAutoFadeBehavior),
            new PropertyMetadata(false));

    private static readonly DependencyProperty IsLitProperty =
        DependencyProperty.RegisterAttached("IsLit", typeof(bool), typeof(ScrollBarAutoFadeBehavior),
            new PropertyMetadata(false));

    // Tracked rather than read back off IsMouseOver, so the state is the one the events actually reported.
    private static readonly DependencyProperty IsHoveredProperty =
        DependencyProperty.RegisterAttached("IsHovered", typeof(bool), typeof(ScrollBarAutoFadeBehavior),
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

        // The thumb, not the bar: the bar spans the whole viewport, so hovering the empty stretch of track
        // below a short thumb would light it up without anyone reaching for it.
        EventManager.RegisterClassHandler(typeof(Thumb), UIElement.MouseEnterEvent,
            new MouseEventHandler(OnThumbEntered));

        // Approaching the bar restores the thumb's full width but deliberately not its opacity: an idle thumb
        // is only a few pixels wide, so it has to widen before the pointer is exactly on it.
        EventManager.RegisterClassHandler(typeof(ScrollBar), UIElement.MouseEnterEvent,
            new MouseEventHandler(OnBarHoverChanged));
        EventManager.RegisterClassHandler(typeof(ScrollBar), UIElement.MouseLeaveEvent,
            new MouseEventHandler(OnBarHoverChanged));
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

    private static void OnThumbEntered(object sender, MouseEventArgs e)
    {
        // Sliders own thumbs too, so this walks up rather than assuming a scroll bar.
        if (sender is not Thumb thumb || OwningBar(thumb) is not { } bar) return;
        if (!(bool)bar.GetValue(IsManagedProperty)) return;

        bar.SetValue(ThumbProperty, thumb);
        Poke(bar);
    }

    private static void OnBarHoverChanged(object sender, MouseEventArgs e)
    {
        if (sender is not ScrollBar bar || !(bool)bar.GetValue(IsManagedProperty)) return;

        var entering = e.RoutedEvent == UIElement.MouseEnterEvent;
        bar.SetValue(IsHoveredProperty, entering);
        ApplyThickness(bar, entering ? ShowDuration : FadeDuration);
    }

    private static ScrollBar? OwningBar(Thumb thumb)
    {
        for (DependencyObject? node = thumb; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is ScrollBar bar) return bar;

        return null;
    }

    private static Thumb? ThumbOf(ScrollBar bar)
    {
        if (bar.GetValue(ThumbProperty) is Thumb known) return known;

        bar.ApplyTemplate();
        if ((bar.Template?.FindName("PART_Track", bar) as Track)?.Thumb is not { } thumb) return null;

        bar.SetValue(ThumbProperty, thumb);
        return thumb;
    }

    private static ScrollBar? BarOf(ScrollViewer viewer, Orientation orientation)
    {
        var slot = orientation == Orientation.Vertical ? VerticalBarProperty : HorizontalBarProperty;
        if (viewer.GetValue(slot) is ScrollBar known)
        {
            // A Collapsed bar is never measured, so the template holding its thumb may not exist until a later
            // pass makes the bar visible — keep reaching for it until it does.
            if (known.GetValue(ThumbProperty) is null) ApplyThickness(known, TimeSpan.Zero);
            return known;
        }

        viewer.ApplyTemplate();
        var part = orientation == Orientation.Vertical ? "PART_VerticalScrollBar" : "PART_HorizontalScrollBar";
        if (viewer.Template?.FindName(part, viewer) is not ScrollBar bar) return null;

        viewer.SetValue(slot, bar);
        bar.SetValue(IsManagedProperty, true);
        Apply(bar, TimeSpan.Zero);
        return bar;
    }

    private static void Poke(ScrollBar bar)
    {
        if (!GetIsEnabled(bar)) return;

        bar.SetValue(LastActivityProperty, Environment.TickCount64);
        if ((bool)bar.GetValue(IsLitProperty)) return;

        bar.SetValue(IsLitProperty, true);
        Lit.Add(new WeakReference<ScrollBar>(bar));
        Apply(bar, ShowDuration);

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

            // The thumb's own hover, not the bar's, for the same reason the reveal is on the thumb. A thumb
            // held still raises no scroll, so the capture is what keeps a stalled drag lit.
            if (bar.GetValue(ThumbProperty) is Thumb { IsMouseOver: true } || bar.IsMouseCaptureWithin)
            {
                bar.SetValue(LastActivityProperty, now);
                continue;
            }

            if (now - (long)bar.GetValue(LastActivityProperty) < HoldMs) continue;

            bar.SetValue(IsLitProperty, false);
            Lit.RemoveAt(i);
            Apply(bar, FadeDuration);
        }

        if (Lit.Count == 0) _sweep?.Stop();
    }

    private static void Apply(ScrollBar bar, Duration duration)
    {
        if (!GetIsEnabled(bar)) return;

        var lit = (bool)bar.GetValue(IsLitProperty);
        bar.BeginAnimation(UIElement.OpacityProperty,
            Tween(lit ? GetActiveOpacity(bar) : GetIdleOpacity(bar), duration));
        ApplyThickness(bar, duration);
    }

    private static void ApplyThickness(ScrollBar bar, Duration duration)
    {
        if (!GetIsEnabled(bar) || ThumbOf(bar) is not { } thumb) return;

        var full = (bool)bar.GetValue(IsLitProperty) || (bool)bar.GetValue(IsHoveredProperty);

        // Scaled rather than resized: the Track positions the thumb from its measured size, and relaying that
        // out on every frame of the fade buys nothing a render transform does not.
        var axis = bar.Orientation == Orientation.Vertical
            ? ScaleTransform.ScaleXProperty
            : ScaleTransform.ScaleYProperty;

        ScaleOf(thumb).BeginAnimation(axis, Tween(full ? 1.0 : GetIdleThickness(bar), duration));
    }

    private static ScaleTransform ScaleOf(Thumb thumb)
    {
        if (thumb.ReadLocalValue(UIElement.RenderTransformProperty) is ScaleTransform existing) return existing;

        var scale = new ScaleTransform();
        thumb.RenderTransformOrigin = new Point(0.5, 0.5);
        thumb.RenderTransform = scale;
        return scale;
    }

    private static DoubleAnimation Tween(double to, Duration duration) =>
        new(to, duration) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
}
