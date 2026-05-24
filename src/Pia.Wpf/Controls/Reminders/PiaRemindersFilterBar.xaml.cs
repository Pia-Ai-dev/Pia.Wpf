using System.Windows;
using System.Windows.Controls;

namespace Pia.Controls.Reminders;

public partial class PiaRemindersFilterBar : UserControl
{
    public static readonly DependencyProperty ActiveFilterProperty =
        DependencyProperty.Register(
            nameof(ActiveFilter),
            typeof(string),
            typeof(PiaRemindersFilterBar),
            new FrameworkPropertyMetadata(
                "All",
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public string ActiveFilter
    {
        get => (string)GetValue(ActiveFilterProperty);
        set => SetValue(ActiveFilterProperty, value);
    }

    public PiaRemindersFilterBar()
    {
        InitializeComponent();
    }
}
