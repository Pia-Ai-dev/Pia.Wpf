using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Pia.Controls;
using Pia.Emoji;
using Xunit;

namespace Pia.Tests.Emoji;

// EmojiPresenter is a WPF element, so anything that constructs one (i.e. any emoji inline) must run
// on an STA thread; the assertions run there too because the elements are thread-affinitized.
public class EmojiInlineBuilderTests
{
    [Fact]
    public void PlainText_ProducesASingleRun() => RunSta(() =>
    {
        var inlines = EmojiInlineBuilder.Build("just text").ToList();
        var run = Assert.IsType<Run>(Assert.Single(inlines));
        Assert.Equal("just text", run.Text);
    });

    [Fact]
    public void Emoji_BecomesAnInlineUIContainerHostingAnEmojiPresenter() => RunSta(() =>
    {
        var inlines = EmojiInlineBuilder.Build("😀").ToList();
        var container = Assert.IsType<InlineUIContainer>(Assert.Single(inlines));
        var presenter = Assert.IsType<EmojiPresenter>(container.Child);
        Assert.Equal("😀", presenter.Emoji);
    });

    [Fact]
    public void MixedContent_InterleavesRunsAndEmojiContainers() => RunSta(() =>
    {
        var inlines = EmojiInlineBuilder.Build("Hi 👋 there").ToList();
        Assert.Collection(inlines,
            i => Assert.Equal("Hi ", Assert.IsType<Run>(i).Text),
            i => Assert.IsType<InlineUIContainer>(i),
            i => Assert.Equal(" there", Assert.IsType<Run>(i).Text));
    });

    [Fact]
    public void EmojiPresenter_SizesToTheInheritedFontSize() => RunSta(() =>
    {
        // The plan hosts emoji inline via InlineUIContainer; the presenter must scale with the
        // surrounding text (e.g. larger in headings). Verify the GlyphSize binding resolves the
        // inherited TextElement.FontSize through the container into the hosted element.
        var container = (InlineUIContainer)EmojiInlineBuilder.Build("🌟").Single();
        var presenter = (EmojiPresenter)container.Child;

        var host = new TextBlock { FontSize = 30, Width = 200 };
        host.Inlines.Add(container);
        host.Measure(new Size(200, 200));
        host.Arrange(new Rect(0, 0, 200, 200));
        host.UpdateLayout();

        Assert.Equal(30.0, presenter.GlyphSize);
    });

    private static void RunSta(Action body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
            throw error;
    }
}
