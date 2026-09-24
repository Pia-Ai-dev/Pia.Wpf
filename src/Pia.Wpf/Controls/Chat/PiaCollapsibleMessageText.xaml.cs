using System.Windows;
using System.Windows.Controls;

namespace Pia.Controls.Chat;

/// <summary>
/// A sent message's text, folded to a few lines with a toggle once it outgrows them — the transcript
/// counterpart of the composer's expander.
/// </summary>
public partial class PiaCollapsibleMessageText : UserControl
{
    private const int CollapsedLines = 5;

    /// <summary>How much wider the box has to be than the text it holds: the document keeps a 5px page
    /// padding a side that it re-applies when overwritten, plus a caret column at the end of each line.
    /// Short of it the last word wraps out of sight — <c>AShortMessageKeepsANarrowBubble</c> holds the line.</summary>
    private const double BoxSlack = 12;

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(PiaCollapsibleMessageText),
            new PropertyMetadata(null, OnTextChanged));

    public static readonly DependencyProperty ToggleAutomationIdProperty =
        DependencyProperty.Register(nameof(ToggleAutomationId), typeof(string), typeof(PiaCollapsibleMessageText),
            new PropertyMetadata(null));

    public static readonly DependencyProperty TextAutomationIdProperty =
        DependencyProperty.Register(nameof(TextAutomationId), typeof(string), typeof(PiaCollapsibleMessageText),
            new PropertyMetadata(null));

    public static readonly DependencyProperty IsOverflowingProperty =
        DependencyProperty.Register(nameof(IsOverflowing), typeof(bool), typeof(PiaCollapsibleMessageText),
            new PropertyMetadata(false));

    public string? Text
    {
        get => (string?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string? ToggleAutomationId
    {
        get => (string?)GetValue(ToggleAutomationIdProperty);
        set => SetValue(ToggleAutomationIdProperty, value);
    }

    public string? TextAutomationId
    {
        get => (string?)GetValue(TextAutomationIdProperty);
        set => SetValue(TextAutomationIdProperty, value);
    }

    /// <summary>True while the text is taller than the folded box — i.e. the toggle is worth offering.</summary>
    public bool IsOverflowing
    {
        get => (bool)GetValue(IsOverflowingProperty);
        set => SetValue(IsOverflowingProperty, value);
    }

    private bool _expanded;
    private Size _natural;
    private double _shown;

    /// <summary>Read off the text, not guessed, so restyling the bubble cannot silently move the fold.</summary>
    private double CollapsedHeight => CollapsedLines * LineHeight;

    private double LineHeight => double.IsNaN(Sizer.LineHeight) ? Sizer.FontSize * 1.5 : Sizer.LineHeight;

    public PiaCollapsibleMessageText()
    {
        InitializeComponent();
        ApplyHeight();
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // A recycled row can arrive holding a shorter message than the one it was expanded for; the toggle
        // goes with it, or it flickers on the new text until the next measure lands.
        var control = (PiaCollapsibleMessageText)d;
        control._expanded = false;
        control.IsOverflowing = false;
        control.ApplyHeight();
    }

    protected override Size MeasureOverride(Size constraint)
    {
        BodyDocument.LineHeight = LineHeight;
        // The sizer wraps in the narrower box the slack leaves, so the two break at the same words.
        Sizer.Measure(new Size(Math.Max(0, constraint.Width - BoxSlack), double.PositiveInfinity));
        _natural = Sizer.DesiredSize;

        Body.Width = Math.Min(constraint.Width, Math.Ceiling(_natural.Width) + BoxSlack);

        // The slack can also let the box fit a short word the sizer had already wrapped, so the fold closes
        // on whichever of the two is shorter rather than leaving an empty line inside the bubble.
        Body.Measure(new Size(Body.Width, double.PositiveInfinity));
        _shown = Math.Min(_natural.Height, Body.DesiredSize.Height);

        // Half a line of slack: a message that fills the fold exactly is not overflowing.
        IsOverflowing = _shown > CollapsedHeight + 0.5;
        ApplyHeight();

        return base.MeasureOverride(constraint);
    }

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        _expanded = !_expanded;
        ApplyHeight();
    }

    private void ApplyHeight()
    {
        var fold = _expanded ? double.PositiveInfinity : CollapsedHeight;
        TextClip.MaxHeight = _shown > 0 ? Math.Min(fold, _shown) : fold;
        MoreLabel.Visibility = _expanded ? Visibility.Collapsed : Visibility.Visible;
        LessLabel.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
        ToggleIcon.Symbol = _expanded
            ? Wpf.Ui.Controls.SymbolRegular.ChevronUp24
            : Wpf.Ui.Controls.SymbolRegular.ChevronDown24;
    }
}
