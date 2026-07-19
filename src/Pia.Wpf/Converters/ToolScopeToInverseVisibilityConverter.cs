using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Pia.Models;

namespace Pia.Converters;

/// <summary>
/// Shows the "Agent mode needs a persona with tools" hint only when the active persona's scope is
/// <see cref="PersonaToolScope.None"/> (Visible); otherwise collapses it. The inverse of
/// <see cref="ToolScopeToBoolConverter"/>, expressed as a visibility for the disabled-lever tooltip.
/// </summary>
public sealed class ToolScopeToInverseVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is PersonaToolScope.None ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
