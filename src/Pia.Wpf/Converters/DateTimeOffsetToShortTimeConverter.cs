using System.Globalization;
using System.Windows.Data;

namespace Pia.Converters;

/// <summary>
/// Formats a <see cref="DateTimeOffset"/> as a local <c>HH:mm:ss</c> time string for
/// display next to live transcription bubbles.
/// </summary>
public sealed class DateTimeOffsetToShortTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is DateTimeOffset dto)
            return dto.LocalDateTime.ToString("HH:mm:ss", culture);
        if (value is DateTime dt)
            return dt.ToString("HH:mm:ss", culture);
        return string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
