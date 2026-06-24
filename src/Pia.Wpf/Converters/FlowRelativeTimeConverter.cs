using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Pia.Converters;

/// <summary>Formats a Flow item's <c>CreatedAt</c> as a compact relative time ("now", "3m", "2h", "1d").</summary>
public class FlowRelativeTimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var when = value switch
        {
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(dt),
            _ => (DateTimeOffset?)null,
        };
        if (when is null)
            return DependencyProperty.UnsetValue;

        var delta = DateTimeOffset.Now - when.Value;
        if (delta < TimeSpan.Zero)
            delta = TimeSpan.Zero;

        if (delta.TotalSeconds < 45)
            return "now";
        if (delta.TotalMinutes < 60)
            return $"{Math.Max(1, (int)delta.TotalMinutes)}m";
        if (delta.TotalHours < 24)
            return $"{(int)delta.TotalHours}h";
        if (delta.TotalDays < 7)
            return $"{(int)delta.TotalDays}d";
        return when.Value.LocalDateTime.ToString("MM-dd", culture);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
