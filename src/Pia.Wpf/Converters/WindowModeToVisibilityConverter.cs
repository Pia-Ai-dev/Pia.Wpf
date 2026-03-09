using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Pia.Models;

namespace Pia.Converters;

public class WindowModeToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not WindowMode currentMode || parameter is not string modeList)
            return Visibility.Collapsed;

        var modes = modeList.Split(',', StringSplitOptions.TrimEntries);
        return modes.Contains(currentMode.ToString(), StringComparer.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
