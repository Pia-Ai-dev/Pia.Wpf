using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Pia.Views;

public static class ComboBoxBehaviors
{
    public static ICommand GetDropDownOpenedCommand(DependencyObject obj) => (ICommand)obj.GetValue(DropDownOpenedCommandProperty);
    public static void SetDropDownOpenedCommand(DependencyObject obj, ICommand value) => obj.SetValue(DropDownOpenedCommandProperty, value);

    public static readonly DependencyProperty DropDownOpenedCommandProperty =
        DependencyProperty.RegisterAttached("DropDownOpenedCommand", typeof(ICommand), typeof(ComboBoxBehaviors),
            new PropertyMetadata(OnDropDownOpenedCommandChanged));

    private static void OnDropDownOpenedCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ComboBox comboBox && e.NewValue is ICommand command)
        {
            comboBox.DropDownOpened += (s, args) => command?.Execute(null);
        }
    }

    public static bool GetKeepSelection(DependencyObject obj) => (bool)obj.GetValue(KeepSelectionProperty);
    public static void SetKeepSelection(DependencyObject obj, bool value) => obj.SetValue(KeepSelectionProperty, value);

    public static readonly DependencyProperty KeepSelectionProperty =
        DependencyProperty.RegisterAttached("KeepSelection", typeof(bool), typeof(ComboBoxBehaviors),
            new PropertyMetadata(false, OnKeepSelectionChanged));

    private static void OnKeepSelectionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ComboBox comboBox && e.NewValue is bool keepSelection && keepSelection)
        {
            comboBox.DropDownClosed += (s, args) =>
            {
                if (comboBox.SelectedItem is null && comboBox.Items.Count > 0)
                {
                    comboBox.SelectedItem = comboBox.Items[0];
                }
            };
        }
    }
}