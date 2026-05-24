using System.Globalization;
using System.Windows.Data;
using Pia.Models;

namespace Pia.Converters;

public class MemoryTypeToLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is MemoryType type
            ? type.ToString().ToUpperInvariant()
            : string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
