using System.Globalization;
using System.Windows.Data;
using Pia.Localization;

namespace Pia.Converters;

/// <summary>
/// Formats the count of transcript messages below the rendered window into the localized "load older"
/// button label. Reads the template from <see cref="LocalizationSource"/> with a literal key so
/// <c>LocalizationTests</c> can statically verify the key exists.
/// </summary>
public class OlderMessagesLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var count = value is int i ? i : 0;
        var template = LocalizationSource.Instance["Assistant_LoadOlderMessages"];
        return string.Format(culture, template, count);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
