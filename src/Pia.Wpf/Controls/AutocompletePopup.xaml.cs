using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Pia.Models;

namespace Pia.Controls;

public partial class AutocompletePopup : UserControl
{
    public event EventHandler<AutocompleteSuggestion>? SuggestionSelected;

    public AutocompletePopup()
    {
        InitializeComponent();
        SuggestionList.PreviewMouseLeftButtonUp += OnItemClicked;
    }

    public bool IsOpen
    {
        get => SuggestionPopup.IsOpen;
        set => SuggestionPopup.IsOpen = value;
    }

    public UIElement? PlacementTarget
    {
        get => SuggestionPopup.PlacementTarget;
        set => SuggestionPopup.PlacementTarget = value;
    }

    public double HorizontalPopupOffset
    {
        get => SuggestionPopup.HorizontalOffset;
        set => SuggestionPopup.HorizontalOffset = value;
    }

    public double VerticalPopupOffset
    {
        get => SuggestionPopup.VerticalOffset;
        set => SuggestionPopup.VerticalOffset = value;
    }

    public int SelectedIndex
    {
        get => SuggestionList.SelectedIndex;
        set => SuggestionList.SelectedIndex = value;
    }

    public AutocompleteSuggestion? SelectedItem =>
        SuggestionList.SelectedItem as AutocompleteSuggestion;

    public void UpdateSuggestions(IReadOnlyList<AutocompleteSuggestion> items)
    {
        // Swap the whole list in one shot rather than clearing + adding item-by-item. The
        // @Files picker can return up to the handler's 500-item hard cap; a per-item
        // ObservableCollection rebuild would fan out ~500 CollectionChanged notifications on
        // every (debounced) keystroke. The ListBox is UI-virtualized, so only the visible
        // containers are realized regardless of list length.
        SuggestionList.ItemsSource = items;

        if (items.Count > 0)
            SuggestionList.SelectedIndex = 0;
    }

    public void MoveSelection(int delta)
    {
        int count = SuggestionList.Items.Count;
        if (count == 0)
            return;

        int newIndex = SuggestionList.SelectedIndex + delta;
        if (newIndex < 0)
            newIndex = count - 1;
        else if (newIndex >= count)
            newIndex = 0;

        SuggestionList.SelectedIndex = newIndex;
        SuggestionList.ScrollIntoView(SuggestionList.SelectedItem);
    }

    public void ConfirmSelection()
    {
        if (SelectedItem is { } item)
            SuggestionSelected?.Invoke(this, item);
    }

    private void OnItemClicked(object sender, MouseButtonEventArgs e)
    {
        if (SuggestionList.SelectedItem is AutocompleteSuggestion item)
            SuggestionSelected?.Invoke(this, item);
    }
}
