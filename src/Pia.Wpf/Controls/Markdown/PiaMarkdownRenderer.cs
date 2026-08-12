using System;
using System.Linq;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Pia.Emoji;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using WpfBlock = System.Windows.Documents.Block;
using WpfInline = System.Windows.Documents.Inline;
using WpfTable = System.Windows.Documents.Table;
using WpfTableRow = System.Windows.Documents.TableRow;
using WpfTableCell = System.Windows.Documents.TableCell;

namespace Pia.Controls.Markdown;

internal static class PiaMarkdownRenderer
{
    private static readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public static FlowDocument Render(string markdown)
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(0),
        };
        doc.SetResourceReference(FrameworkContentElement.StyleProperty, "PiaMarkdownStyle");

        if (string.IsNullOrEmpty(markdown))
        {
            return doc;
        }

        var ast = Markdig.Markdown.Parse(markdown, _pipeline);
        foreach (var block in ast)
        {
            var rendered = RenderBlock(block);
            if (rendered is not null)
            {
                doc.Blocks.Add(rendered);
            }
        }

        return doc;
    }

    // FencedCodeBlock must precede CodeBlock — it is a subtype.
    private static WpfBlock? RenderBlock(MdBlock block) => block switch
    {
        HeadingBlock heading => RenderHeading(heading),
        ParagraphBlock paragraph => RenderParagraph(paragraph),
        ListBlock list => RenderList(list),
        QuoteBlock quote => RenderQuote(quote),
        FencedCodeBlock fenced => RenderCodeCard(fenced.Info, JoinCodeLines(fenced)),
        CodeBlock code => RenderCodeCard(string.Empty, JoinCodeLines(code)),
        ThematicBreakBlock => RenderThematicBreak(),
        MdTable table => RenderTable(table),
        HtmlBlock html => RenderHtmlBlock(html),
        _ => null,
    };

    private static Paragraph RenderHeading(HeadingBlock heading)
    {
        var paragraph = new Paragraph
        {
            Tag = $"Heading{Math.Clamp(heading.Level, 1, 4)}",
        };
        AppendInlines(heading.Inline, paragraph.Inlines);
        return paragraph;
    }

    private static Paragraph RenderParagraph(ParagraphBlock block)
    {
        var paragraph = new Paragraph();
        AppendInlines(block.Inline, paragraph.Inlines);
        return paragraph;
    }

    private static List RenderList(ListBlock listBlock)
    {
        var list = new List
        {
            MarkerStyle = listBlock.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
        };

        if (listBlock.IsOrdered &&
            !string.IsNullOrEmpty(listBlock.OrderedStart) &&
            int.TryParse(listBlock.OrderedStart, out var start) &&
            start > 0)
        {
            list.StartIndex = start;
        }

        foreach (var child in listBlock)
        {
            if (child is not ListItemBlock itemBlock) continue;

            var item = new ListItem();
            foreach (var nested in itemBlock)
            {
                var rendered = RenderBlock(nested);
                if (rendered is not null)
                {
                    item.Blocks.Add(rendered);
                }
            }
            list.ListItems.Add(item);
        }

        return list;
    }

    private static Section RenderQuote(QuoteBlock quoteBlock)
    {
        var section = new Section
        {
            Tag = "Blockquote",
        };
        foreach (var nested in quoteBlock)
        {
            var rendered = RenderBlock(nested);
            if (rendered is not null)
            {
                section.Blocks.Add(rendered);
            }
        }
        return section;
    }

    private static BlockUIContainer RenderCodeCard(string? languageHint, string code)
    {
        var control = new CodeBlockControl
        {
            LanguageLabel = NormalizeLabel(languageHint),
        };

        var language = CodeColorizer.ResolveLanguage(languageHint);
        var colorizer = new CodeColorizer();
        var runs = colorizer.Highlight(code, language);
        control.SetContent(code, runs);

        return new BlockUIContainer(control)
        {
            Margin = new Thickness(0),
        };
    }

    private static Paragraph RenderThematicBreak()
    {
        var paragraph = new Paragraph
        {
            Tag = "ThematicBreak",
            Margin = new Thickness(0, 8, 0, 8),
        };
        var border = new System.Windows.Controls.Border
        {
            Height = 1,
            Background = (System.Windows.Media.Brush)Application.Current?.TryFindResource("ControlStrokeColorDefaultBrush")! ?? System.Windows.Media.Brushes.Gray,
        };
        paragraph.Inlines.Add(new InlineUIContainer(border));
        return paragraph;
    }

    private static WpfTable RenderTable(MdTable tableBlock)
    {
        var table = new WpfTable();

        var columnCount = tableBlock.ColumnDefinitions?.Count ?? 0;
        if (columnCount == 0)
        {
            foreach (var row in tableBlock.OfType<MdTableRow>())
            {
                columnCount = Math.Max(columnCount, row.Count);
            }
        }
        for (var i = 0; i < columnCount; i++)
        {
            table.Columns.Add(new TableColumn());
        }

        TableRowGroup? headerGroup = null;
        TableRowGroup? bodyGroup = null;
        var bodyRowIndex = 0;

        foreach (var rowBlock in tableBlock.OfType<MdTableRow>())
        {
            var row = new WpfTableRow();
            if (rowBlock.IsHeader)
            {
                row.Tag = "TableHeader";
            }
            else if ((bodyRowIndex & 1) == 1)
            {
                row.Tag = "TableRowOdd";
            }

            foreach (var cellBlock in rowBlock.OfType<MdTableCell>())
            {
                var cell = new WpfTableCell();
                foreach (var nested in cellBlock)
                {
                    var rendered = RenderBlock(nested);
                    if (rendered is not null)
                    {
                        cell.Blocks.Add(rendered);
                    }
                }
                if (cellBlock.ColumnSpan > 1) cell.ColumnSpan = cellBlock.ColumnSpan;
                if (cellBlock.RowSpan > 1) cell.RowSpan = cellBlock.RowSpan;
                row.Cells.Add(cell);
            }

            if (rowBlock.IsHeader)
            {
                headerGroup ??= new TableRowGroup { Tag = "TableHeaderGroup" };
                headerGroup.Rows.Add(row);
            }
            else
            {
                bodyGroup ??= new TableRowGroup();
                bodyGroup.Rows.Add(row);
                bodyRowIndex++;
            }
        }

        if (headerGroup is not null) table.RowGroups.Add(headerGroup);
        if (bodyGroup is not null) table.RowGroups.Add(bodyGroup);

        return table;
    }

    private static WpfBlock? RenderHtmlBlock(HtmlBlock html)
    {
        var raw = html.Lines.ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var paragraph = new Paragraph();
        paragraph.Inlines.Add(new Run(raw));
        return paragraph;
    }

    private static void AppendInlines(ContainerInline? container, InlineCollection target)
    {
        if (container is null) return;

        var inline = container.FirstChild;
        while (inline is not null)
        {
            var rendered = RenderInline(inline);
            if (rendered is not null)
            {
                target.Add(rendered);
            }
            inline = inline.NextSibling;
        }
    }

    private static WpfInline? RenderInline(MdInline inline) => inline switch
    {
        LiteralInline literal => RenderLiteral(literal.Content.ToString()),
        CodeInline codeInline => RenderCodeSpan(codeInline),
        EmphasisInline emphasis => RenderEmphasis(emphasis),
        LinkInline link => RenderLink(link),
        AutolinkInline autolink => RenderAutolink(autolink),
        LineBreakInline lineBreak => lineBreak.IsHard ? new LineBreak() : new Run(" "),
        HtmlEntityInline entity => new Run(entity.Transcoded.ToString()),
        HtmlInline => null,
        ContainerInline container => RenderContainer(container),
        _ => null,
    };

    private static Span RenderContainer(ContainerInline container)
    {
        var span = new Span();
        AppendInlines(container, span.Inlines);
        return span;
    }

    /// <summary>
    /// Renders literal text, splitting emoji into color inline images. Plain text (the common case)
    /// stays a single <see cref="Run"/>; text containing emoji becomes a <see cref="Span"/> of text
    /// runs interleaved with <c>InlineUIContainer</c> emoji.
    /// </summary>
    private static WpfInline RenderLiteral(string text)
    {
        var inlines = EmojiInlineBuilder.Build(text).ToList();
        if (inlines.Count == 1 && inlines[0] is Run run)
            return run;

        var span = new Span();
        foreach (var inline in inlines)
            span.Inlines.Add(inline);
        return span;
    }

    private static Run RenderCodeSpan(CodeInline code)
    {
        return new Run(code.Content)
        {
            Tag = "CodeSpan",
        };
    }

    private static WpfInline RenderEmphasis(EmphasisInline emphasis)
    {
        Span span;
        if (emphasis.DelimiterChar == '~')
        {
            span = new Span { TextDecorations = TextDecorations.Strikethrough };
        }
        else if (emphasis.DelimiterCount >= 2)
        {
            span = new Bold();
        }
        else
        {
            span = new Italic();
        }

        AppendInlines(emphasis, span.Inlines);
        return span;
    }

    private static WpfInline RenderLink(LinkInline link)
    {
        if (link.IsImage)
        {
            return new Run($"[image: {link.Url ?? link.Title ?? string.Empty}]")
            {
                FontStyle = FontStyles.Italic,
            };
        }

        var url = link.Url ?? string.Empty;
        var hyperlink = new Hyperlink();
        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute))
        {
            hyperlink.NavigateUri = absolute;
        }

        AppendInlines(link, hyperlink.Inlines);
        if (hyperlink.Inlines.Count == 0)
        {
            hyperlink.Inlines.Add(new Run(url));
        }

        if (!string.IsNullOrEmpty(link.Title))
        {
            hyperlink.ToolTip = link.Title;
        }

        return hyperlink;
    }

    private static WpfInline RenderAutolink(AutolinkInline autolink)
    {
        var hyperlink = new Hyperlink(new Run(autolink.Url));
        if (Uri.TryCreate(autolink.Url, UriKind.Absolute, out var uri))
        {
            hyperlink.NavigateUri = uri;
        }
        return hyperlink;
    }

    private static string JoinCodeLines(LeafBlock block)
    {
        return block.Lines.Count > 0 ? block.Lines.ToString() : string.Empty;
    }

    private static string NormalizeLabel(string? hint)
    {
        if (string.IsNullOrWhiteSpace(hint)) return string.Empty;
        var trimmed = hint.Trim();
        var spaceIndex = trimmed.IndexOf(' ');
        if (spaceIndex > 0) trimmed = trimmed[..spaceIndex];
        return trimmed;
    }
}
