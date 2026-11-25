using System.Runtime.InteropServices;

namespace Pia.Services;

// The single-file host's DllNotFoundException carries no Win32 code; loading again here recovers it.
internal static partial class NativeLoadProbe
{
    private const uint LoadWithAlteredSearchPath = 0x00000008;

    internal static int? GetLoadError(string dllPath)
    {
        var handle = LoadLibraryEx(dllPath, IntPtr.Zero, LoadWithAlteredSearchPath);
        if (handle == IntPtr.Zero)
        {
            return Marshal.GetLastPInvokeError();
        }

        FreeLibrary(handle);
        return null;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr LoadLibraryEx(string lpLibFileName, IntPtr hFile, uint dwFlags);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeLibrary(IntPtr hLibModule);
}
