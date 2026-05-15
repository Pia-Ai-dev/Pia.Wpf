using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Pia.Controls.Chat;

public partial class PiaSuggestionChips : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(PiaSuggestionChips),
            new PropertyMetadata(null));

    public static readonly DependencyProperty ItemClickCommandProperty =
        DependencyProperty.Register(nameof(ItemClickCommand), typeof(ICommand), typeof(PiaSuggestionChips),
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

    public PiaSuggestionChips() => InitializeComponent();
}
