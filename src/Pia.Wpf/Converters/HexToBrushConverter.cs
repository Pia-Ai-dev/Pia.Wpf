using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Pia.Converters;

/// <summary>
/// Converts a <c>#RRGGBB</c> / <c>#AARRGGBB</c> hex string to a <see cref="SolidColorBrush"/> for the
/// accent-colour preview swatch in the persona dialog. Returns <see cref="Brushes.Transparent"/> for
/// null, blank, or unparseable input.
/// </summary>
public class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string s && !string.IsNullOrWhiteSpace(s))
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(s.Trim());
                return new SolidColorBrush(color);
            }
            catch (FormatException)
            {
                // Partially-typed hex (e.g. "#7C") — fall through to transparent.
            }
        }

        return Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
