using System.Globalization;
using System.Text;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Pia.Helpers;

public static class SpeakableText
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseGridTables()
        .UseEmphasisExtras()
        .UseTaskLists()
        .UseAutoLinks()
        .Build();

    public static string FromMarkdown(string markdown)
    {
        var lines = new List<string>();
        AppendBlocks(Markdown.Parse(markdown, Pipeline), lines);
        return string.Join("\n", lines);
    }

    private static void AppendBlocks(ContainerBlock container, List<string> lines)
    {
        foreach (var block in container)
        {
            switch (block)
            {
                case Table or LinkReferenceDefinitionGroup:
                    break;
                case ContainerBlock child:
                    AppendBlocks(child, lines);
                    break;
                case ParagraphBlock or HeadingBlock when ((LeafBlock)block).Inline is { } inline:
                    var text = new StringBuilder();
                    AppendInlines(inline, text);
                    var line = RemoveEmoji(text.ToString()).Trim();
                    if (line.Length > 0)
                        lines.Add(line);
                    break;
            }
        }
    }

    private static void AppendInlines(ContainerInline container, StringBuilder text)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    text.Append(literal.Content.AsSpan());
                    break;
                case CodeInline code:
                    text.Append(code.Content);
                    break;
                case HtmlEntityInline entity:
                    text.Append(entity.Transcoded.AsSpan());
                    break;
                case LineBreakInline:
                    text.Append(' ');
                    break;
                case LinkInline link when !link.IsImage && !link.IsAutoLink:
                    AppendInlines(link, text);
                    break;
                case LinkInline:
                    break;
                case EmphasisInline emphasis:
                    AppendInlines(emphasis, text);
                    break;
            }
        }
    }

    private static string RemoveEmoji(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (!IsEmoji(rune))
                result.Append(rune.ToString());
        }
        return result.ToString();
    }

    private static bool IsEmoji(Rune rune)
    {
        if (rune.Value is 0xFE0F or 0x200D)
            return true;
        var category = Rune.GetUnicodeCategory(rune);
        if (category is not (UnicodeCategory.OtherSymbol or UnicodeCategory.ModifierSymbol))
            return false;
        return rune.Value >= 0x1F000 || rune.Value is >= 0x2600 and <= 0x27BF;
    }
}
