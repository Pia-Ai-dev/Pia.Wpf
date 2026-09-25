using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Pia.Controls;
using Pia.Controls.Chat;
using Pia.Emoji;
using Pia.Tests.Views;
using Xunit;

namespace Pia.Tests.Controls;

/// <summary>
/// Emoji, @-command pills and code cards render as embedded elements, which WPF's own copy turns into one
/// space each. What lands on the clipboard has to be the text the reader sees.
/// </summary>
[Collection("WpfApplicationStatic")]
public class SelectionCopyTests
{
    [Fact]
    public void CopyingAnAnswer_KeepsItsEmoji()
    {
        const string markdown =
            "Hier sind drei passende Emojis dafür:\n\n📁 ⬆️ ✅\n\nAlternativ funktioniert auch 📤 ☁️ ✔️ super.";

        Assert.Equal(
            "Hier sind drei passende Emojis dafür:\r\n📁 ⬆️ ✅\r\nAlternativ funktioniert auch 📤 ☁️ ✔️ super.\r\n",
            WpfStaHost.Run(() => CopyAll(Answer(markdown))));
    }

    [Fact]
    public void CopyingPartOfAnAnswer_KeepsTheEmojiInsideTheSelection()
    {
        Assert.Equal("auch 📤 ☁️ ✔️ super", WpfStaHost.Run(() =>
        {
            var answer = Answer("Alternativ funktioniert auch 📤 ☁️ ✔️ super.");
            var viewer = Viewer(answer);
            var text = new TextRange(viewer.Document.ContentStart, viewer.Document.ContentEnd);
            var start = text.Start.GetInsertionPosition(LogicalDirection.Forward);
            viewer.Selection.Select(Advance(start, "Alternativ funktioniert ".Length), Before(viewer.Document, "."));
            return Copy(answer, viewer);
        }));
    }

    /// <summary>A boundary placed inside an emoji's container, either side of the image, still takes the whole emoji.</summary>
    [Fact]
    public void ASelectionBoundaryInsideAnEmoji_CopiesTheWholeEmoji()
    {
        var (upTo, from) = WpfStaHost.Run(() =>
        {
            var answer = Answer("ab 😀 cd");
            var viewer = Viewer(answer);
            var document = viewer.Document;
            var emoji = Emoji(document);

            viewer.Selection.Select(document.ContentStart, emoji.ContentEnd);
            var upTo = Copy(answer, viewer);

            viewer.Selection.Select(emoji.ContentStart, document.ContentEnd);
            return (upTo, Copy(answer, viewer));
        });

        Assert.Equal("ab 😀", upTo);
        Assert.Equal("😀 cd\r\n", from);
    }

    private static InlineUIContainer Emoji(FlowDocument document)
    {
        for (var p = document.ContentStart; p is not null; p = p.GetNextContextPosition(LogicalDirection.Forward))
        {
            if (p.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.EmbeddedElement)
                return (InlineUIContainer)p.Parent;
        }

        throw new InvalidOperationException("the document holds no emoji");
    }

    [Fact]
    public void EmojiInsideFormattingListsAndTables_KeepTheirPlaceAndTheMarkers()
    {
        const string markdown =
            "# Title 🎉\n\n> quote 💬\n\n[link 🔗](https://example.com)\n\n" +
            "😀😀 **bold 🔥 text**\n\n- ✅ item one\n- item two 🎉\n\n1. 🚀 first\n2. second\n3. third 🎯\n\n" +
            "| a | b |\n|---|---|\n| 😀 | x |";

        Assert.Equal(
            "Title 🎉\r\nquote 💬\r\nlink 🔗\r\n" +
            "😀😀 bold 🔥 text\r\n•\t✅ item one\r\n•\titem two 🎉\r\n1.\t🚀 first\r\n2.\tsecond\r\n3.\tthird 🎯\r\n" +
            "a\tb\r\n😀\tx\r\n",
            WpfStaHost.Run(() => CopyAll(Answer(markdown))));
    }

    /// <summary>A selection that runs into a table takes the whole row, so the table text is WPF's to lay out.</summary>
    [Fact]
    public void ASelectionEndingInsideATable_KeepsTheEmojiOfTheRowsItTakes()
    {
        Assert.Equal("intro 🙂\r\na\tb\r\n😀\tx ✅\r\n", WpfStaHost.Run(() =>
        {
            var answer = Answer("intro 🙂\n\n| a | b |\n|---|---|\n| 😀 | x ✅ |\n| y | z |\n\nafter 🎯");
            var viewer = Viewer(answer);
            viewer.Selection.Select(viewer.Document.ContentStart, Before(viewer.Document, "x"));
            return Copy(answer, viewer);
        }));
    }

    [Fact]
    public void CopyingAcrossACodeCard_KeepsItsCode()
    {
        const string markdown = "before 😀\n\n```csharp\nvar x = 1;\nvar y = 2;\n```\n\nafter";

        Assert.Equal(
            "before 😀\r\nvar x = 1;\r\nvar y = 2;\r\nafter\r\n",
            WpfStaHost.Run(() => CopyAll(Answer(markdown))));
    }

    /// <summary>The card's own box sits inside the answer's document, so its copy bubbles through the answer's box.</summary>
    [Fact]
    public void CopyingInsideACodeCard_CopiesOnlyWhatIsSelectedThere()
    {
        var (copied, selected) = WpfStaHost.Run(() =>
        {
            var answer = Answer("before 😀\n\n```csharp\nvar x = 1;\n```\n\nafter");
            var card = (FrameworkElement)Viewer(answer).Document.Blocks.OfType<BlockUIContainer>().Single().Child;
            var code = (RichTextBox)card.FindName("CodeViewer")!;
            code.SelectAll();
            return (Copy(answer, code), code.Selection.Text);
        });

        Assert.Equal(selected, copied);
        Assert.StartsWith("var x = 1;", copied);
    }

    [Fact]
    public void CopyingASentMessage_KeepsItsEmojiAndCommands()
    {
        Assert.Equal("hi 👋 there @summarize now ✅\r\n", WpfStaHost.Run(() =>
        {
            var (bubble, body) = Bubble("hi 👋 there @summarize now ✅");
            body.SelectAll();
            return Copy(bubble, body);
        }));
    }

    /// <summary>Word and Outlook paste the RTF, another WPF box the XAML, so those carry the commands too.</summary>
    [Fact]
    public void CopyingASentMessage_KeepsItsCommandsInTheRichFormats()
    {
        var (rtf, xaml, _) = WpfStaHost.Run(() =>
        {
            var (bubble, body) = Bubble("hi 👋 there @summarize now ✅");
            body.SelectAll();
            return CopyRich(bubble, body);
        });

        Assert.Contains("@summarize", rtf);
        Assert.Contains("@summarize", xaml);
    }

    /// <summary>A rendered emoji travels as its picture; the command beside it still arrives as text.</summary>
    [Fact]
    public void CopyingASentMessageWithRenderedEmoji_KeepsThePicturesAndTheCommand()
    {
        var (rtf, _, package) = WpfStaHost.Run(() =>
        {
            var (bubble, body) = Bubble("hi 👋 there @summarize now ✅");
            foreach (var presenter in Presenters(body.Document))
                presenter.Source = EmojiImageRenderer.Shared.Render(presenter.Emoji, 20);
            body.SelectAll();
            return CopyRich(bubble, body);
        });

        Assert.Contains("\\pict", rtf);
        Assert.Contains("@summarize", rtf);
        Assert.NotNull(package);
        Assert.Contains("@summarize", package);
    }

    [Fact]
    public void CopyingPartOfASentMessage_KeepsTheCommandInTheRichFormats()
    {
        var (rtf, xaml, _) = WpfStaHost.Run(() =>
        {
            var (bubble, body) = Bubble("hi 👋 there @summarize now ✅");
            body.Selection.Select(Before(body.Document, "there"), Before(body.Document, " now"));
            return CopyRich(bubble, body);
        });

        Assert.Contains("@summarize", rtf);
        Assert.Contains("@summarize", xaml);
        Assert.DoesNotContain("hi ", xaml);
    }

    [Fact]
    public void AddingASelectionToThePiiList_KeepsItsEmoji()
    {
        Assert.Equal("Max 😀", WpfStaHost.Run(() =>
        {
            var answer = Answer("Max 😀");
            Viewer(answer).SelectAll();
            return answer.GetSelectedText();
        }));
    }

    private static MarkdownMessageControl Answer(string markdown)
    {
        var control = new MarkdownMessageControl { MarkdownText = markdown };
        Layout(control);
        return control;
    }

    private static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(600, 2000));
        element.Arrange(new Rect(0, 0, 600, 2000));
        element.UpdateLayout();
    }

    private static RichTextBox Viewer(MarkdownMessageControl control) =>
        (RichTextBox)control.FindName("MarkdownViewer")!;

    private static string CopyAll(MarkdownMessageControl answer)
    {
        var viewer = Viewer(answer);
        viewer.SelectAll();
        return Copy(answer, viewer);
    }

    private static (PiaCollapsibleMessageText Bubble, RichTextBox Body) Bubble(string text)
    {
        var bubble = new PiaCollapsibleMessageText { Text = text };
        Layout(bubble);
        return (bubble, (RichTextBox)bubble.FindName("Body")!);
    }

    private static IEnumerable<EmojiPresenter> Presenters(FlowDocument document)
    {
        for (var p = document.ContentStart; p is not null; p = p.GetNextContextPosition(LogicalDirection.Forward))
        {
            if (p.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.EmbeddedElement
                && p.GetAdjacentElement(LogicalDirection.Forward) is EmojiPresenter presenter)
                yield return presenter;
        }
    }

    private static string Copy(FrameworkElement host, RichTextBox box)
    {
        string? unicode = null;
        string? ansi = null;
        CopyInto(host, box, data =>
        {
            unicode = data.GetData(DataFormats.UnicodeText, false) as string;
            ansi = data.GetData(DataFormats.Text, false) as string;
        });

        Assert.Equal(unicode, ansi);
        return unicode ?? "<no text on the clipboard>";
    }

    /// <summary>The package comes back as the text it loads to, since a WPF stream cannot leave the host thread.</summary>
    private static (string Rtf, string Xaml, string? Package) CopyRich(FrameworkElement host, RichTextBox box)
    {
        (string, string, string?) formats = default;
        CopyInto(host, box, data => formats = (
            data.GetData(DataFormats.Rtf, false) as string ?? "<no RTF>",
            data.GetData(DataFormats.Xaml, false) as string ?? "<no XAML>",
            data.GetData(DataFormats.XamlPackage, false) is Stream package ? TextOf(package) : null));
        return formats;
    }

    /// <summary>Listens on the host so every handler on the box has run, and cancels so the real clipboard stays untouched.</summary>
    private static void CopyInto(FrameworkElement host, RichTextBox box, Action<IDataObject> read)
    {
        void Capture(object sender, DataObjectCopyingEventArgs e)
        {
            read(e.DataObject);
            e.CancelCommand();
        }

        DataObject.AddCopyingHandler(host, Capture);
        try
        {
            ApplicationCommands.Copy.Execute(null, box);
        }
        finally
        {
            DataObject.RemoveCopyingHandler(host, Capture);
        }
    }

    private static string TextOf(Stream package)
    {
        var document = new FlowDocument();
        package.Position = 0;
        new TextRange(document.ContentStart, document.ContentEnd).Load(package, DataFormats.XamlPackage);
        return new TextRange(document.ContentStart, document.ContentEnd).Text;
    }

    private static TextPointer Advance(TextPointer start, int characters)
    {
        var position = start;
        for (var i = 0; i < characters; i++)
            position = position.GetNextInsertionPosition(LogicalDirection.Forward)!;
        return position;
    }

    private static TextPointer Before(FlowDocument document, string marker)
    {
        for (var p = document.ContentStart; p is not null; p = p.GetNextContextPosition(LogicalDirection.Forward))
        {
            if (p.GetPointerContext(LogicalDirection.Forward) != TextPointerContext.Text) continue;
            var index = p.GetTextInRun(LogicalDirection.Forward).IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0) return p.GetPositionAtOffset(index)!;
        }

        throw new InvalidOperationException($"'{marker}' is not in the document");
    }
}
