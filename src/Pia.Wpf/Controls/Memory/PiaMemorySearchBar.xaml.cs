using System.Windows;
using System.Windows.Controls;

namespace Pia.Controls.Memory;

public partial class PiaMemorySearchBar : UserControl
{
    public static readonly DependencyProperty QueryProperty =
        DependencyProperty.Register(nameof(Query), typeof(string), typeof(PiaMemorySearchBar),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty ActiveFilterProperty =
        DependencyProperty.Register(nameof(ActiveFilter), typeof(string), typeof(PiaMemorySearchBar),
            new FrameworkPropertyMetadata("All", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public string Query
    {
        get => (string)GetValue(QueryProperty);
        set => SetValue(QueryProperty, value);
    }

    public string ActiveFilter
    {
        get => (string)GetValue(ActiveFilterProperty);
        set => SetValue(ActiveFilterProperty, value);
    }

    public PiaMemorySearchBar() => InitializeComponent();
}
