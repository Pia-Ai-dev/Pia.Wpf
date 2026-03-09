using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Pia.Models;

namespace Pia.Converters;

public class VoiceModeStateToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not VoiceModeState currentState || parameter is not string stateList)
            return Visibility.Collapsed;

        var states = stateList.Split(',', StringSplitOptions.TrimEntries);
        return states.Contains(currentState.ToString(), StringComparer.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
