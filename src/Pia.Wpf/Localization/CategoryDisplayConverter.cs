using System.Globalization;
using System.Windows.Data;

namespace Pia.Localization;

/// <summary>
/// Converts a category key string (e.g. "Person") to its localized display name
/// using the ViewStrings resource (e.g. "Settings_Privacy_Category_Person").
/// </summary>
public class CategoryDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string category)
            return LocalizationSource.Instance[$"Settings_Privacy_Category_{category}"];

        return value;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
