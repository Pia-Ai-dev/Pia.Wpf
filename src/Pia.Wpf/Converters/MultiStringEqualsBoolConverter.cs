using System.Globalization;
using System.Windows.Data;

namespace Pia.Converters;

/// <summary>
/// Multi-value converter returning <c>true</c> when the two bound strings are equal
/// (case-insensitive, both non-empty). Used to mark the currently-selected swatch in the
/// persona emoji / accent-colour pickers.
/// </summary>
public class MultiStringEqualsBoolConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not string a || values[1] is not string b)
            return false;
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            return false;
        return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
