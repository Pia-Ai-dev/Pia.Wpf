using Pia.Services.MeetingAttendee;
using Xunit;
using static Pia.Services.MeetingAttendee.BrowserWindowChrome;

namespace Pia.Tests.Services.MeetingAttendee;

/// <summary>
/// Unit tests for the pure window-pick rule extracted from <see cref="BrowserWindowChrome"/>. The
/// P/Invoke itself drives a live window and is covered by the Phase-2 manual smoke (join a meeting,
/// confirm no taskbar button), not an automated test.
/// </summary>
public class BrowserWindowChromeTests
{
    private static readonly IntPtr Main = new(100);

    [Fact]
    public void PickBrowserWindow_PicksVisibleTitledWindowOwnedByRootPid()
    {
        var windows = new List<WindowInfo>
        {
            // child/GPU/utility windows of the same PID: no title → skipped.
            new(new IntPtr(1), 42, IsVisible: true, Title: ""),
            new(new IntPtr(2), 42, IsVisible: false, Title: "hidden"),
            new(Main, 42, IsVisible: true, Title: "Teams meeting — Chromium"),
        };

        Assert.Equal(Main, PickBrowserWindow(windows, rootProcessId: 42));
    }

    [Fact]
    public void PickBrowserWindow_IgnoresWindowsOfOtherProcesses()
    {
        var windows = new List<WindowInfo>
        {
            new(new IntPtr(9), 999, IsVisible: true, Title: "Some other app"),
        };

        Assert.Equal(IntPtr.Zero, PickBrowserWindow(windows, rootProcessId: 42));
    }

    [Fact]
    public void PickBrowserWindow_ReturnsZeroWhenNoTitledVisibleWindow()
    {
        var windows = new List<WindowInfo>
        {
            new(new IntPtr(1), 42, IsVisible: true, Title: "   "),   // whitespace title
            new(new IntPtr(2), 42, IsVisible: false, Title: "off"),  // not visible
        };

        Assert.Equal(IntPtr.Zero, PickBrowserWindow(windows, rootProcessId: 42));
    }
}
