using System.Globalization;
using System.Windows.Data;
using Pia.Localization;

namespace Pia.Converters;

/// <summary>
/// Formats a change-set file count into the localized "N file(s) changed" header label. Reads the
/// template from <see cref="LocalizationSource"/> with a literal key so <c>LocalizationTests</c> can
/// statically verify the key exists.
/// </summary>
public class ChangedFilesLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var count = value is int i ? i : 0;
        var template = LocalizationSource.Instance["ActionCard_ChangeSet_Files"];
        return string.Format(culture, template, count);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
