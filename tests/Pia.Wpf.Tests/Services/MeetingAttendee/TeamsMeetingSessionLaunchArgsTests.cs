using System.IO;
using System.Text.RegularExpressions;
using Pia.Services.MeetingAttendee;
using Xunit;

namespace Pia.Tests.Services.MeetingAttendee;

/// <summary>
/// Guards the one <c>--disable-features</c> switch Pia passes. Chromium honours only the last
/// occurrence, so a switch of our own silently replaces Playwright's list unless it re-states it.
/// </summary>
public class TeamsMeetingSessionLaunchArgsTests
{
    private static readonly string DriverBundlePath = Path.Combine(
        AppContext.BaseDirectory, ".playwright", "package", "lib", "coreBundle.js");

    private const string LayoutChangedHint =
        "The Playwright driver layout changed. Re-derive TeamsMeetingSession.PlaywrightDisabledFeatures "
        + "by hand from the driver's disabledFeatures list and update this parser.";

    private static BrowserLaunchSpec Spec(bool showWindow = false)
    {
        const string exe = @"C:\cache\chromium-1\chrome-win64\chrome.exe";
        return new BrowserLaunchSpec(exe, null, "chrome", exe, showWindow);
    }

    private static string[] DriverDisabledFeatures()
    {
        Assert.True(File.Exists(DriverBundlePath), $"{LayoutChangedHint} Not found: {DriverBundlePath}");

        var literals = Regex.Matches(
            File.ReadAllText(DriverBundlePath),
            @"disabledFeatures\s*=\s*\[(.*?)\]\.filter\(Boolean\)",
            RegexOptions.Singleline);
        Assert.True(literals.Count == 1, $"{LayoutChangedHint} Found {literals.Count} disabledFeatures literals.");

        return [.. Regex.Matches(literals[0].Groups[1].Value, "\"([^\"]+)\"").Select(m => m.Groups[1].Value)];
    }

    private static string DisableFeaturesValue(bool showWindow = false)
    {
        var matches = TeamsMeetingSession.BuildLaunchArgs(Spec(showWindow))
            .Where(a => a.StartsWith("--disable-features=", StringComparison.Ordinal))
            .ToArray();

        Assert.Single(matches);
        return matches[0]["--disable-features=".Length..];
    }

    [Fact]
    public void PlaywrightDisabledFeatures_MatchesTheDriverBundleExactly()
    {
        // Ordered equality, not a superset: when Playwright *removes* a feature a superset assertion
        // still passes and our mirror quietly accumulates names Chromium no longer knows.
        Assert.Equal(DriverDisabledFeatures(), TeamsMeetingSession.PlaywrightDisabledFeatures);
    }

    [Fact]
    public void PiaDisabledFeatures_DoNotOverlapTheMirror()
    {
        Assert.Empty(TeamsMeetingSession.PiaDisabledFeatures
            .Intersect(TeamsMeetingSession.PlaywrightDisabledFeatures, StringComparer.Ordinal));
    }

    [Fact]
    public void LaunchArgs_CarryOneDisableFeaturesAndNoEnableFeatures()
    {
        var args = TeamsMeetingSession.BuildLaunchArgs(Spec());

        Assert.Single(args, a => a.StartsWith("--disable-features=", StringComparison.Ordinal));
        // Playwright passes its own --enable-features; one of ours would erase it the same way.
        Assert.DoesNotContain(args, a => a.StartsWith("--enable-features=", StringComparison.Ordinal));
    }

    [Theory]
    // The two that bind local-network sockets and raise the firewall prompt.
    [InlineData("MediaRouter")]
    [InlineData("DialMediaRouteProvider")]
    [InlineData("WebRtcHideLocalIpsWithMdns")]
    [InlineData("CalculateNativeWinOcclusion")]
    public void DisableFeaturesValue_CarriesTheSocketAndOcclusionFeatures(string feature)
    {
        Assert.Contains(feature, DisableFeaturesValue().Split(','));
    }

    [Fact]
    public void DisableFeaturesValue_IsExactlyTheMirrorPlusOurs()
    {
        Assert.Equal(
            [.. TeamsMeetingSession.PlaywrightDisabledFeatures, .. TeamsMeetingSession.PiaDisabledFeatures],
            DisableFeaturesValue().Split(','));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HiddenWindow_ParksOffScreen(bool showWindow)
    {
        var args = TeamsMeetingSession.BuildLaunchArgs(Spec(showWindow));

        Assert.Equal(!showWindow, args.Contains("--window-position=-32000,-32000"));
        Assert.Equal(!showWindow, args.Contains("--window-size=1280,720"));
    }

    [Fact]
    public void LaunchArgs_KeepTheAudioCriticalSwitches()
    {
        var args = TeamsMeetingSession.BuildLaunchArgs(Spec());

        Assert.Contains("--autoplay-policy=no-user-gesture-required", args);
        // Muting output or faking the playback device would kill the audio we capture.
        Assert.DoesNotContain("--mute-audio", args);
    }
}
