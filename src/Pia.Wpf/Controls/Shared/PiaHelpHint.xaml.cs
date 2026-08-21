using System.Windows;
using System.Windows.Controls;

namespace Pia.Controls.Shared;

/// <summary>A hover-only help glyph, so an explanation costs no vertical space next to what it explains.</summary>
public partial class PiaHelpHint : UserControl
{
    /// <summary>Null collapses the glyph — do not default it to an empty string.</summary>
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(PiaHelpHint),
            new FrameworkPropertyMetadata(null));

    public string? Text
    {
        get => (string?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public static readonly DependencyProperty IconSizeProperty =
        DependencyProperty.Register(nameof(IconSize), typeof(double), typeof(PiaHelpHint),
            new FrameworkPropertyMetadata(16d));

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public PiaHelpHint()
    {
        InitializeComponent();
        HintBody.DataContext = this;
    }
}
