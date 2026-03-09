using System.Globalization;
using System.Windows.Data;

namespace Pia.Converters;

public class StringEqualsBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string current || parameter is not string target)
            return false;

        return string.Equals(current, target, StringComparison.Ordinal);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
