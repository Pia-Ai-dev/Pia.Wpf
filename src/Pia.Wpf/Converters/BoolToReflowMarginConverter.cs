using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Pia.Converters;

/// <summary>
/// When the Flow rail is pinned (true), reserves the rail's width as a right margin so the docked rail
/// reflows the main content narrower instead of overlaying it (design §4). Must match FlowView's rail Width.
/// </summary>
public class BoolToReflowMarginConverter : IValueConverter
{
    private const double RailWidth = 340;

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? new Thickness(0, 0, RailWidth, 0) : new Thickness(0);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
