using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Pia.Controls;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;

namespace Pia.Behaviors;

/// <summary>
/// Attached behavior that adds @-command autocomplete to a TextBox.
/// Shows a popup when the user types @ preceded by whitespace or at start of input.
/// </summary>
public static class AtCommandAutocompleteBehavior
{
    private class State
    {
        public AutocompletePopup? Popup;
        public IAutocompleteService? Service;
        public DispatcherTimer? DebounceTimer;
        public string LastFragment = string.Empty;
    }

    private static readonly DependencyProperty StateProperty =
        DependencyProperty.RegisterAttached("__AtCommandState", typeof(State),
            typeof(AtCommandAutocompleteBehavior));

    // IsEnabled attached property
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached("IsEnabled", typeof(bool),
            typeof(AtCommandAutocompleteBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    // PopupControl attached property
    public static readonly DependencyProperty PopupControlProperty =
        DependencyProperty.RegisterAttached("PopupControl", typeof(AutocompletePopup),
            typeof(AtCommandAutocompleteBehavior),
            new PropertyMetadata(null, OnPopupControlChanged));

    public static AutocompletePopup? GetPopupControl(DependencyObject obj) =>
        (AutocompletePopup?)obj.GetValue(PopupControlProperty);
    public static void SetPopupControl(DependencyObject obj, AutocompletePopup? value) =>
        obj.SetValue(PopupControlProperty, value);

    // AutocompleteService attached property
    public static readonly DependencyProperty AutocompleteServiceProperty =
        DependencyProperty.RegisterAttached("AutocompleteService", typeof(IAutocompleteService),
            typeof(AtCommandAutocompleteBehavior),
            new PropertyMetadata(null, OnServiceChanged));

    public static IAutocompleteService? GetAutocompleteService(DependencyObject obj) =>
        (IAutocompleteService?)obj.GetValue(AutocompleteServiceProperty);
    public static void SetAutocompleteService(DependencyObject obj, IAutocompleteService? value) =>
        obj.SetValue(AutocompleteServiceProperty, value);

    private static State GetOrCreateState(DependencyObject obj)
    {
        var state = (State?)obj.GetValue(StateProperty);
        if (state is null)
        {
            state = new State();
            obj.SetValue(StateProperty, state);
        }
        return state;
    }

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox textBox) return;

        if ((bool)e.NewValue)
        {
            textBox.TextChanged += OnTextChanged;
            textBox.PreviewKeyDown += OnPreviewKeyDown;
            textBox.LostFocus += OnLostFocus;
        }
        else
        {
            textBox.TextChanged -= OnTextChanged;
            textBox.PreviewKeyDown -= OnPreviewKeyDown;
            textBox.LostFocus -= OnLostFocus;
        }
    }

    private static void OnPopupControlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox textBox) return;
        var state = GetOrCreateState(textBox);

        // Unwire old
        if (state.Popup is not null)
            state.Popup.SuggestionSelected -= (_, item) => OnSuggestionSelected(textBox, item);

        state.Popup = e.NewValue as AutocompletePopup;

        // Wire new
        if (state.Popup is not null)
        {
            state.Popup.PlacementTarget = textBox;
            state.Popup.SuggestionSelected += (_, item) => OnSuggestionSelected(textBox, item);
        }
    }

    private static void OnServiceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var state = GetOrCreateState(d);
        state.Service = e.NewValue as IAutocompleteService;
    }

    private static void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox textBox) return;
        var state = GetOrCreateState(textBox);

        // Debounce to avoid hammering on every keystroke
        state.DebounceTimer?.Stop();
        state.DebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        state.DebounceTimer.Tick += async (_, _) =>
        {
            state.DebounceTimer.Stop();
            await UpdateAutocompleteAsync(textBox, state);
        };
        state.DebounceTimer.Start();
    }

    private static async Task UpdateAutocompleteAsync(TextBox textBox, State state)
    {
        if (state.Popup is null || state.Service is null)
            return;

        var text = textBox.Text;
        var caretIndex = textBox.CaretIndex;

        if (!AtCommandParser.ShouldShowAutocomplete(text, caretIndex, out var fragment))
        {
            state.Popup.IsOpen = false;
            state.LastFragment = string.Empty;
            return;
        }

        // Avoid re-fetching for the same fragment
        if (fragment == state.LastFragment && state.Popup.IsOpen)
            return;

        state.LastFragment = fragment;

        var (domain, itemFilter) = AtCommandParser.ParseTriggerFragment(fragment);
        var suggestions = await state.Service.GetSuggestionsAsync(domain, itemFilter);

        if (suggestions.Count == 0)
        {
            state.Popup.IsOpen = false;
            return;
        }

        state.Popup.UpdateSuggestions(suggestions);

        // Position popup near the @ character
        var triggerStart = AtCommandParser.GetTriggerStartIndex(text, caretIndex);
        if (triggerStart >= 0)
        {
            var rect = textBox.GetRectFromCharacterIndex(triggerStart);
            state.Popup.HorizontalPopupOffset = rect.X;
        }

        state.Popup.IsOpen = true;
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox) return;
        var state = GetOrCreateState(textBox);

        if (state.Popup is not { IsOpen: true })
            return;

        switch (e.Key)
        {
            case Key.Up:
                state.Popup.MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Down:
                state.Popup.MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Enter when Keyboard.Modifiers == ModifierKeys.None:
            case Key.Tab:
                state.Popup.ConfirmSelection();
                e.Handled = true;
                break;
            case Key.Escape:
                state.Popup.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    private static void OnLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox textBox) return;
        var state = GetOrCreateState(textBox);

        // Delay closing so click on popup item can register
        textBox.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (state.Popup is not null && !state.Popup.IsKeyboardFocusWithin)
                state.Popup.IsOpen = false;
        });
    }

    private static void OnSuggestionSelected(TextBox textBox, AutocompleteSuggestion item)
    {
        var state = GetOrCreateState(textBox);
        if (state.Popup is null) return;

        var text = textBox.Text;
        var caretIndex = textBox.CaretIndex;
        var triggerStart = AtCommandParser.GetTriggerStartIndex(text, caretIndex);

        if (triggerStart < 0) return;

        string replacement;
        if (item.IsTier1)
        {
            // Tier 1: insert "@Domain:" to transition to tier 2
            replacement = $"@{item.DisplayText}:";
        }
        else
        {
            // Tier 2: insert full "@Domain:ItemTitle " with trailing space
            var domainName = item.Domain switch
            {
                AtCommandDomain.Memory => "Memory",
                AtCommandDomain.Todo => "Todo",
                AtCommandDomain.Reminder => "Reminder",
                _ => ""
            };
            var formattedTitle = AtCommandParser.FormatItemTitle(item.DisplayText);
            replacement = $"@{domainName}:{formattedTitle} ";
        }

        // Replace from @ to current caret position
        var before = text[..triggerStart];
        var after = text[caretIndex..];
        textBox.Text = before + replacement + after;
        textBox.CaretIndex = before.Length + replacement.Length;

        if (item.IsTier1)
        {
            // Keep popup open — tier 2 will load via TextChanged
            state.LastFragment = string.Empty; // Force re-fetch
        }
        else
        {
            state.Popup.IsOpen = false;
        }
    }
}
