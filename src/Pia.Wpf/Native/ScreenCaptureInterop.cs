using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Pia.Native;

internal static unsafe partial class ScreenCaptureInterop
{
    public const uint SRCCOPY = 0x00CC0020;
    public const uint PW_RENDERFULLCONTENT = 0x00000002;
    public const uint DIB_RGB_COLORS = 0;
    public const uint BI_RGB = 0;
    public const uint DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public const uint DWMWA_CLOAKED = 14;
    public const int GWL_EXSTYLE = -20;
    public const uint MONITORINFOF_PRIMARY = 1;

    public static readonly nint DpiAwarenessContextPerMonitorAwareV2 = -4;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEXW
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        public fixed char szDevice[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    /// <summary>The three trailing entries are the colour table GDI may still write even at 32 bits per pixel.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColor0;
        public uint bmiColor1;
        public uint bmiColor2;
    }

    [LibraryImport("user32.dll")]
    public static partial nint GetDC(nint hWnd);

    [LibraryImport("user32.dll")]
    public static partial int ReleaseDC(nint hWnd, nint hDC);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateCompatibleBitmap(nint hdc, int cx, int cy);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint ho);

    [LibraryImport("gdi32.dll")]
    public static partial nint SelectObject(nint hdc, nint h);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BitBlt(nint hdc, int x, int y, int cx, int cy, nint hdcSrc, int x1, int y1, uint rop);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    public static partial int GetDIBits(
        nint hdc, nint hbm, uint start, uint cLines, byte* lpvBits, ref BITMAPINFO lpbmi, uint usage);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PrintWindow(nint hwnd, nint hdcBlt, uint nFlags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hWnd, out RECT lpRect);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmGetWindowAttribute(nint hwnd, uint dwAttribute, void* pvAttribute, uint cbAttribute);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmFlush();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumWindows(delegate* unmanaged[Stdcall]<nint, nint, int> lpEnumFunc, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumDisplayMonitors(
        nint hdc, nint lprcClip, delegate* unmanaged[Stdcall]<nint, nint, nint, nint, int> lpfnEnum, nint dwData);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfo(nint hMonitor, ref MONITORINFOEXW lpmi);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    public static partial int GetWindowTextLength(nint hWnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
    public static partial int GetWindowText(nint hWnd, char* lpString, int nMaxCount);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(nint hWnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    public static partial int GetWindowLong(nint hWnd, int nIndex);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowDisplayAffinity(nint hWnd, out uint pdwAffinity);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);

    [LibraryImport("user32.dll")]
    public static partial nint SetThreadDpiAwarenessContext(nint dpiContext);

    [LibraryImport("user32.dll")]
    public static partial nint GetThreadDpiAwarenessContext();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AreDpiAwarenessContextsEqual(nint dpiContextA, nint dpiContextB);

    [LibraryImport("user32.dll")]
    public static partial int GetAwarenessFromDpiAwarenessContext(nint value);

    public static void CollectHandle(nint listHandle, nint value)
    {
        if (GCHandle.FromIntPtr(listHandle).Target is List<nint> handles)
        {
            handles.Add(value);
        }
    }

    /// <summary>An exception escaping an unmanaged callback terminates the process, so both callbacks swallow.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    public static int EnumWindowsCallback(nint hwnd, nint lParam)
    {
        try
        {
            CollectHandle(lParam, hwnd);
        }
        catch (Exception)
        {
        }

        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    public static int EnumMonitorsCallback(nint hMonitor, nint hdc, nint clip, nint data)
    {
        try
        {
            CollectHandle(data, hMonitor);
        }
        catch (Exception)
        {
        }

        return 1;
    }

    /// <summary>Every handle the enumeration callback reaches, collected through a GC handle passed as lParam.</summary>
    public static List<nint> EnumerateHandles(Func<nint, bool> enumerate)
    {
        var handles = new List<nint>();
        var pinned = GCHandle.Alloc(handles);
        try
        {
            enumerate(GCHandle.ToIntPtr(pinned));
        }
        finally
        {
            pinned.Free();
        }

        return handles;
    }

    public static List<nint> EnumerateTopLevelWindows() =>
        EnumerateHandles(static state => EnumWindows(&EnumWindowsCallback, state));

    public static List<nint> EnumerateMonitors() =>
        EnumerateHandles(static state => EnumDisplayMonitors(0, 0, &EnumMonitorsCallback, state));

    public static string ReadDeviceName(MONITORINFOEXW info)
    {
        var raw = new string(info.szDevice, 0, 32);
        var end = raw.IndexOf('\0');
        return end >= 0 ? raw[..end] : raw;
    }
}
