using System.Windows;
using System.Windows.Controls;
using Pia.Models;

namespace Pia.Controls.Vault;

public partial class PiaTypeChip : UserControl
{
    public static readonly DependencyProperty TypeProperty =
        DependencyProperty.Register(nameof(Type), typeof(MemoryType), typeof(PiaTypeChip),
            new PropertyMetadata(MemoryType.Note));

    public MemoryType Type
    {
        get => (MemoryType)GetValue(TypeProperty);
        set => SetValue(TypeProperty, value);
    }

    public PiaTypeChip() => InitializeComponent();
}
