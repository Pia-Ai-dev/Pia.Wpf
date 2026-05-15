using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Pia.Models;

namespace Pia.Converters;

public enum MemoryTypeBrushKind
{
    Background,
    Foreground
}

public class MemoryTypeToBrushConverter : IValueConverter
{
    public MemoryTypeBrushKind Kind { get; set; } = MemoryTypeBrushKind.Background;

    public object? Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not MemoryType type) return DependencyProperty.UnsetValue;

        var suffix = Kind == MemoryTypeBrushKind.Background ? "BgBrush" : "FgBrush";
        var key = $"Type{type}{suffix}";

        if (Application.Current?.TryFindResource(key) is Brush brush)
            return brush;

        var fallback = Kind == MemoryTypeBrushKind.Background ? "SurfaceMutedBrush" : "TextMutedBrush";
        return Application.Current?.TryFindResource(fallback) as Brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
