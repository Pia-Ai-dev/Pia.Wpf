using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Pia.Models;

namespace Pia.Converters;

public enum ReminderStatusBrushKind
{
    Background,
    Foreground,
}

public class ReminderStatusToBrushConverter : IValueConverter
{
    public ReminderStatusBrushKind Kind { get; set; } = ReminderStatusBrushKind.Background;

    public object? Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not ReminderStatus status) return DependencyProperty.UnsetValue;

        var (bgKey, fgKey) = status switch
        {
            ReminderStatus.Active    => ("PiaAccentSoftBrush", "PiaAccentBrush"),
            ReminderStatus.Snoozed   => ("WarnSoftBrush",      "WarnBrush"),
            ReminderStatus.Completed => ("SuccessSoftBrush",   "PiaSuccessBrush"),
            ReminderStatus.Disabled  => ("SurfaceMutedBrush",  "TextMutedBrush"),
            _                        => ("SurfaceMutedBrush",  "TextMutedBrush"),
        };

        var key = Kind == ReminderStatusBrushKind.Background ? bgKey : fgKey;
        return Application.Current?.TryFindResource(key) as Brush ?? DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
