using Microsoft.Extensions.Logging.Abstractions;
using Pia.Models;
using Pia.Services.MeetingAttendee;
using Xunit;

namespace Pia.Tests.Services.MeetingAttendee;

/// <summary>
/// Tests the default-browser ProgId → <see cref="MeetingBrowserSelection"/> mapping and the
/// graceful-degrade behaviour of <see cref="DefaultBrowserResolver"/>, using an injected registry-read
/// seam so the live registry is never touched.
/// </summary>
public class DefaultBrowserResolverTests
{
    [Theory]
    [InlineData("ChromeHTML", MeetingBrowserSelection.SystemChrome)]
    [InlineData("ChromeHTML.X9Y", MeetingBrowserSelection.SystemChrome)]   // versioned variant
    [InlineData("MSEdgeHTM", MeetingBrowserSelection.SystemEdge)]
    [InlineData("MSEdgeHTM.8WEKYB3D8BBWE", MeetingBrowserSelection.SystemEdge)]
    [InlineData("FirefoxURL-308046B0AF4A39CB", MeetingBrowserSelection.BundledChromium)]
    [InlineData("BraveHTML", MeetingBrowserSelection.BundledChromium)]
    [InlineData("", MeetingBrowserSelection.BundledChromium)]
    [InlineData(null, MeetingBrowserSelection.BundledChromium)]
    public void MapProgIdToSelection_MapsKnownProgIds(string? progId, MeetingBrowserSelection expected)
    {
        Assert.Equal(expected, DefaultBrowserResolver.MapProgIdToSelection(progId));
    }

    [Fact]
    public void ResolveChromiumSelectionOrBundled_UsesInjectedProgId()
    {
        var resolver = new DefaultBrowserResolver(
            NullLogger<DefaultBrowserResolver>.Instance, readHttpsProgId: () => "ChromeHTML");

        Assert.Equal(MeetingBrowserSelection.SystemChrome, resolver.ResolveChromiumSelectionOrBundled());
    }

    [Fact]
    public void ResolveChromiumSelectionOrBundled_WhenRegistryReadThrows_FallsBackToBundled()
    {
        var resolver = new DefaultBrowserResolver(
            NullLogger<DefaultBrowserResolver>.Instance,
            readHttpsProgId: () => throw new InvalidOperationException("registry unavailable"));

        Assert.Equal(MeetingBrowserSelection.BundledChromium, resolver.ResolveChromiumSelectionOrBundled());
    }
}
