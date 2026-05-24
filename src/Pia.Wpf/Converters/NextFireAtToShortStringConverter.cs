using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Pia.Converters;

public class NextFireAtToShortStringConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not DateTime fireAt) return DependencyProperty.UnsetValue;

        var now = DateTime.Now;
        var today = now.Date;
        var delta = fireAt - now;

        if (fireAt < now)
        {
            var overdue = now - fireAt;
            if (overdue.TotalMinutes < 60)
                return $"-{(int)overdue.TotalMinutes}m";
            if (overdue.TotalHours < 24)
                return $"-{(int)overdue.TotalHours}h";
            return fireAt.ToString("MM-dd HH:mm", culture);
        }

        if (delta.TotalMinutes < 60)
            return $"in {Math.Max(1, (int)delta.TotalMinutes)}m";

        if (fireAt.Date == today)
            return fireAt.ToString("HH:mm", culture);

        if (fireAt.Date == today.AddDays(1))
            return "tom " + fireAt.ToString("HH:mm", culture);

        if (fireAt.Date <= today.AddDays(6))
            return fireAt.ToString("ddd HH:mm", culture);

        if (fireAt.Year == now.Year)
            return fireAt.ToString("MM-dd", culture);

        return fireAt.ToString("yyyy-MM-dd", culture);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
