using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Pia.Services.MeetingAttendee;

/// <summary>
/// Win32 helper that suppresses the Windows taskbar button of the meeting attendee's off-screen browser
/// window, so a hidden ("show window = off") meeting leaves no orphan taskbar button. It switches the
/// window to a tool window (<c>WS_EX_TOOLWINDOW</c>, clearing <c>WS_EX_APPWINDOW</c>), which the taskbar
/// does not show.
///
/// <para>Best-effort by design: the browser HWND exists only after the window is created, so the lookup
/// polls briefly; a miss (a visible taskbar button) is a cosmetic issue, never a join failure. The
/// window-pick rule is extracted as the pure <see cref="PickBrowserWindow"/> so it is unit-testable
/// without a live window.</para>
/// </summary>
internal static class BrowserWindowChrome
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_APPWINDOW = 0x00040000;
    private const int SW_HIDE = 0;
    private const int SW_SHOWNA = 8;

    /// <summary>A snapshot of one top-level window, for the pure pick predicate.</summary>
    internal readonly record struct WindowInfo(IntPtr Handle, uint ProcessId, bool IsVisible, string Title);

    /// <summary>
    /// Finds the top-level browser window owned by <paramref name="rootProcessId"/> and removes its
    /// taskbar button. Polls up to ~3 s (100 ms cadence) for the window to appear after launch. Caller
    /// should only invoke this for a hidden (off-screen) window.
    /// </summary>
    public static async Task SuppressTaskbarButtonAsync(int rootProcessId, ILogger logger, CancellationToken ct = default)
    {
        const int maxAttempts = 30;   // ~3 s total at 100 ms cadence
        const int delayMs = 100;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var hwnd = FindBrowserWindow((uint)rootProcessId);
            if (hwnd != IntPtr.Zero)
            {
                ApplyToolWindowStyle(hwnd);
                logger.LogDebug("Suppressed taskbar button for meeting browser pid {Pid}", rootProcessId);
                return;
            }

            await Task.Delay(delayMs, ct).ConfigureAwait(false);
        }

        logger.LogDebug(
            "No top-level window resolved for meeting browser pid {Pid} within the poll window; taskbar button not suppressed",
            rootProcessId);
    }

    /// <summary>
    /// Pure window-pick rule: the launched browser's main window is the first top-level window that is
    /// owned by the root PID, visible, and titled — which skips the child/GPU/utility windows (no title)
    /// a single Chromium launch also spawns.
    /// </summary>
    internal static IntPtr PickBrowserWindow(IReadOnlyList<WindowInfo> windows, uint rootProcessId)
    {
        foreach (var w in windows)
        {
            if (w.ProcessId == rootProcessId && w.IsVisible && !string.IsNullOrWhiteSpace(w.Title))
                return w.Handle;
        }
        return IntPtr.Zero;
    }

    private static IntPtr FindBrowserWindow(uint rootProcessId)
    {
        var windows = new List<WindowInfo>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == rootProcessId)
                windows.Add(new WindowInfo(hwnd, pid, IsWindowVisible(hwnd), GetWindowTitle(hwnd)));
            return true;   // keep enumerating
        }, IntPtr.Zero);

        return PickBrowserWindow(windows, rootProcessId);
    }

    private static void ApplyToolWindowStyle(IntPtr hwnd)
    {
        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        // Parenthesize the OR before the mask: in C# '&' binds tighter than '|', so without the parens
        // APPWINDOW would never be cleared.
        var updated = (ex | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(updated));

        // Re-assert so the taskbar drops the button. The window is already parked off-screen, so this
        // hide/show-no-activate flicker is invisible.
        ShowWindow(hwnd, SW_HIDE);
        ShowWindow(hwnd, SW_SHOWNA);
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0) return string.Empty;
        var sb = new StringBuilder(length + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);
}
