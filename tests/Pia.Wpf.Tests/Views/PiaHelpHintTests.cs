using System.Windows;
using System.Windows.Controls;
using Pia.Controls.Shared;
using Xunit;

namespace Pia.Tests.Views;

[Collection("WpfApplicationStatic")]
public class PiaHelpHintTests
{
    // The tooltip body lives in a popup outside the control's tree, so this asserts the text actually
    // arrives there rather than that the DP round-trips.
    [Fact]
    public void TheTooltipBody_CarriesTheHintText()
    {
        var actual = WpfStaHost.Run(() =>
        {
            var view = Realize(new PiaHelpHint { Text = "why this screen exists" });
            return ((TextBlock)ToolTipOf(view).Content).Text;
        });

        Assert.Equal("why this screen exists", actual);
    }

    [Fact]
    public void TheGlyph_IsCollapsedWhenNoTextIsGivenAndVisibleWhenOneIs()
    {
        var (withoutText, withText) = WpfStaHost.Run(() =>
        {
            var bare = Realize(new PiaHelpHint());
            var hinted = Realize(new PiaHelpHint { Text = "h" });
            return (Target(bare).Visibility.ToString(), Target(hinted).Visibility.ToString());
        });

        Assert.Equal(nameof(Visibility.Collapsed), withoutText);
        Assert.Equal(nameof(Visibility.Visible), withText);
    }

    // The 5s WPF default expires mid-sentence on a paragraph-length hint, and a screenshot cannot see it.
    [Fact]
    public void TheTooltip_OutlastsTheWpfDefaultShowDuration()
    {
        var (showDuration, initialDelay) = WpfStaHost.Run(() =>
        {
            var target = Target(Realize(new PiaHelpHint { Text = "h" }));
            return (ToolTipService.GetShowDuration(target), ToolTipService.GetInitialShowDelay(target));
        });

        Assert.True(showDuration >= 20_000, $"show duration was {showDuration}ms");
        Assert.True(initialDelay <= 400, $"initial delay was {initialDelay}ms");
    }

    /// <summary>Off-tree, so nothing applies the style or transfers a binding until layout is forced.</summary>
    private static PiaHelpHint Realize(PiaHelpHint view)
    {
        view.Measure(new Size(1000, 1000));
        view.Arrange(new Rect(0, 0, 1000, 1000));
        view.UpdateLayout();
        return view;
    }

    private static FrameworkElement Target(PiaHelpHint view) => (FrameworkElement)view.Content;

    private static ToolTip ToolTipOf(PiaHelpHint view) => (ToolTip)Target(view).ToolTip;
}
