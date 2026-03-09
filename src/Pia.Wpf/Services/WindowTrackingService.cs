using System.Runtime.InteropServices;
using System.Text;
using Pia.Services.Interfaces;

namespace Pia.Services;

public unsafe partial class WindowTrackingService : IWindowTrackingService
{
    private IntPtr _previousWindowHandle = IntPtr.Zero;
    private string? _trackedWindowTitle;
    private readonly object _lock = new();

    public bool HasTrackedWindow => _previousWindowHandle != IntPtr.Zero;

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out POINT lpPoint);

    [LibraryImport("user32.dll")]
    private static partial IntPtr WindowFromPoint(POINT Point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern int GetWindowText(IntPtr hWnd, char* lpString, int nMaxCount);

    private struct POINT
    {
        public int X;
        public int Y;
    }

    public void TrackForegroundWindow()
    {
        lock (_lock)
        {
            var currentWindow = GetForegroundWindow();
            if (currentWindow != IntPtr.Zero)
            {
                _previousWindowHandle = currentWindow;
                _trackedWindowTitle = GetWindowTitle(currentWindow);
            }
        }
    }

    public void TrackWindowAtCursor()
    {
        lock (_lock)
        {
            if (GetCursorPos(out var point))
            {
                var windowHandle = WindowFromPoint(point);

                if (windowHandle != IntPtr.Zero && IsWindow(windowHandle))
                {
                    _previousWindowHandle = windowHandle;
                    _trackedWindowTitle = GetWindowTitle(windowHandle);
                    return;
                }
            }

            TrackForegroundWindow();
        }
    }

    public string? GetTrackedWindowTitle()
    {
        lock (_lock)
        {
            return _trackedWindowTitle;
        }
    }

    public void ClearTracking()
    {
        lock (_lock)
        {
            _previousWindowHandle = IntPtr.Zero;
            _trackedWindowTitle = null;
        }
    }

    public bool RestorePreviousWindow()
    {
        if (_previousWindowHandle == IntPtr.Zero)
            return false;

        if (!IsWindow(_previousWindowHandle))
        {
            _previousWindowHandle = IntPtr.Zero;
            _trackedWindowTitle = null;
            return false;
        }

        var result = SetForegroundWindow(_previousWindowHandle);
        return result;
    }

    private string? GetWindowTitle(IntPtr hWnd)
    {
        const int nChars = 256;
        var buffer = new char[nChars + 1];

        fixed (char* lpString = buffer)
        {
            var length = GetWindowText(hWnd, lpString, nChars);
            return length > 0 ? new string(buffer, 0, length) : null;
        }
    }
}
