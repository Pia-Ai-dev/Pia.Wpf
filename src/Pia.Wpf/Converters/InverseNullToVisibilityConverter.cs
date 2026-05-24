using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Pia.Converters;

public class InverseNullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            null => Visibility.Visible,
            string s => string.IsNullOrEmpty(s) ? Visibility.Visible : Visibility.Collapsed,
            _ => Visibility.Collapsed,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
