using System.Globalization;
using System.Windows.Data;

namespace Pia.Converters;

public class MultiplyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double v && parameter is string p &&
            double.TryParse(p, NumberStyles.Any, CultureInfo.InvariantCulture, out var multiplier))
            return v * multiplier;
        return value;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
