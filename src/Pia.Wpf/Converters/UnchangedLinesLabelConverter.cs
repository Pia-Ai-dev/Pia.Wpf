using System.Globalization;
using System.Windows.Data;
using Pia.Localization;

namespace Pia.Converters;

/// <summary>
/// Formats a collapsed-hunk line count into the localized "⋯ N unchanged lines" bar label. Reads the
/// template from <see cref="LocalizationSource"/> with a literal key so <c>LocalizationTests</c> can
/// statically verify the key exists.
/// </summary>
public class UnchangedLinesLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var count = value is int i ? i : 0;
        var template = LocalizationSource.Instance["ActionCard_Diff_UnchangedLines"];
        return "⋯ " + string.Format(culture, template, count);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
