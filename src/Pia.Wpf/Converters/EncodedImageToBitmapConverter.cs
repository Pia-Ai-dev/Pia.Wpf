using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace Pia.Converters;

public class EncodedImageToBitmapConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not byte[] { Length: > 0 } bytes)
            return null;

        try
        {
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return At96Dpi(bitmap);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or IOException)
        {
            return null;
        }
    }

    // The viewer's zoom is in pixels, so a non-96 DPI image must not render at a DPI-scaled natural size.
    private static BitmapSource At96Dpi(BitmapSource bitmap)
    {
        if (Math.Abs(bitmap.DpiX - 96) < 0.01 && Math.Abs(bitmap.DpiY - 96) < 0.01)
            return bitmap;

        var stride = (bitmap.PixelWidth * bitmap.Format.BitsPerPixel + 7) / 8;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        var normalized = BitmapSource.Create(bitmap.PixelWidth, bitmap.PixelHeight, 96, 96,
            bitmap.Format, bitmap.Palette, pixels, stride);
        normalized.Freeze();
        return normalized;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
