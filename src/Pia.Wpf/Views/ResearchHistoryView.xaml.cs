using System.Windows.Controls;

namespace Pia.Views;

public partial class ResearchHistoryView : UserControl
{
    public ResearchHistoryView()
    {
        InitializeComponent();
    }

    private void OnEntriesSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox listBox && listBox.SelectedItem is { } selected)
        {
            listBox.ScrollIntoView(selected);
        }
    }
}
