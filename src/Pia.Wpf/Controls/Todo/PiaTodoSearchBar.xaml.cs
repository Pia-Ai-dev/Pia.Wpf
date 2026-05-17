using System.Windows;
using System.Windows.Controls;

namespace Pia.Controls.Todo;

public partial class PiaTodoSearchBar : UserControl
{
    public static readonly DependencyProperty QueryProperty =
        DependencyProperty.Register(nameof(Query), typeof(string), typeof(PiaTodoSearchBar),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public string Query
    {
        get => (string)GetValue(QueryProperty);
        set => SetValue(QueryProperty, value);
    }

    public PiaTodoSearchBar() => InitializeComponent();
}
