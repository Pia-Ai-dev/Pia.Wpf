using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Pia.Localization;
using Pia.Models;

namespace Pia.Converters;

public class ReminderStatusToLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not ReminderStatus status) return DependencyProperty.UnsetValue;

        var key = status switch
        {
            ReminderStatus.Active    => "Reminders_Filter_Active",
            ReminderStatus.Snoozed   => "Reminders_Filter_Snoozed",
            ReminderStatus.Disabled  => "Reminders_Filter_Disabled",
            ReminderStatus.Completed => "Reminders_Filter_Completed",
            _ => string.Empty,
        };

        return string.IsNullOrEmpty(key) ? string.Empty : LocalizationSource.Instance[key];
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
