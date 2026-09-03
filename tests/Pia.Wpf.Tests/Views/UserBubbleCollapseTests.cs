using System.Windows;
using Pia.Controls.Chat;
using Xunit;

namespace Pia.Tests.Views;

/// <summary>
/// A sent message folds to a few lines and offers to unfold. The offer is driven by the text's measured
/// height, so only a laid-out control can say whether it appears when it should.
/// </summary>
[Collection("WpfApplicationStatic")]
public class UserBubbleCollapseTests
{
    private const string ShortMessage = "one line";

    private static readonly string LongMessage =
        string.Join(Environment.NewLine, Enumerable.Range(1, 30).Select(i => $"line {i}"));

    [Fact]
    public void TheToggleAppearsOnlyOnceTheMessageOutgrowsTheFold()
    {
        PiaCollapsibleMessageText? shortMessage = null;
        PiaCollapsibleMessageText? longMessage = null;

        WpfStaHost.Run(() =>
        {
            shortMessage = Build(ShortMessage);
            longMessage = Build(LongMessage);
            return 0;
        });
        WpfStaHost.Pump();

        var (shortOverflows, longOverflows, shortToggle, longToggle) = WpfStaHost.Run(() =>
            (shortMessage!.IsOverflowing, longMessage!.IsOverflowing,
             Toggle(shortMessage!).Visibility, Toggle(longMessage!).Visibility));

        Assert.False(shortOverflows, "a one-line message offered a toggle that cannot do anything");
        Assert.True(longOverflows, "a thirty-line message was not detected as folded");
        Assert.Equal(Visibility.Collapsed, shortToggle);
        Assert.Equal(Visibility.Visible, longToggle);
    }

    [Fact]
    public void UnfoldingLiftsTheCapAndFoldingPutsItBack()
    {
        PiaCollapsibleMessageText? control = null;

        WpfStaHost.Run(() =>
        {
            control = Build(LongMessage);
            return 0;
        });
        WpfStaHost.Pump();

        var (folded, unfolded, refolded, lineHeight) = WpfStaHost.Run(() =>
        {
            var before = Clip(control!).MaxHeight;
            Click(control!);
            var open = Clip(control!).MaxHeight;
            Click(control!);
            return (before, open, Clip(control!).MaxHeight, Body(control!).LineHeight);
        });

        Assert.True(double.IsPositiveInfinity(unfolded), $"unfolding left MaxHeight at {unfolded}");
        // Five lines is the requirement; the pixel height is whatever the bubble's style makes it.
        Assert.Equal(5 * lineHeight, folded);
        Assert.Equal(folded, refolded);
    }

    /// <summary>A shorter message arriving in a recycled row must not inherit the unfolded state.</summary>
    [Fact]
    public void ANewMessageArrivesFolded()
    {
        PiaCollapsibleMessageText? control = null;

        WpfStaHost.Run(() =>
        {
            control = Build(LongMessage);
            return 0;
        });
        WpfStaHost.Pump();

        var (refolded, stillOffering) = WpfStaHost.Run(() =>
        {
            Click(control!);
            control!.Text = ShortMessage;
            Measure(control!);
            return (Clip(control!).MaxHeight, control!.IsOverflowing);
        });

        Assert.False(double.IsPositiveInfinity(refolded), "the fold stayed lifted for the next message");
        Assert.False(stillOffering, "the toggle stayed on screen for a message that now fits");
    }

    private static PiaCollapsibleMessageText Build(string text)
    {
        var control = new PiaCollapsibleMessageText { Text = text };
        Measure(control);
        return control;
    }

    private static FrameworkElement Clip(PiaCollapsibleMessageText control) =>
        (FrameworkElement)control.FindName("TextClip");

    private static System.Windows.Controls.TextBlock Body(PiaCollapsibleMessageText control) =>
        (System.Windows.Controls.TextBlock)control.FindName("Body");

    private static Wpf.Ui.Controls.Button Toggle(PiaCollapsibleMessageText control) =>
        (Wpf.Ui.Controls.Button)control.FindName("Toggle");

    private static void Click(PiaCollapsibleMessageText control)
    {
        Toggle(control).RaiseEvent(
            new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Measure(control);
    }

    /// <summary>The control is never in a window, so nothing measures it unless the test does.</summary>
    private static void Measure(FrameworkElement control)
    {
        control.Measure(new Size(520, 4000));
        control.Arrange(new Rect(0, 0, 520, 4000));
        control.UpdateLayout();
    }
}
