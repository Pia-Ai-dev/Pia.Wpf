using System.Windows;
using System.Windows.Controls;

namespace Pia.Controls.Routines;

public partial class PiaRoutinesSearchBar : UserControl
{
    public static readonly DependencyProperty QueryProperty =
        DependencyProperty.Register(nameof(Query), typeof(string), typeof(PiaRoutinesSearchBar),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public string Query
    {
        get => (string)GetValue(QueryProperty);
        set => SetValue(QueryProperty, value);
    }

    public PiaRoutinesSearchBar() => InitializeComponent();
}
