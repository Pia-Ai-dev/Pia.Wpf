using System.Globalization;
using System.Windows.Data;

namespace Pia.Converters;

/// <summary>Shifts a stored UTC <see cref="DateTime"/> to local time for display; WPF
/// <c>StringFormat</c> ignores <see cref="DateTimeKind"/> and would print the UTC clock.</summary>
public class UtcToLocalDateTimeConverter : IValueConverter
{
    // ToLocalTime treats Unspecified as UTC, which matches how the stores persist timestamps.
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is DateTime dt ? dt.ToLocalTime() : value;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
