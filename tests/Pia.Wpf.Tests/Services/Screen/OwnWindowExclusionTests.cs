using Pia.Services.Screen;
using Xunit;

namespace Pia.Tests.Services.Screen;

public class OwnWindowExclusionTests
{
    [Fact]
    public void Needed_KeepsAVisibleWindow()
    {
        var needed = OwnWindowExclusion.Needed([(0x1234, true)]);

        Assert.Equal([(nint)0x1234], needed);
    }

    [Fact]
    public void Needed_DropsAnInvisibleWindow()
    {
        var needed = OwnWindowExclusion.Needed([(0x1234, false)]);

        Assert.Empty(needed);
    }

    /// <summary>The live shape: one visible main window among WPF's hidden HwndWrapper, IME and DDE windows.</summary>
    [Fact]
    public void Needed_KeepsOnlyTheVisibleOnesOutOfAWholeProcess()
    {
        var own = new (nint Hwnd, bool IsVisible)[]
        {
            (0xE00B06, true),
            (0x30B6C, true),
            (0x30B5E, false),
            (0x690A16, false),
            (0x160AE6, false),
            (0x840ABE, false),
            (0x40B32, false),
        };

        var needed = OwnWindowExclusion.Needed(own);

        Assert.Equal([(nint)0xE00B06, (nint)0x30B6C], needed);
    }

    [Fact]
    public void Needed_DropsAZeroHandle()
    {
        var needed = OwnWindowExclusion.Needed([(0, true)]);

        Assert.Empty(needed);
    }

    [Fact]
    public void Needed_OnAnEmptyListIsEmpty()
    {
        Assert.Empty(OwnWindowExclusion.Needed([]));
    }
}
