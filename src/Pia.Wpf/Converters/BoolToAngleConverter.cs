using System.Globalization;
using System.Windows.Data;

namespace Pia.Converters;

public class BoolToAngleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var spec = (parameter as string) ?? "0|-90";
        var parts = spec.Split('|');
        var open = parts.Length > 0 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a) ? a : 0d;
        var closed = parts.Length > 1 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b) ? b : -90d;
        return value is bool flag && flag ? open : closed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
