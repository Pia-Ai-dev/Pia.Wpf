using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Pia.Behaviors;
using Pia.Tests.Views;
using Xunit;

namespace Pia.Tests.Behaviors;

/// <summary>
/// The fade hangs off a class handler on <see cref="ScrollViewer.ScrollChangedEvent"/>, which needs a real
/// arrange pass under a PresentationSource — hence the hidden HwndSource rather than a bare Measure/Arrange.
/// A class handler on <c>Loaded</c> looks like the obvious hook and is not one: WPF only broadcasts Loaded
/// into subtrees that hold an instance handler, so it never ran.
/// </summary>
[Collection("WpfApplicationStatic")]
public class ScrollBarAutoFadeBehaviorTests
{
    private const double Idle = 0.28;
    private const double IdleThickness = 0.5;
    private const double Active = 0.9;
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan ShortWait = TimeSpan.FromMilliseconds(700);

    [Fact]
    public void ABar_ArrangesFaded_LightsUpOnAScroll_AndFadesBackAfterTheHold()
    {
        var rig = Host();

        try
        {
            Assert.True(WaitForOpacity(rig.Bar, Idle),
                $"a bar nothing has touched should sit at {Idle}, not {Opacity(rig.Bar)}");

            WpfStaHost.Run(() => { rig.Viewer.ScrollToVerticalOffset(300); return 0; });
            Assert.True(WaitForOpacity(rig.Bar, Active),
                $"scrolling should light the bar, but it is at {Opacity(rig.Bar)}");

            Assert.True(WaitForOpacity(rig.Bar, Idle),
                $"the bar should fade back once the hold expires, but it is at {Opacity(rig.Bar)}");
        }
        finally
        {
            Dispose(rig);
        }
    }

    [Fact]
    public void AJitterSizedScroll_LeavesTheBarFaded()
    {
        var rig = Host();

        try
        {
            Assert.True(WaitForOpacity(rig.Bar, Idle), "the bar did not settle before the nudge");

            WpfStaHost.Run(() => { rig.Viewer.ScrollToVerticalOffset(0.5); return 0; });

            // A remeasure moves the offset by a fraction; if that counted as scrolling, every page would flash.
            Assert.False(WaitForOpacity(rig.Bar, Active, ShortWait), "a sub-pixel scroll lit the bar up");
        }
        finally
        {
            Dispose(rig);
        }
    }

    [Fact]
    public void TheFadeCanBeTurnedOff_ForASubtree()
    {
        var rig = Host(parent => ScrollBarAutoFadeBehavior.SetIsEnabled(parent, false));

        try
        {
            Assert.False(WaitForOpacity(rig.Bar, Idle, ShortWait),
                "an opted-out subtree should keep its bars fully opaque");

            // Scrolling too, or the assertion above would also pass with the whole behavior wired to nothing.
            WpfStaHost.Run(() => { rig.Viewer.ScrollToVerticalOffset(300); return 0; });
            Assert.False(WaitForOpacity(rig.Bar, Idle, ShortWait),
                "an opted-out bar should not fade after a scroll either");
            Assert.Equal(1.0, Opacity(rig.Bar), 3);
            Assert.False(HasScale(rig), "an opted-out bar should not get a thickness scale installed at all");
        }
        finally
        {
            Dispose(rig);
        }
    }

    [Fact]
    public void HoveringTheThumb_LightsTheBarUp()
    {
        var rig = Host();

        try
        {
            Assert.True(WaitForOpacity(rig.Bar, Idle), "the bar did not settle before the hover");

            Enter(ThumbOf(rig));

            Assert.True(WaitForOpacity(rig.Bar, Active),
                $"a hover on the thumb should light the bar, but it is at {Opacity(rig.Bar)}");
        }
        finally
        {
            Dispose(rig);
        }
    }

    [Fact]
    public void HoveringTheEmptyTrack_WidensTheThumb_WithoutBrightening()
    {
        var rig = Host();

        try
        {
            Assert.True(WaitForOpacity(rig.Bar, Idle), "the bar did not settle before the hover");
            Assert.True(WaitForThickness(rig, IdleThickness), "the thumb did not narrow before the hover");

            // The bar spans the whole viewport, so most of what the pointer can reach is track, not thumb.
            Enter(rig.Bar);

            // Wide enough to grab, still quiet: the width and the brightness are driven apart on purpose.
            Assert.True(WaitForThickness(rig, 1.0), "approaching the bar left the thumb too thin to grab");
            Assert.False(WaitForOpacity(rig.Bar, Active, ShortWait),
                "hovering the empty stretch of track lit the bar up");

            Leave(rig.Bar);
            Assert.True(WaitForThickness(rig, IdleThickness), "the thumb stayed wide after the pointer left");
        }
        finally
        {
            Dispose(rig);
        }
    }

    [Fact]
    public void AScroll_WidensTheThumb_EvenWithThePointerNowhereNearIt()
    {
        var rig = Host();

        try
        {
            Assert.True(WaitForThickness(rig, IdleThickness), "the thumb did not narrow on arrange");

            WpfStaHost.Run(() => { rig.Viewer.ScrollToVerticalOffset(300); return 0; });

            Assert.True(WaitForThickness(rig, 1.0), "a wheel scroll should widen the thumb, not just brighten it");
            Assert.True(WaitForThickness(rig, IdleThickness), "the thumb did not narrow again after the hold");
        }
        finally
        {
            Dispose(rig);
        }
    }

    private sealed record Rig(HwndSource Source, ScrollViewer Viewer, ScrollBar Bar);

    private static Rig Host(Action<Border>? configureParent = null)
    {
        ScrollBarAutoFadeBehavior.Install();

        HwndSource? source = null;
        ScrollViewer? viewer = null;
        ScrollBar? bar = null;

        WpfStaHost.Run(() =>
        {
            viewer = new ScrollViewer
            {
                Width = 200,
                Height = 200,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new Border { Height = 2000 },
            };

            var parent = new Border { Child = viewer };
            configureParent?.Invoke(parent);

            // Not shown (no WS_VISIBLE): all this needs is a source, so the arrange pass is a real one.
            source = new HwndSource(new HwndSourceParameters("PiaScrollBarAutoFadeTests")
            {
                Width = 300,
                Height = 300,
                WindowStyle = unchecked((int)0x80000000), // WS_POPUP
            })
            {
                RootVisual = parent,
            };

            parent.UpdateLayout();
            bar = (ScrollBar)viewer.Template.FindName("PART_VerticalScrollBar", viewer);
            return 0;
        });
        WpfStaHost.Pump();

        return new Rig(source!, viewer!, bar!);
    }

    private static void Dispose(Rig rig) => WpfStaHost.Run(() =>
    {
        rig.Source.Dispose();
        return 0;
    });

    private static Thumb ThumbOf(Rig rig) => WpfStaHost.Run(() =>
        ((Track)rig.Bar.Template.FindName("PART_Track", rig.Bar)).Thumb);

    /// <summary>Synthetic, because nothing can move a real pointer onto an unshown window. It proves the class
    /// handler is registered and reached — the one thing the Loaded hook silently failed to do.</summary>
    private static void Enter(UIElement element) => Raise(element, UIElement.MouseEnterEvent);

    private static void Leave(UIElement element) => Raise(element, UIElement.MouseLeaveEvent);

    private static void Raise(UIElement element, RoutedEvent routedEvent) => WpfStaHost.Run(() =>
    {
        element.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = routedEvent });
        return 0;
    });

    private static bool HasScale(Rig rig) => WpfStaHost.Run(() => ThumbOf(rig).RenderTransform is ScaleTransform);

    /// <summary>1 when no scale was installed, so a behavior wired to nothing reads as "full width".</summary>
    private static double Thickness(Rig rig) => WpfStaHost.Run(() =>
        ThumbOf(rig).RenderTransform is ScaleTransform scale ? scale.ScaleX : 1.0);

    private static bool WaitForThickness(Rig rig, double expected)
    {
        var deadline = DateTime.UtcNow + SettleTimeout;

        while (DateTime.UtcNow < deadline)
        {
            if (Math.Abs(Thickness(rig) - expected) < 0.01) return true;
            Thread.Sleep(25);
        }

        return false;
    }

    private static double Opacity(ScrollBar bar) => WpfStaHost.Run(() => bar.Opacity);

    private static bool WaitForOpacity(ScrollBar bar, double expected, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? SettleTimeout);

        while (DateTime.UtcNow < deadline)
        {
            if (Math.Abs(Opacity(bar) - expected) < 0.01) return true;
            Thread.Sleep(25);
        }

        return false;
    }
}
