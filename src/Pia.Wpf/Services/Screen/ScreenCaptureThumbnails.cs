using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Pia.Services.Screen;

public static class ScreenCaptureThumbnails
{
    public const int PickerMaxEdge = 240;

    /// <summary>A frozen copy scaled to the long edge; the source itself when it already fits.</summary>
    public static BitmapSource Create(BitmapSource frozen, int maxEdge)
    {
        ArgumentNullException.ThrowIfNull(frozen);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEdge, 1);

        var longest = Math.Max(frozen.PixelWidth, frozen.PixelHeight);
        if (longest <= maxEdge) return frozen;

        var scale = (double)maxEdge / longest;
        var scaled = new TransformedBitmap(frozen, new ScaleTransform(scale, scale));

        // The copy is what lets the full frame be collected: a TransformedBitmap holds its Source for life.
        var copy = new WriteableBitmap(scaled);
        copy.Freeze();
        return copy;
    }
}
