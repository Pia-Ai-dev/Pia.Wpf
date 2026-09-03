using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Pia.Controls.Chat;

/// <summary>
/// A sent message's text, folded to a few lines with a toggle once it outgrows them — the transcript
/// counterpart of the composer's expander.
/// </summary>
public partial class PiaCollapsibleMessageText : UserControl
{
    private const int CollapsedLines = 5;

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(PiaCollapsibleMessageText),
            new PropertyMetadata(null, OnTextChanged));

    public static readonly DependencyProperty ToggleAutomationIdProperty =
        DependencyProperty.Register(nameof(ToggleAutomationId), typeof(string), typeof(PiaCollapsibleMessageText),
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

    /// <summary>True while the text is taller than the folded box — i.e. the toggle is worth offering.</summary>
    public bool IsOverflowing
    {
        get => (bool)GetValue(IsOverflowingProperty);
        set => SetValue(IsOverflowingProperty, value);
    }

    private bool _expanded;

    /// <summary>Read off the text, not guessed, so restyling the bubble cannot silently move the fold.</summary>
    private double CollapsedHeight =>
        CollapsedLines * (double.IsNaN(Body.LineHeight) ? Body.FontSize * 1.5 : Body.LineHeight);

    public PiaCollapsibleMessageText()
    {
        InitializeComponent();
        ApplyHeight();

        // The fold depends on where the text wraps, so a narrower window can turn a four-line message
        // into a six-line one.
        SizeChanged += (_, _) => RefreshOverflow();
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (PiaCollapsibleMessageText)d;

        // A recycled row can arrive holding a shorter message than the one it was expanded for; the
        // toggle goes with it, or it flickers on the new text until the measurement below lands.
        control._expanded = false;
        control.IsOverflowing = false;
        control.ApplyHeight();
        control.Dispatcher.BeginInvoke(control.RefreshOverflow, DispatcherPriority.Loaded);
    }

    private void RefreshOverflow() =>
        // Half a line of slack: a message that fills the fold exactly is not overflowing.
        IsOverflowing = Body.DesiredSize.Height > CollapsedHeight + 0.5;

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        _expanded = !_expanded;
        ApplyHeight();
    }

    private void ApplyHeight()
    {
        TextClip.MaxHeight = _expanded ? double.PositiveInfinity : CollapsedHeight;
        MoreLabel.Visibility = _expanded ? Visibility.Collapsed : Visibility.Visible;
        LessLabel.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
        ToggleIcon.Symbol = _expanded
            ? Wpf.Ui.Controls.SymbolRegular.ChevronUp24
            : Wpf.Ui.Controls.SymbolRegular.ChevronDown24;
    }
}
