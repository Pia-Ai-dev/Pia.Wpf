using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Pia.Services.Interfaces;

namespace Pia.Services;

public unsafe partial class WindowTrackingService : IWindowTrackingService
{
    private readonly ILogger<WindowTrackingService> _logger;
    private IntPtr _previousWindowHandle = IntPtr.Zero;
    private string? _trackedWindowTitle;
    private string? _trackedProcessName;
    private readonly object _lock = new();

    public bool HasTrackedWindow => _previousWindowHandle != IntPtr.Zero;

    public WindowTrackingService(ILogger<WindowTrackingService> logger)
    {
        _logger = logger;
    }

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

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern int GetClassName(IntPtr hWnd, char* lpClassName, int nMaxCount);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private const uint GA_ROOT = 2;

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
                StoreWindow(currentWindow, "foreground");
            }
            else
            {
                _logger.LogWarning("TrackForegroundWindow: GetForegroundWindow returned null");
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
                    // Resolve to top-level window (critical for Electron apps like Teams/Discord)
                    var rootHandle = GetAncestor(windowHandle, GA_ROOT);
                    if (rootHandle != IntPtr.Zero)
                    {
                        if (rootHandle != windowHandle)
                        {
                            var childClass = GetWindowClassName(windowHandle);
                            _logger.LogDebug(
                                "Resolved child window {ChildHandle} (class: {ChildClass}) to root {RootHandle}",
                                windowHandle, childClass, rootHandle);
                        }
                        windowHandle = rootHandle;
                    }

                    if (IsWindow(windowHandle))
                    {
                        StoreWindow(windowHandle, "cursor");
                        return;
                    }
                }

                _logger.LogDebug("No valid window at cursor ({X}, {Y}), falling back to foreground", point.X, point.Y);
            }
            else
            {
                _logger.LogWarning("GetCursorPos failed");
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

    public string? GetTrackedWindowProcessName()
    {
        lock (_lock)
        {
            return _trackedProcessName;
        }
    }

    public void ClearTracking()
    {
        lock (_lock)
        {
            _previousWindowHandle = IntPtr.Zero;
            _trackedWindowTitle = null;
            _trackedProcessName = null;
        }
    }

    public bool RestorePreviousWindow()
    {
        if (_previousWindowHandle == IntPtr.Zero)
        {
            _logger.LogWarning("RestorePreviousWindow: no tracked window");
            return false;
        }

        if (!IsWindow(_previousWindowHandle))
        {
            _logger.LogWarning(
                "RestorePreviousWindow: tracked window {Handle} ('{Title}', process: {Process}) is no longer valid",
                _previousWindowHandle, _trackedWindowTitle, _trackedProcessName);
            _previousWindowHandle = IntPtr.Zero;
            _trackedWindowTitle = null;
            _trackedProcessName = null;
            return false;
        }

        var result = SetForegroundWindow(_previousWindowHandle);
        _logger.LogInformation(
            "RestorePreviousWindow: SetForegroundWindow({Handle}) for '{Title}' (process: {Process}) returned {Result}",
            _previousWindowHandle, _trackedWindowTitle, _trackedProcessName, result);
        return result;
    }

    private void StoreWindow(IntPtr handle, string source)
    {
        _previousWindowHandle = handle;
        _trackedWindowTitle = GetWindowTitle(handle);
        _trackedProcessName = GetProcessName(handle);
        var className = GetWindowClassName(handle);

        _logger.LogInformation(
            "Tracked window via {Source}: handle={Handle}, title='{Title}', process='{Process}', class='{ClassName}'",
            source, handle, _trackedWindowTitle, _trackedProcessName, className);
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

    private string? GetWindowClassName(IntPtr hWnd)
    {
        const int nChars = 256;
        var buffer = new char[nChars + 1];

        fixed (char* lpString = buffer)
        {
            var length = GetClassName(hWnd, lpString, nChars);
            return length > 0 ? new string(buffer, 0, length) : null;
        }
    }

    private static string? GetProcessName(IntPtr hWnd)
    {
        try
        {
            GetWindowThreadProcessId(hWnd, out var processId);
            if (processId == 0) return null;
            var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }
}
