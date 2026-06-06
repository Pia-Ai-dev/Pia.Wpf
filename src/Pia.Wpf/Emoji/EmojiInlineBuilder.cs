using System.Collections.Generic;
using System.Windows;
using System.Windows.Data;
using System.Windows.Documents;
using Pia.Controls;

namespace Pia.Emoji;

/// <summary>
/// Turns a string into a sequence of <see cref="Inline"/>s for FlowDocument hosts (the read-only
/// assistant-message <c>RichTextBox</c>): a <see cref="Run"/> for each text span and an
/// <see cref="InlineUIContainer"/> wrapping an <see cref="EmojiPresenter"/> for each emoji, so emoji
/// appear in color inline with the text.
/// </summary>
public static class EmojiInlineBuilder
{
    /// <summary>Splits <paramref name="text"/> into text and color-emoji inlines.</summary>
    public static IEnumerable<Inline> Build(string? text)
    {
        foreach (var (segment, isEmoji) in EmojiScanner.Segment(text))
            yield return isEmoji ? CreateEmojiInline(segment) : new Run(segment);
    }

    private static Inline CreateEmojiInline(string emoji)
    {
        var presenter = new EmojiPresenter { Emoji = emoji };

        // Size the glyph to the surrounding text. TextElement.FontSize is inherited, so it flows into
        // the hosted element from the FlowDocument context (body text, headings, …) and updates live.
        presenter.SetBinding(EmojiPresenter.GlyphSizeProperty, new Binding
        {
            Path = new PropertyPath(TextElement.FontSizeProperty),
            RelativeSource = new RelativeSource(RelativeSourceMode.Self),
        });

        return new InlineUIContainer(presenter) { BaselineAlignment = BaselineAlignment.Center };
    }
}
