using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Documents;
using Pia.Controls.Markdown;
using Pia.Tests.Views;
using Xunit;

namespace Pia.Tests.Controls;

/// <summary>Indentation is semantic in YAML, so a code card must show the fence body byte-for-byte.</summary>
[Collection("WpfApplicationStatic")]
public class CodeBlockIndentationTests
{
    [Fact]
    public void FenceAtColumnZero_KeepsNestedIndentation()
    {
        var code = CodeOf("```yaml\nroot:\n  child: 1\n    deep: 2\n```");

        Assert.Equal("root:\n  child: 1\n    deep: 2", Normalize(code));
    }

    [Fact]
    public void IndentedFence_KeepsNestedIndentation()
    {
        var code = CodeOf("   ```yaml\n   root:\n     child: 1\n   ```");

        Assert.Equal("root:\n  child: 1", Normalize(code));
    }

    [Fact]
    public void IndentedFence_WithFlushBody_KeepsNestedIndentation()
    {
        var code = CodeOf("   ```yaml\nroot:\n  child: 1\n   ```");

        Assert.Equal("root:\n  child: 1", Normalize(code));
    }

    [Fact]
    public void FenceInsideListItem_KeepsNestedIndentation()
    {
        var code = CodeOf("1. step\n\n   ```yaml\n   root:\n     child: 1\n   ```\n");

        Assert.Equal("root:\n  child: 1", Normalize(code));
    }

    // A flush-left body ends the list item, so the fence closes empty and the YAML falls into a lazy
    // continuation paragraph. Nothing survives to show, so no card is drawn.
    [Fact]
    public void FenceInsideListItem_WithFlushBody_DrawsNoCard()
    {
        var codes = CodeBlocksOf("1. step\n\n   ```yaml\nroot:\n  child: 1\n   ```\n");

        Assert.Empty(codes);
    }

    [Fact]
    public void TabIndentedContent_KeepsTabs()
    {
        var code = CodeOf("```yaml\nroot:\n\tchild: 1\n```");

        Assert.Equal("root:\n\tchild: 1", Normalize(code));
    }

    [Fact]
    public void CrlfSource_KeepsNestedIndentation()
    {
        var code = CodeOf("```yaml\r\nroot:\r\n  child: 1\r\n```");

        Assert.Equal("root:\n  child: 1", Normalize(code));
    }

    [Fact]
    public void FenceInListWithoutBlankLine_KeepsNestedIndentation()
    {
        var code = CodeOf("1. step\n   ```yaml\n   root:\n     child: 1\n   ```\n");

        Assert.Equal("root:\n  child: 1", Normalize(code));
    }

    [Fact]
    public void IndentedCodeBlock_KeepsNestedIndentation()
    {
        var code = CodeOf("text\n\n    root:\n      child: 1\n");

        Assert.Equal("root:\n  child: 1", Normalize(code));
    }

    [Fact]
    public void TildeFence_KeepsNestedIndentation()
    {
        var code = CodeOf("~~~yaml\nroot:\n  child: 1\n~~~");

        Assert.Equal("root:\n  child: 1", Normalize(code));
    }

    [Fact]
    public void FenceInBlockquote_KeepsNestedIndentation()
    {
        var code = CodeOf("> ```yaml\n> root:\n>   child: 1\n> ```");

        Assert.Equal("root:\n  child: 1", Normalize(code));
    }

    [Fact]
    public void ListItemDeepIndent_KeepsNestedIndentation()
    {
        var code = CodeOf("- step\n\n  ```yaml\n  services:\n    web:\n      image: nginx\n  ```\n");

        Assert.Equal("services:\n  web:\n    image: nginx", Normalize(code));
    }

    [Fact]
    public void IndentedFence_DeepFlushBody_KeepsEveryLevel()
    {
        var code = CodeOf(
            "   ```yaml\nenv:\n- name: DATABASE_URL\n  valueFrom:\n    secretKeyRef:\n      name: test-db-app\n      key: uri\n   ```");

        Assert.Equal(
            "env:\n- name: DATABASE_URL\n  valueFrom:\n    secretKeyRef:\n      name: test-db-app\n      key: uri",
            Normalize(code));
    }

    [Fact]
    public void FenceBody_EndsWithoutATrailingBlankLine()
    {
        var code = CodeOf("```yaml\nroot:\n  child: 1\n```");

        Assert.Equal("root:\n  child: 1", code);
    }

    private static string Normalize(string code) => code.Replace("\r\n", "\n").TrimEnd('\n');

    private static string CodeOf(string markdown)
    {
        var blocks = CodeBlocksOf(markdown);
        Assert.NotEmpty(blocks);
        return blocks[0];
    }

    private static IReadOnlyList<string> CodeBlocksOf(string markdown)
    {
        IReadOnlyList<string>? codes = null;
        WpfStaHost.Run(() =>
        {
            var doc = PiaMarkdownRenderer.Render(markdown);
            codes = doc.Blocks.SelectMany(Flatten)
                .OfType<BlockUIContainer>()
                .Select(c => c.Child)
                .OfType<CodeBlockControl>()
                .Select(control => (RichTextBox)control.FindName("CodeViewer")!)
                .Select(viewer => string.Concat(
                    viewer.Document.Blocks.OfType<Paragraph>()
                        .SelectMany(p => p.Inlines.OfType<Run>())
                        .Select(r => r.Text)))
                .ToList();
            return 0;
        });
        return codes!;
    }

    private static IEnumerable<Block> Flatten(Block block)
    {
        yield return block;
        if (block is Section section)
        {
            foreach (var nested in section.Blocks.SelectMany(Flatten))
                yield return nested;
        }
        if (block is List list)
        {
            foreach (var nested in list.ListItems.SelectMany(i => i.Blocks).SelectMany(Flatten))
                yield return nested;
        }
    }
}
