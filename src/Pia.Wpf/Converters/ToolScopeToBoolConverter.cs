using System.Globalization;
using System.Windows.Data;
using Pia.Models;

namespace Pia.Converters;

/// <summary>
/// Gates the Chat/Agent lever: a persona with <see cref="PersonaToolScope.None"/> cannot plan,
/// so the lever reads as disabled (returns false); every other scope enables it (true).
/// </summary>
public sealed class ToolScopeToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is PersonaToolScope scope && scope != PersonaToolScope.None;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
