using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Pia.Behaviors;

/// <summary>Copies a <see cref="RichTextBox"/> selection with the text its emoji, pills and code cards stand for;
/// WPF's own copy writes each embedded element as one space.</summary>
public static class SelectionCopyBehavior
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached("IsEnabled", typeof(bool),
            typeof(SelectionCopyBehavior), new PropertyMetadata(false, OnIsEnabledChanged));

    /// <summary>Set on an <see cref="InlineUIContainer"/> or <see cref="BlockUIContainer"/>: the text it copies as.</summary>
    public static readonly DependencyProperty CopyTextProperty =
        DependencyProperty.RegisterAttached("CopyText", typeof(string),
            typeof(SelectionCopyBehavior), new PropertyMetadata(null));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    public static string? GetCopyText(DependencyObject obj) => (string?)obj.GetValue(CopyTextProperty);
    public static void SetCopyText(DependencyObject obj, string? value) => obj.SetValue(CopyTextProperty, value);

    public static string GetSelectedText(RichTextBox box)
    {
        var selection = box.Selection;
        var text = selection.Text;
        if (!HasCopyText(selection)) return text;

        // List markers, table rows and cell ranges stay WPF's to lay out: the selection is read off a twin whose
        // stand-ins are Runs. The twin of plain spaces has to reproduce WPF's text, or the mapping is not trusted.
        try
        {
            return new Twin(box.Document, _ => " ").Read(selection) == text
                ? new Twin(box.Document, copyText => copyText).Read(selection)
                : text;
        }
        catch (NotSupportedException)
        {
            return text;
        }
    }

    private static bool HasCopyText(TextRange range)
    {
        for (var position = range.Start;
             position is not null && position.CompareTo(range.End) < 0;
             position = position.GetNextContextPosition(LogicalDirection.Forward))
        {
            if (position.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.EmbeddedElement
                && position.Parent is TextElement container && GetCopyText(container) is not null)
                return true;
        }

        return false;
    }

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RichTextBox box) return;

        DataObject.RemoveCopyingHandler(box, OnCopying);
        if ((bool)e.NewValue)
            DataObject.AddCopyingHandler(box, OnCopying);
    }

    private static void OnCopying(object sender, DataObjectCopyingEventArgs e)
    {
        // A code card's own box raises its copy inside this document; that selection is not ours.
        if (sender is not RichTextBox box || !ReferenceEquals(e.OriginalSource, box)) return;

        var text = GetSelectedText(box);
        e.DataObject.SetData(DataFormats.UnicodeText, text);
        e.DataObject.SetData(DataFormats.Text, text);
    }

    /// <summary>The document's structure with each stand-in as text; every other element keeps its symbol count,
    /// so a position maps across by its offset plus what the stand-ins before it grew by.</summary>
    private sealed class Twin
    {
        private readonly FlowDocument _source;
        private readonly Func<string, string> _standIn;
        private readonly FlowDocument _document = new();
        private readonly List<(int SourceEnd, int Growth)> _growth = [];

        public Twin(FlowDocument source, Func<string, string> standIn)
        {
            _source = source;
            _standIn = standIn;
            _document.Blocks.AddRange(source.Blocks.Select(Clone).ToList());
        }

        public string Read(TextRange selection) => new TextRange(Map(selection.Start), Map(selection.End)).Text;

        private TextPointer Map(TextPointer position)
        {
            var offset = _source.ContentStart.GetOffsetToPosition(position);
            var growth = _growth.Where(g => g.SourceEnd <= offset).Sum(g => g.Growth);
            return _document.ContentStart.GetPositionAtOffset(offset + growth, position.LogicalDirection)
                ?? throw new NotSupportedException();
        }

        private Block Clone(Block block) => block switch
        {
            Paragraph paragraph => WithInlines(new Paragraph(), paragraph.Inlines),
            Section section => WithBlocks(new Section(), section.Blocks),
            List list => WithItems(new List { MarkerStyle = list.MarkerStyle, StartIndex = list.StartIndex }, list),
            Table table => WithRowGroups(new Table(), table),
            BlockUIContainer container when GetCopyText(container) is { } copyText =>
                StandIn(container, new Paragraph(new Run(_standIn(copyText)))),
            BlockUIContainer => new BlockUIContainer(new Border()),
            _ => throw new NotSupportedException(),
        };

        private Inline Clone(Inline inline) => inline switch
        {
            Run run => new Run(run.Text),
            LineBreak => new LineBreak(),
            Span span => WithInlines(new Span(), span.Inlines),
            InlineUIContainer container when GetCopyText(container) is { } copyText =>
                StandIn(container, new Run(_standIn(copyText))),
            InlineUIContainer => new InlineUIContainer(new Border()),
            _ => throw new NotSupportedException(),
        };

        private T StandIn<T>(TextElement source, T twin) where T : TextElement
        {
            _growth.Add((_source.ContentStart.GetOffsetToPosition(source.ElementEnd), Size(twin) - Size(source)));
            return twin;
        }

        private static int Size(TextElement element) => element switch
        {
            Paragraph { Inlines.FirstInline: Run run } => run.Text.Length + 4,
            Run run => run.Text.Length + 2,
            _ => element.ElementStart.GetOffsetToPosition(element.ElementEnd),
        };

        private Paragraph WithInlines(Paragraph twin, InlineCollection inlines)
        {
            twin.Inlines.AddRange(inlines.Select(Clone).ToList());
            return twin;
        }

        private Span WithInlines(Span twin, InlineCollection inlines)
        {
            twin.Inlines.AddRange(inlines.Select(Clone).ToList());
            return twin;
        }

        private Section WithBlocks(Section twin, BlockCollection blocks)
        {
            twin.Blocks.AddRange(blocks.Select(Clone).ToList());
            return twin;
        }

        private List WithItems(List twin, List source)
        {
            foreach (var item in source.ListItems)
            {
                var twinItem = new ListItem();
                twinItem.Blocks.AddRange(item.Blocks.Select(Clone).ToList());
                twin.ListItems.Add(twinItem);
            }

            return twin;
        }

        private Table WithRowGroups(Table twin, Table source)
        {
            foreach (var group in source.RowGroups)
            {
                var twinGroup = new TableRowGroup();
                foreach (var row in group.Rows)
                {
                    var twinRow = new TableRow();
                    foreach (var cell in row.Cells)
                    {
                        var twinCell = new TableCell { ColumnSpan = cell.ColumnSpan, RowSpan = cell.RowSpan };
                        twinCell.Blocks.AddRange(cell.Blocks.Select(Clone).ToList());
                        twinRow.Cells.Add(twinCell);
                    }

                    twinGroup.Rows.Add(twinRow);
                }

                twin.RowGroups.Add(twinGroup);
            }

            return twin;
        }
    }
}
