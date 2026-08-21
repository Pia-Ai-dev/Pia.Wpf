using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace Pia.Controls.Shared;

public partial class PiaEmptyState : UserControl
{
    public static readonly DependencyProperty SymbolProperty =
        DependencyProperty.Register(nameof(Symbol), typeof(SymbolRegular), typeof(PiaEmptyState),
            new FrameworkPropertyMetadata(SymbolRegular.Empty));

    public SymbolRegular Symbol
    {
        get => (SymbolRegular)GetValue(SymbolProperty);
        set => SetValue(SymbolProperty, value);
    }

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(PiaEmptyState),
            new FrameworkPropertyMetadata(string.Empty));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Null collapses the second line — do not default it to an empty string.</summary>
    public static readonly DependencyProperty HintProperty =
        DependencyProperty.Register(nameof(Hint), typeof(string), typeof(PiaEmptyState),
            new FrameworkPropertyMetadata(null));

    public string? Hint
    {
        get => (string?)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    /// <summary>Null falls back to the accent brush, so a caller only sets this to deviate.</summary>
    public static readonly DependencyProperty IconBrushProperty =
        DependencyProperty.Register(nameof(IconBrush), typeof(Brush), typeof(PiaEmptyState),
            new FrameworkPropertyMetadata(null));

    public Brush? IconBrush
    {
        get => (Brush?)GetValue(IconBrushProperty);
        set => SetValue(IconBrushProperty, value);
    }

    public static readonly DependencyProperty MaxTextWidthProperty =
        DependencyProperty.Register(nameof(MaxTextWidth), typeof(double), typeof(PiaEmptyState),
            new FrameworkPropertyMetadata(360d));

    public double MaxTextWidth
    {
        get => (double)GetValue(MaxTextWidthProperty);
        set => SetValue(MaxTextWidthProperty, value);
    }

    public PiaEmptyState() => InitializeComponent();
}
