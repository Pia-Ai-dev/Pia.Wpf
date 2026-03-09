using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Pia.Converters;

public class IdEqualsVisibilityConverter : IValueConverter, IMultiValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null || parameter is null)
            return Visibility.Collapsed;

        var valueStr = value.ToString();
        var paramStr = parameter.ToString();

        return valueStr == paramStr ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var invert = parameter as string == "Invert";

        if (values is null || values.Length < 2
            || values[0] is null || values[0] == DependencyProperty.UnsetValue
            || values[1] is null || values[1] == DependencyProperty.UnsetValue)
            return invert ? Visibility.Visible : Visibility.Collapsed;

        var match = values[0].ToString() == values[1].ToString();
        if (invert) match = !match;

        return match ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
