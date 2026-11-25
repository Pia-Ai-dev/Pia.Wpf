using System.Windows;
using System.Windows.Input;

namespace Pia.Behaviors;

/// <summary>Passes focus to the next control when the focused one disables itself, e.g. while its command runs.</summary>
public static class FocusHandoffBehavior
{
    public static readonly DependencyProperty MoveFocusWhenDisabledProperty =
        DependencyProperty.RegisterAttached("MoveFocusWhenDisabled", typeof(bool), typeof(FocusHandoffBehavior),
            new PropertyMetadata(false, OnMoveFocusWhenDisabledChanged));

    public static bool GetMoveFocusWhenDisabled(DependencyObject obj) => (bool)obj.GetValue(MoveFocusWhenDisabledProperty);

    public static void SetMoveFocusWhenDisabled(DependencyObject obj, bool value) => obj.SetValue(MoveFocusWhenDisabledProperty, value);

    private static void OnMoveFocusWhenDisabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
            return;

        element.IsEnabledChanged -= OnIsEnabledChanged;
        if (e.NewValue is true)
            element.IsEnabledChanged += OnIsEnabledChanged;
    }

    // Left alone, WPF hands focus to a focusable ancestor, which frames a whole dialog or page.
    private static void OnIsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false && sender is UIElement { IsKeyboardFocused: true } element)
            element.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
    }
}
