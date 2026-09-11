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
            return (before, open, Clip(control!).MaxHeight, Sizer(control!).LineHeight);
        });

        // The whole text rather than infinity: the cap also hides the line the box reserves and never fills.
        Assert.Equal(30 * lineHeight, unfolded);
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

    /// <summary>The text box fills the width it is offered, so without the hidden sizer every bubble would
    /// be the widest one the transcript allows.</summary>
    [Fact]
    public void AShortMessageKeepsANarrowBubble()
    {
        PiaCollapsibleMessageText? control = null;

        WpfStaHost.Run(() =>
        {
            control = Build(ShortMessage);
            return 0;
        });
        WpfStaHost.Pump();

        var (width, natural) = WpfStaHost.Run(() =>
            (control!.DesiredSize.Width, Sizer(control!).DesiredSize.Width));

        Assert.True(width < 200, $"a two-word message took {width:0} of the 520 available");
        Assert.True(width >= natural, $"the text was given {width:0} for {natural:0} of content");
    }

    /// <summary>The box lays the text out inside its document's page padding, which it re-applies when
    /// overwritten. A box only as wide as the text it was measured against wraps the last word out of
    /// sight — behind the fold, where nothing says it is missing.</summary>
    [Theory]
    [InlineData("hi")]
    [InlineData(ShortMessage)]
    [InlineData("Zertifikate aus dem Input Store auflisten und zusammenfassen")]
    public void TheBoxIsWiderThanTheTextByAtLeastItsPagePadding(string text)
    {
        PiaCollapsibleMessageText? control = null;

        WpfStaHost.Run(() =>
        {
            control = Build(text);
            return 0;
        });
        WpfStaHost.Pump();

        var (boxWidth, textWidth, padding) = WpfStaHost.Run(() =>
        {
            var box = Body(control!);
            return (box.Width, Sizer(control!).DesiredSize.Width,
                    box.Document.PagePadding.Left + box.Document.PagePadding.Right);
        });

        Assert.True(boxWidth >= textWidth + padding,
            $"'{text}' was measured at {textWidth:0} and given a {boxWidth:0} box with {padding:0} of padding");
    }

    /// <summary>The whole point of the box: a passage can be picked out of a sent message, not just the
    /// message copied whole.</summary>
    [Fact]
    public void TheSentTextCanBeSelected()
    {
        PiaCollapsibleMessageText? control = null;

        WpfStaHost.Run(() =>
        {
            control = Build(ShortMessage);
            return 0;
        });
        WpfStaHost.Pump();

        var (readOnly, selected) = WpfStaHost.Run(() =>
        {
            var box = Body(control!);
            box.SelectAll();
            return (box.IsReadOnly, box.Selection.Text.Trim());
        });

        Assert.True(readOnly, "the sent message became editable");
        Assert.Equal(ShortMessage, selected);
    }

    private static PiaCollapsibleMessageText Build(string text)
    {
        var control = new PiaCollapsibleMessageText { Text = text };
        Measure(control);
        return control;
    }

    private static FrameworkElement Clip(PiaCollapsibleMessageText control) =>
        (FrameworkElement)control.FindName("TextClip");

    private static System.Windows.Controls.TextBlock Sizer(PiaCollapsibleMessageText control) =>
        (System.Windows.Controls.TextBlock)control.FindName("Sizer");

    private static System.Windows.Controls.RichTextBox Body(PiaCollapsibleMessageText control) =>
        (System.Windows.Controls.RichTextBox)control.FindName("Body");

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
