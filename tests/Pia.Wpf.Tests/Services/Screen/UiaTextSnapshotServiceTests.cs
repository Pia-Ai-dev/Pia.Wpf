using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services.Screen;
using Xunit;

namespace Pia.Tests.Services.Screen;

/// <summary>The deterministic half. A real window round-trip needs a pumping message loop and a desktop, so it
/// lives in the probe as its positive control.</summary>
public class UiaTextSnapshotServiceTests
{
    private static UiaTextSnapshotService Service() =>
        new(NullLogger<UiaTextSnapshotService>.Instance);

    [Fact]
    public async Task Snapshot_OfZeroHandle_IsWindowGone_AndNeverThrows()
    {
        var snapshot = await Service().SnapshotAsync(0, TestContext.Current.CancellationToken);

        Assert.Equal(ScreenTextSnapshotOutcome.WindowGone, snapshot.Outcome);
        Assert.False(snapshot.IsUsable);
        Assert.Equal(string.Empty, snapshot.Text);
        Assert.Equal(string.Empty, snapshot.ContentHash);
    }

    [Fact]
    public async Task Snapshot_OfAStaleHandle_NeverThrows()
    {
        var snapshot = await Service().SnapshotAsync(0x7FFF_FFF1, TestContext.Current.CancellationToken);

        // WindowGone on a desktop session, Error on a session where UIA itself refuses; never a throw and
        // never a usable answer, which is what a caller has to be able to rely on.
        Assert.False(snapshot.IsUsable);
        Assert.Contains(snapshot.Outcome, new[]
        {
            ScreenTextSnapshotOutcome.WindowGone,
            ScreenTextSnapshotOutcome.NoAutomationTree,
            ScreenTextSnapshotOutcome.Error,
            ScreenTextSnapshotOutcome.Timeout,
        });
    }

    [Fact]
    public void Constants_ArePinned()
    {
        Assert.Equal(4000, UiaTextSnapshotService.MaxElements);
        Assert.Equal(8000, UiaTextSnapshotService.MaxChars);
        Assert.Equal(TimeSpan.FromSeconds(5), UiaTextSnapshotService.Watchdog);
    }

    /// <summary>Never usable and never a throw, whatever the failure was.</summary>
    [Theory]
    [InlineData(ScreenTextSnapshotOutcome.WindowGone)]
    [InlineData(ScreenTextSnapshotOutcome.NoAutomationTree)]
    [InlineData(ScreenTextSnapshotOutcome.NoText)]
    [InlineData(ScreenTextSnapshotOutcome.Timeout)]
    [InlineData(ScreenTextSnapshotOutcome.Error)]
    public void Failed_IsNeverUsable(ScreenTextSnapshotOutcome outcome)
    {
        var snapshot = ScreenTextSnapshot.Failed(1, outcome, TimeSpan.FromMilliseconds(7), elementsVisited: 3);

        Assert.False(snapshot.IsUsable);
        Assert.Equal(3, snapshot.ElementsVisited);
        Assert.Equal(0, snapshot.TextNodes);
    }

    /// <summary>The window's own text must never reach a log line through a record's synthesised ToString.</summary>
    [Fact]
    public void ToString_CarriesCountsButNotTheText()
    {
        var snapshot = new ScreenTextSnapshot(
            1, ScreenTextSnapshotOutcome.Ok, "SECRET payroll figures", 12, 3, false,
            TimeSpan.FromMilliseconds(40), "0123456789abcdef");

        var rendered = snapshot.ToString();

        Assert.DoesNotContain("payroll", rendered);
        Assert.Contains("Chars = 22", rendered);
        Assert.Contains("0123456789abcdef", rendered);
    }
}
