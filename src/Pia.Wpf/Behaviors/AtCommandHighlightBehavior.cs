using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Pia.Emoji;

namespace Pia.Behaviors;

/// <summary>Builds a message's text into a TextBlock's Inlines, with @-commands as pills and emoji in
/// color.</summary>
public static partial class AtCommandHighlightBehavior
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached("Text", typeof(string),
            typeof(AtCommandHighlightBehavior),
            new PropertyMetadata(null, OnTextChanged));

    public static string? GetText(DependencyObject obj) => (string?)obj.GetValue(TextProperty);
    public static void SetText(DependencyObject obj, string? value) => obj.SetValue(TextProperty, value);

    // Same pattern as AtCommandParser.CommandPattern but we compile it here
    // to avoid coupling view behavior to service layer
    [GeneratedRegex("""(?:^|(?<=\s))(@\w+(?::(?:"[^"]*"|\w*))?)""", RegexOptions.Multiline)]
    private static partial Regex CommandHighlightPattern();

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Control and not TextBlock) return;

        var host = (FrameworkElement)d;
        var inlines = Target(host);
        if (inlines is null) return;

        var text = e.NewValue as string;
        inlines.Clear();

        if (string.IsNullOrEmpty(text))
            return;

        var matches = CommandHighlightPattern().Matches(text);

        if (matches.Count == 0)
        {
            AddText(inlines, text);
            return;
        }

        int lastIndex = 0;
        foreach (Match match in matches)
        {
            if (match.Index > lastIndex)
            {
                AddText(inlines, text[lastIndex..match.Index]);
            }

            var label = new TextBlock
            {
                Text = match.Groups[1].Value,
                FontWeight = FontWeights.SemiBold,
                FontSize = TextElement.GetFontSize(host)
            };
            var border = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(4, 1, 4, 1),
                Margin = new Thickness(0, 0, 2, 0),
                Child = label
            };

            // Resource references, not resolved brushes: the inlines are built before the bubble is in the
            // tree, and they have to follow a theme switch afterwards.
            label.SetResourceReference(TextBlock.ForegroundProperty, "UserBubbleFgBrush");
            border.SetResourceReference(Border.BackgroundProperty, "UserBubbleChipBgBrush");

            inlines.Add(new InlineUIContainer(border) { BaselineAlignment = BaselineAlignment.Center });

            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < text.Length)
        {
            AddText(inlines, text[lastIndex..]);
        }
    }

    // A RichTextBox target fills the paragraph its markup already declares — the document carries the line
    // metrics, and rebuilding it here would drop them.
    private static InlineCollection? Target(FrameworkElement host) => host switch
    {
        TextBlock textBlock => textBlock.Inlines,
        RichTextBox { Document.Blocks.FirstBlock: Paragraph paragraph } => paragraph.Inlines,
        _ => null,
    };

    /// <summary>Adds a plain-text span as inlines, with any emoji rendered in color.</summary>
    private static void AddText(InlineCollection target, string text)
    {
        foreach (var inline in EmojiInlineBuilder.Build(text))
            target.Add(inline);
    }
}
