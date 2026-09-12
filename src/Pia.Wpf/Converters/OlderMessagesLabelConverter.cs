using System.Globalization;
using System.Windows.Data;
using Pia.Localization;

namespace Pia.Converters;

/// <summary>The binding site names the resource key for the label; without one it is the Assistant
/// transcript's.</summary>
public class OlderMessagesLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var count = value is int i ? i : 0;
        var template = parameter is string key
            ? LocalizationSource.Instance[key]
            : LocalizationSource.Instance["Assistant_LoadOlderMessages"];
        return string.Format(culture, template, count);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
