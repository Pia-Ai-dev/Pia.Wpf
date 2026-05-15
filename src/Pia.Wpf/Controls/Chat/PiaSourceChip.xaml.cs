using System.Windows;
using System.Windows.Controls;

namespace Pia.Controls.Chat;

public partial class PiaSourceChip : UserControl
{
    public static readonly DependencyProperty NumberProperty =
        DependencyProperty.Register(nameof(Number), typeof(int), typeof(PiaSourceChip),
            new PropertyMetadata(0));

    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.Register(nameof(Source), typeof(string), typeof(PiaSourceChip),
            new PropertyMetadata(null));

    public static readonly DependencyProperty MetaProperty =
        DependencyProperty.Register(nameof(Meta), typeof(string), typeof(PiaSourceChip),
            new PropertyMetadata(null));

    public int Number
    {
        get => (int)GetValue(NumberProperty);
        set => SetValue(NumberProperty, value);
    }

    public string? Source
    {
        get => (string?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public string? Meta
    {
        get => (string?)GetValue(MetaProperty);
        set => SetValue(MetaProperty, value);
    }

    public PiaSourceChip() => InitializeComponent();
}
