using System.Windows;
using System.Windows.Media;
using Pia.Controls.Shared;
using Wpf.Ui.Controls;
using Xunit;

namespace Pia.Tests.Views;

[Collection("WpfApplicationStatic")]
public class PiaEmptyStateTests
{
    // Foreground and Visibility are the two things a bounds-only UI check cannot see, and both hang off a
    // null-valued DP here, so the default is exactly what a weak assertion would observe.
    [Fact]
    public void TheIcon_FallsBackToTheAccentBrush_WhenNoIconBrushIsGiven()
    {
        var (actual, accent) = WpfStaHost.Run(() =>
        {
            var view = Realize(new PiaEmptyState { Symbol = SymbolRegular.CalendarClock24, Title = "t" });
            return (Describe(FindIcon(view).Foreground),
                    Describe((Brush)Application.Current.Resources["PiaAccentBrush"]));
        });

        Assert.Equal(accent, actual);
    }

    [Fact]
    public void TheIcon_UsesIconBrush_WhenTheCallerOverridesIt()
    {
        var (actual, expected) = WpfStaHost.Run(() =>
        {
            var subtle = (Brush)Application.Current.Resources["TextSubtleBrush"];
            var view = Realize(new PiaEmptyState { Title = "t", IconBrush = subtle });
            return (Describe(FindIcon(view).Foreground), Describe(subtle));
        });

        Assert.Equal(expected, actual);
        Assert.NotEqual(
            WpfStaHost.Run(() => Describe((Brush)Application.Current.Resources["PiaAccentBrush"])),
            actual);
    }

    [Fact]
    public void TheHintLine_IsCollapsedWhenNoHintIsGivenAndVisibleWhenOneIs()
    {
        var (withoutHint, withHint) = WpfStaHost.Run(() =>
        {
            var bare = Realize(new PiaEmptyState { Title = "t" });
            var hinted = Realize(new PiaEmptyState { Title = "t", Hint = "h" });
            return (HintLine(bare).Visibility.ToString(), HintLine(hinted).Visibility.ToString());
        });

        Assert.Equal(nameof(Visibility.Collapsed), withoutHint);
        Assert.Equal(nameof(Visibility.Visible), withHint);
    }

    /// <summary>Off-tree, so nothing applies the style or transfers a binding until layout is forced.</summary>
    private static PiaEmptyState Realize(PiaEmptyState view)
    {
        view.Measure(new Size(1000, 1000));
        view.Arrange(new Rect(0, 0, 1000, 1000));
        view.UpdateLayout();
        return view;
    }

    private static SymbolIcon FindIcon(PiaEmptyState view) =>
        Children(view).OfType<SymbolIcon>().Single();

    private static System.Windows.Controls.TextBlock HintLine(PiaEmptyState view) =>
        Children(view).OfType<System.Windows.Controls.TextBlock>().Last();

    private static IEnumerable<DependencyObject> Children(PiaEmptyState view) =>
        ((System.Windows.Controls.Panel)view.Content).Children.Cast<DependencyObject>();

    /// <summary>Only primitives may cross back off the STA host, so brushes are compared as text.</summary>
    private static string Describe(Brush? brush) =>
        brush is SolidColorBrush solid ? solid.Color.ToString() : brush?.ToString() ?? "<null>";
}
