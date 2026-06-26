using System.Collections;
using System.Windows;
using System.Windows.Controls;

namespace Pia.Controls.Cards;

/// <summary>
/// A strictly presentational button row. Renders the supplied <see cref="DecisionButton"/> items
/// as WPF-UI buttons; emphasis selects the appearance and each command's CanExecute drives the
/// button's enabled state. Holds no state of its own beyond <see cref="ItemsSource"/>.
/// </summary>
public partial class CardDecisionBar : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(
            nameof(ItemsSource),
            typeof(IEnumerable),
            typeof(CardDecisionBar),
            new PropertyMetadata(null));

    public CardDecisionBar()
    {
        InitializeComponent();
    }

    /// <summary>The <see cref="DecisionButton"/> items to render.</summary>
    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }
}
