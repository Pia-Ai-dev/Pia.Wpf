using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Pia.Converters;

public class IntToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var n = value switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            _ => 0
        };
        return n > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
