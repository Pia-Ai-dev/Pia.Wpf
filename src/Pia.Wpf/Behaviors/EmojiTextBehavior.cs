using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Pia.Emoji;

namespace Pia.Behaviors;

/// <summary>
/// Attached behavior that fills a <see cref="TextBlock"/>'s inlines from <see cref="EmojiInlineBuilder"/>
/// so emoji in otherwise-plain display text — chat titles, message previews — render in color inline with
/// the surrounding text instead of as monochrome Segoe UI Emoji glyphs. Unlike
/// <see cref="AtCommandHighlightBehavior"/> it does no @-command pill styling, so it suits read-only labels
/// (titles, snippets) where a command pill would be out of place.
/// </summary>
public static class EmojiTextBehavior
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached("Text", typeof(string),
            typeof(EmojiTextBehavior), new PropertyMetadata(null, OnTextChanged));

    public static string? GetText(DependencyObject obj) => (string?)obj.GetValue(TextProperty);
    public static void SetText(DependencyObject obj, string? value) => obj.SetValue(TextProperty, value);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock textBlock)
            return;

        textBlock.Inlines.Clear();

        // Plain text (the common case) comes back as a single Run; only emoji become InlineUIContainers.
        if (e.NewValue is string text && text.Length > 0)
            foreach (var inline in EmojiInlineBuilder.Build(text))
                textBlock.Inlines.Add(inline);
    }
}
