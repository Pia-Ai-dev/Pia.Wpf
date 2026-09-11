using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Pia.Helpers;

namespace Pia.Views.Dialogs;

public partial class ScreenCapturePickerView : UserControl
{
    public ScreenCapturePickerView()
    {
        InitializeComponent();
        WindowList.ItemContainerGenerator.StatusChanged += OnWindowContainersChanged;
    }

    private void OnWindowContainersChanged(object? sender, EventArgs e)
    {
        if (WindowList.ItemContainerGenerator.Status != GeneratorStatus.ContainersGenerated) return;

        // The dialog takes focus itself as it opens, so the card can only claim it afterwards.
        Dispatcher.BeginInvoke(FocusSelectedCard, DispatcherPriority.Background);
    }

    private void FocusSelectedCard()
    {
        foreach (var card in Cards())
        {
            if (card.IsChecked == true)
            {
                card.Focus();
                return;
            }
        }
    }

    // The surrounding ScrollViewer claims the arrow keys and scrolls with them, so moving the selection
    // has to be done here or the list is only reachable with the mouse.
    private void Picker_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var step = e.Key switch
        {
            Key.Right or Key.Down => 1,
            Key.Left or Key.Up => -1,
            _ => 0,
        };
        if (step == 0) return;

        var cards = Cards().ToList();
        var current = cards.FindIndex(c => c.IsKeyboardFocusWithin);
        if (current < 0) current = cards.FindIndex(c => c.IsChecked == true);
        if (current < 0) return;

        e.Handled = true;
        var next = current + step;
        if (next < 0 || next >= cards.Count) return;

        cards[next].BringIntoView();
        cards[next].Focus();
    }

    // Minimized targets are disabled, and a card that cannot be captured is not worth stopping on.
    private IEnumerable<RadioButton> Cards()
    {
        foreach (var list in new[] { MonitorList, WindowList })
        {
            for (var i = 0; i < list.Items.Count; i++)
            {
                if (list.ItemContainerGenerator.ContainerFromIndex(i) is not DependencyObject container)
                    continue;
                if (container.FindChild<RadioButton>("TargetCard") is { IsEnabled: true } card)
                    yield return card;
            }
        }
    }

    // Arrow keys only move focus; the selection has to follow it for the list to be navigable.
    private void TargetCard_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is RadioButton { IsEnabled: true } card) card.IsChecked = true;
    }
}
