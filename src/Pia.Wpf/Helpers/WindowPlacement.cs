using System.Windows;
using Pia.Native;

namespace Pia.Helpers;

public static class WindowPlacement
{
    private const double TitleBarHeight = 32;
    private const double MinGrabbableWidth = 100;

    /// <summary>True when enough of the title bar lies on a work area for the user to drag the window.</summary>
    public static bool IsReachable(Rect window, IEnumerable<Rect> workAreas)
    {
        var titleBar = new Rect(window.Left, window.Top, window.Width, Math.Min(TitleBarHeight, window.Height));
        var minWidth = Math.Min(MinGrabbableWidth, titleBar.Width);
        var minHeight = titleBar.Height / 2;

        return workAreas.Any(area =>
        {
            var overlap = Rect.Intersect(titleBar, area);
            return !overlap.IsEmpty && overlap.Width >= minWidth && overlap.Height >= minHeight;
        });
    }

    public static Rect CenterIn(Rect workArea, Size size)
    {
        var width = Math.Min(size.Width, workArea.Width);
        var height = Math.Min(size.Height, workArea.Height);
        return new Rect(
            workArea.Left + (workArea.Width - width) / 2,
            workArea.Top + (workArea.Height - height) / 2,
            width,
            height);
    }

    /// <summary>In physical pixels, the same space <see cref="ScreenCaptureInterop.GetWindowRect"/> reports.</summary>
    internal static unsafe List<Rect> MonitorWorkAreas()
    {
        var areas = new List<Rect>();
        foreach (var monitor in ScreenCaptureInterop.EnumerateMonitors())
        {
            var info = default(ScreenCaptureInterop.MONITORINFOEXW);
            info.cbSize = (uint)sizeof(ScreenCaptureInterop.MONITORINFOEXW);
            if (ScreenCaptureInterop.GetMonitorInfo(monitor, ref info))
                areas.Add(ToRect(info.rcWork));
        }

        return areas;
    }

    internal static Rect ToRect(ScreenCaptureInterop.RECT rect) =>
        new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
}
