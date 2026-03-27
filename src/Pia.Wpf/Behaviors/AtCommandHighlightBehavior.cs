using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Pia.Behaviors;

/// <summary>
/// Attached behavior for TextBlock that highlights @-commands with a distinct style.
/// Replaces plain text binding with styled Inlines.
/// </summary>
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
        if (d is not TextBlock textBlock) return;

        var text = e.NewValue as string;
        textBlock.Inlines.Clear();

        if (string.IsNullOrEmpty(text))
            return;

        var matches = CommandHighlightPattern().Matches(text);

        if (matches.Count == 0)
        {
            textBlock.Inlines.Add(new Run(text));
            return;
        }

        int lastIndex = 0;
        foreach (Match match in matches)
        {
            // Add text before the match
            if (match.Index > lastIndex)
            {
                textBlock.Inlines.Add(new Run(text[lastIndex..match.Index]));
            }

            // Add the @-command with highlight styling
            var commandRun = new Run(match.Groups[1].Value)
            {
                Foreground = GetCommandForeground(textBlock),
                FontWeight = FontWeights.SemiBold
            };

            // Wrap in a border-like effect using an InlineUIContainer with a small Border
            var border = new Border
            {
                Background = GetCommandBackground(textBlock),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(4, 1, 4, 1),
                Margin = new Thickness(0, 0, 2, 0),
                Child = new TextBlock
                {
                    Text = match.Groups[1].Value,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = GetCommandForeground(textBlock),
                    FontSize = textBlock.FontSize
                }
            };

            textBlock.Inlines.Add(new InlineUIContainer(border) { BaselineAlignment = BaselineAlignment.Center });

            lastIndex = match.Index + match.Length;
        }

        // Add remaining text after last match
        if (lastIndex < text.Length)
        {
            textBlock.Inlines.Add(new Run(text[lastIndex..]));
        }
    }

    private static Brush GetCommandForeground(FrameworkElement element)
    {
        // Keep white to match the user bubble's existing text color
        return Brushes.White;
    }

    private static Brush GetCommandBackground(FrameworkElement element)
    {
        // Semi-transparent white pill on the blue bubble — visible in both light and dark themes
        return new SolidColorBrush(Color.FromArgb(50, 255, 255, 255));
    }
}
