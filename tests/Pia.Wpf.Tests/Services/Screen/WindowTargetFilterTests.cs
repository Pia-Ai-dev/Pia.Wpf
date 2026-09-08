using Pia.Services.Screen;
using Xunit;

namespace Pia.Tests.Services.Screen;

public class WindowTargetFilterTests
{
    private const uint OwnProcessId = 4242;
    private const uint WsExAppWindow = 0x00040000;

    [Fact]
    public void Classify_AcceptsANormalVisibleTitledWindow() =>
        Assert.Equal(WindowRejection.None, WindowTargetFilter.Classify(Normal(), OwnProcessId));

    [Fact]
    public void Classify_RejectsOwnProcessWindows() =>
        Assert.Equal(
            WindowRejection.OwnProcess,
            WindowTargetFilter.Classify(Normal() with { ProcessId = OwnProcessId }, OwnProcessId));

    [Fact]
    public void Classify_RejectsInvisibleWindows() =>
        Assert.Equal(
            WindowRejection.NotVisible,
            WindowTargetFilter.Classify(Normal() with { IsVisible = false }, OwnProcessId));

    [Fact]
    public void Classify_RejectsToolWindows() =>
        Assert.Equal(
            WindowRejection.ToolWindow,
            WindowTargetFilter.Classify(
                Normal() with { ExStyle = WindowTargetFilter.WS_EX_TOOLWINDOW | WsExAppWindow }, OwnProcessId));

    [Fact]
    public void Classify_RejectsCloakedWindows() =>
        Assert.Equal(
            WindowRejection.Cloaked,
            WindowTargetFilter.Classify(Normal() with { IsCloaked = true }, OwnProcessId));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Classify_RejectsUntitledWindows(string title) =>
        Assert.Equal(
            WindowRejection.Untitled,
            WindowTargetFilter.Classify(Normal() with { Title = title }, OwnProcessId));

    [Fact]
    public void Classify_RejectsEmptyBounds() =>
        Assert.Equal(
            WindowRejection.EmptyBounds,
            WindowTargetFilter.Classify(Normal() with { Bounds = new PixelRect(0, 0, 0, 900) }, OwnProcessId));

    [Fact]
    public void Classify_KeepsMinimizedWindows()
    {
        var minimized = Normal() with { IsMinimized = true, Bounds = new PixelRect(-32000, -32000, 1200, 800) };

        Assert.Equal(WindowRejection.None, WindowTargetFilter.Classify(minimized, OwnProcessId));
    }

    [Fact]
    public void Classify_ReportsTheFirstRuleInOrder()
    {
        Assert.Equal(
            WindowRejection.OwnProcess,
            WindowTargetFilter.Classify(Normal() with { ProcessId = OwnProcessId, IsVisible = false }, OwnProcessId));

        Assert.Equal(
            WindowRejection.NotVisible,
            WindowTargetFilter.Classify(
                Normal() with { IsVisible = false, ExStyle = WindowTargetFilter.WS_EX_TOOLWINDOW }, OwnProcessId));

        Assert.Equal(
            WindowRejection.ToolWindow,
            WindowTargetFilter.Classify(
                Normal() with { ExStyle = WindowTargetFilter.WS_EX_TOOLWINDOW, IsCloaked = true }, OwnProcessId));

        Assert.Equal(
            WindowRejection.Cloaked,
            WindowTargetFilter.Classify(Normal() with { IsCloaked = true, Title = string.Empty }, OwnProcessId));

        Assert.Equal(
            WindowRejection.Untitled,
            WindowTargetFilter.Classify(
                Normal() with { Title = string.Empty, Bounds = new PixelRect(0, 0, 0, 0) }, OwnProcessId));
    }

    [Fact]
    public void IsEligible_IsClassifyEqualsNone()
    {
        Assert.True(WindowTargetFilter.IsEligible(Normal(), OwnProcessId));
        Assert.False(WindowTargetFilter.IsEligible(Normal() with { IsCloaked = true }, OwnProcessId));
    }

    private static WindowDescriptor Normal() => new(
        Hwnd: 0x2222,
        ProcessId: 1000,
        IsVisible: true,
        IsMinimized: false,
        IsCloaked: false,
        ExStyle: WsExAppWindow,
        Title: "Estimate.xlsx - Excel",
        Bounds: new PixelRect(100, 80, 1600, 900));
}
