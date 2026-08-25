using System.Linq;
using System.Windows.Documents;
using Pia.Controls.Markdown;
using Pia.Tests.Views;
using Xunit;

namespace Pia.Tests.Controls;

/// <summary>
/// A ::: fence parses into a Markdig CustomContainer that Pia styles no differently from prose — but it must
/// still reach the answer. Before the fallback existed the renderer dropped the whole fenced region.
/// </summary>
[Collection("WpfApplicationStatic")]
public class MarkdownContainerBlockTests
{
    [Fact]
    public void FencedContainer_RendersItsBody()
    {
        var doc = Render(":::warn Datenverlust\nDieser Schritt loescht alles.\n:::");

        Assert.Contains("Dieser Schritt loescht alles.", PlainText(doc));
    }

    [Fact]
    public void FencedContainer_KeepsInlineFormattingAndSurroundingProse()
    {
        var doc = Render("before\n\n:::info Hinweis\nbody **bold**\n:::\n\nafter");
        var text = PlainText(doc);

        Assert.Contains("before", text);
        Assert.Contains("body bold", text);
        Assert.Contains("after", text);
    }

    [Fact]
    public void UnclosedFence_StillRendersItsBody()
    {
        var doc = Render(":::warn Datenverlust\nno terminator here");

        Assert.Contains("no terminator here", PlainText(doc));
    }

    private static FlowDocument Render(string markdown)
    {
        FlowDocument? doc = null;
        WpfStaHost.Run(() =>
        {
            doc = PiaMarkdownRenderer.Render(markdown);
            return 0;
        });
        return doc!;
    }

    private static string PlainText(FlowDocument doc) =>
        string.Join(
            "\n",
            doc.Blocks.SelectMany(Flatten)
                .OfType<Paragraph>()
                .Select(p => new TextRange(p.ContentStart, p.ContentEnd).Text.Trim()));

    private static System.Collections.Generic.IEnumerable<Block> Flatten(Block block)
    {
        yield return block;
        if (block is Section section)
        {
            foreach (var nested in section.Blocks.SelectMany(Flatten))
                yield return nested;
        }
    }
}
