using System.Globalization;
using System.Windows.Data;

namespace Pia.Converters;

/// <summary>
/// Multiplies a segment's fraction (<c>[0,1]</c>) by the bar host's <c>ActualWidth</c> to produce a
/// pixel width for the Vault Overview's proportional bar. <paramref name="values"/>[0] = fraction,
/// [1] = host width. Guards the first-measure case (width 0/NaN) so a segment never renders a negative
/// or NaN width; the binding re-fires on size change once layout completes.
/// </summary>
public class FractionToWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not double fraction || values[1] is not double width)
            return 0d;

        var result = fraction * width;
        if (double.IsNaN(result) || double.IsInfinity(result) || result < 0)
            return 0d;

        return result;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
