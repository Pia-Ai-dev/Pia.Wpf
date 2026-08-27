using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Pia.Helpers;

/// <summary>Extracts the application icon out of an installed .exe, for buttons that hand a file to a third-party app.</summary>
internal static partial class AppIcon
{
    /// <summary>
    /// The icon as a frozen <see cref="ImageSource"/> — frozen so a background prewarm may hand it to the
    /// UI thread. Null on any failure, so callers fall back to a generic glyph rather than hiding their
    /// button. Extraction is not cheap and an install does not move while the app runs: callers cache.
    /// </summary>
    public static ImageSource? TryLoad(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return null;

        var large = new IntPtr[1];
        try
        {
            var count = ExtractIconEx(exePath, 0, large, null, 1);
            if (count == 0 || large[0] == IntPtr.Zero) return null;

            var source = Imaging.CreateBitmapSourceFromHIcon(
                large[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (large[0] != IntPtr.Zero) DestroyIcon(large[0]);
        }
    }

    [LibraryImport("shell32.dll", EntryPoint = "ExtractIconExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint ExtractIconEx(
        string lpszFile, int nIconIndex, [Out] IntPtr[]? phiconLarge, [Out] IntPtr[]? phiconSmall, uint nIcons);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr hIcon);
}
