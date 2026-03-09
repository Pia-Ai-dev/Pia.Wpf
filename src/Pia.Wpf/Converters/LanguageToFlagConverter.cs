using System.Globalization;
using System.Windows.Data;

namespace Pia.Converters;

public class LanguageToFlagConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null)
            return string.Empty;

        return value.ToString() switch
        {
            "EN" => "EN",
            "DE" => "DE",
            "FR" => "FR",
            _ => value
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string strValue)
        {
            if (strValue.Contains("EN"))
                return "EN";
            if (strValue.Contains("DE"))
                return "DE";
            if (strValue.Contains("FR"))
                return "FR";
        }
        return value;
    }
}
