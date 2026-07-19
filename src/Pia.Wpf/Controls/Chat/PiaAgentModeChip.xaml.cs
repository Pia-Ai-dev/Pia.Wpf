using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Pia.Controls.Chat;

/// <summary>
/// Renders the typed <see cref="Pia.Models.AgentModeSuggestion"/> chips (R8). Its own DPs + command,
/// routed separately from the string suggestion chips — clicking it switches the user to Agent mode
/// rather than pasting text.
/// </summary>
public partial class PiaAgentModeChip : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(PiaAgentModeChip),
            new PropertyMetadata(null));

    public static readonly DependencyProperty ItemClickCommandProperty =
        DependencyProperty.Register(nameof(ItemClickCommand), typeof(ICommand), typeof(PiaAgentModeChip),
            new PropertyMetadata(null));

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public ICommand? ItemClickCommand
    {
        get => (ICommand?)GetValue(ItemClickCommandProperty);
        set => SetValue(ItemClickCommandProperty, value);
    }

    public PiaAgentModeChip() => InitializeComponent();
}
