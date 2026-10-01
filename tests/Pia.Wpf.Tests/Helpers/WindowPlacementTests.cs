using System.Windows;
using Pia.Helpers;
using Xunit;

namespace Pia.Tests.Helpers;

public sealed class WindowPlacementTests
{
    private static readonly Rect Primary = new(0, 0, 1920, 1040);
    private static readonly Rect RightOfPrimary = new(1920, 0, 2560, 1400);
    private static readonly Rect LeftOfPrimary = new(-1920, 0, 1920, 1040);

    [Fact]
    public void IsReachable_WindowInsidePrimary_IsTrue()
    {
        Assert.True(WindowPlacement.IsReachable(new Rect(100, 100, 1000, 700), [Primary]));
    }

    [Fact]
    public void IsReachable_WindowOnDisconnectedMonitor_IsFalse()
    {
        var onSecondMonitor = new Rect(2200, 200, 1000, 700);

        Assert.True(WindowPlacement.IsReachable(onSecondMonitor, [Primary, RightOfPrimary]));
        Assert.False(WindowPlacement.IsReachable(onSecondMonitor, [Primary]));
    }

    [Fact]
    public void IsReachable_MonitorLeftOfPrimary_AcceptsNegativeCoordinates()
    {
        Assert.True(WindowPlacement.IsReachable(new Rect(-1500, 100, 1000, 700), [LeftOfPrimary, Primary]));
    }

    [Fact]
    public void IsReachable_TitleBarAboveWorkArea_IsFalse()
    {
        Assert.False(WindowPlacement.IsReachable(new Rect(100, -200, 1000, 700), [Primary]));
    }

    [Fact]
    public void IsReachable_OnlyASliverOfTitleBarVisible_IsFalse()
    {
        Assert.False(WindowPlacement.IsReachable(new Rect(1900, 100, 1000, 700), [Primary]));
    }

    [Fact]
    public void IsReachable_PartlyOffScreenWithTitleBarGrabbable_IsTrue()
    {
        Assert.True(WindowPlacement.IsReachable(new Rect(1500, 600, 1000, 700), [Primary]));
    }

    [Fact]
    public void IsReachable_DeadZoneOfMixedHeightMonitors_IsFalse()
    {
        var tallLeft = new Rect(0, 0, 1920, 2120);
        var shortRight = new Rect(1920, 0, 1920, 1040);

        Assert.False(WindowPlacement.IsReachable(new Rect(2200, 1500, 800, 400), [tallLeft, shortRight]));
    }

    [Fact]
    public void IsReachable_NoMonitors_IsFalse()
    {
        Assert.False(WindowPlacement.IsReachable(new Rect(100, 100, 1000, 700), []));
    }

    [Fact]
    public void CenterIn_ShrinksAnOversizedWindowToTheWorkArea()
    {
        var placed = WindowPlacement.CenterIn(Primary, new Size(2560, 1400));

        Assert.Equal(Primary, placed);
    }

    [Fact]
    public void CenterIn_CentersAWindowThatFits()
    {
        var placed = WindowPlacement.CenterIn(new Rect(-1920, 0, 1920, 1040), new Size(1000, 700));

        Assert.Equal(new Rect(-1460, 170, 1000, 700), placed);
    }
}
