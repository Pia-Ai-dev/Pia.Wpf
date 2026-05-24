using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Pia.Converters;

public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            null => Visibility.Collapsed,
            string s => string.IsNullOrEmpty(s) ? Visibility.Collapsed : Visibility.Visible,
            _ => Visibility.Visible,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
