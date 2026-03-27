using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Pia.Models;

namespace Pia.Controls;

public partial class AutocompletePopup : UserControl
{
    public ObservableCollection<AutocompleteSuggestion> Suggestions { get; } = [];

    public event EventHandler<AutocompleteSuggestion>? SuggestionSelected;

    public AutocompletePopup()
    {
        InitializeComponent();
        SuggestionList.ItemsSource = Suggestions;
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
        Suggestions.Clear();
        foreach (var item in items)
            Suggestions.Add(item);

        if (Suggestions.Count > 0)
            SuggestionList.SelectedIndex = 0;
    }

    public void MoveSelection(int delta)
    {
        if (Suggestions.Count == 0)
            return;

        int newIndex = SuggestionList.SelectedIndex + delta;
        if (newIndex < 0)
            newIndex = Suggestions.Count - 1;
        else if (newIndex >= Suggestions.Count)
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
