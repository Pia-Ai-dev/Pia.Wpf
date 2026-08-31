using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
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
            Assert.True(WaitForOpacity(rig.Bar, 1.0),
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
            Assert.False(WaitForOpacity(rig.Bar, 1.0, ShortWait), "a sub-pixel scroll lit the bar up");
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
        }
        finally
        {
            Dispose(rig);
        }
    }

    [Fact]
    public void HoveringTheBar_LightsItUp()
    {
        var rig = Host();

        try
        {
            Assert.True(WaitForOpacity(rig.Bar, Idle), "the bar did not settle before the hover");

            // Synthetic, because nothing can move a real pointer onto an unshown window. It proves the class
            // handler is registered and reached — the one thing the Loaded hook silently failed to do.
            WpfStaHost.Run(() =>
            {
                rig.Bar.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0)
                {
                    RoutedEvent = UIElement.MouseEnterEvent,
                });
                return 0;
            });

            Assert.True(WaitForOpacity(rig.Bar, 1.0),
                $"a hover should light the bar, but it is at {Opacity(rig.Bar)}");
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
