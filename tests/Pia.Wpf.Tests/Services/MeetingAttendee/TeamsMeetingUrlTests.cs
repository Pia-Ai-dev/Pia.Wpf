using System;
using System.Web;
using Pia.Services.MeetingAttendee;
using Xunit;

namespace Pia.Tests.Services.MeetingAttendee;

/// <summary>
/// Exercises the pure launcher-URL transform only. The redirect-follow (network) lives in
/// <see cref="TeamsMeetingSession"/> and is deliberately NOT exercised here.
/// </summary>
public sealed class TeamsMeetingUrlTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildLauncherUrl_Throws_ForNullOrWhitespace(string? input)
    {
        Assert.ThrowsAny<ArgumentException>(() => TeamsMeetingUrl.BuildLauncherUrl(input!));
    }

    [Fact]
    public void BuildLauncherUrl_SetsAllLaunchParams()
    {
        var result = TeamsMeetingUrl.BuildLauncherUrl(
            "https://teams.microsoft.com/dl/launcher/launcher.html?url=%2F_%23%2Fl%2Fmeetup-join%2F19");

        var query = HttpUtility.ParseQueryString(new Uri(result).Query);

        Assert.Equal("false", query["msLaunch"]);
        Assert.Equal("meetup-join", query["type"]);
        Assert.Equal("true", query["directDl"]);
        Assert.Equal("true", query["suppressPrompt"]);
    }

    [Fact]
    public void BuildLauncherUrl_OverwritesExistingMsLaunchTrue()
    {
        var result = TeamsMeetingUrl.BuildLauncherUrl(
            "https://teams.microsoft.com/dl/launcher/launcher.html?msLaunch=true&anchor=conversations");

        var query = HttpUtility.ParseQueryString(new Uri(result).Query);

        Assert.Equal("false", query["msLaunch"]);
        // msLaunch must not appear twice (overwrite, not append).
        Assert.DoesNotContain("msLaunch=true", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildLauncherUrl_PreservesUnrelatedQueryParams()
    {
        // The meeting context lives in these params; the transform must not drop them.
        var result = TeamsMeetingUrl.BuildLauncherUrl(
            "https://teams.microsoft.com/l/meetup-join/19%3ameeting_abc?context=%7B%22Tid%22%3A%22t1%22%7D&tenantId=contoso");

        var query = HttpUtility.ParseQueryString(new Uri(result).Query);

        Assert.Equal("{\"Tid\":\"t1\"}", query["context"]);
        Assert.Equal("contoso", query["tenantId"]);
        // ...and the launch params are still applied alongside them.
        Assert.Equal("false", query["msLaunch"]);
        Assert.Equal("meetup-join", query["type"]);
    }

    [Fact]
    public void BuildLauncherUrl_PreservesSchemeHostAndPath()
    {
        var result = TeamsMeetingUrl.BuildLauncherUrl(
            "https://teams.microsoft.com/dl/launcher/launcher.html?foo=bar");

        var uri = new Uri(result);

        Assert.Equal("https", uri.Scheme);
        Assert.Equal("teams.microsoft.com", uri.Host);
        Assert.Equal("/dl/launcher/launcher.html", uri.AbsolutePath);
    }

    [Fact]
    public void BuildLauncherUrl_DoesNotAddEnableMobilePage()
    {
        // We deliberately omit enableMobilePage (the blueprint adds it) because the desktop web
        // selectors target the desktop DOM.
        var result = TeamsMeetingUrl.BuildLauncherUrl(
            "https://teams.microsoft.com/dl/launcher/launcher.html?x=1");

        Assert.DoesNotContain("enableMobilePage", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildLauncherUrl_AddsLaunchParams_WhenNoQueryPresent()
    {
        var result = TeamsMeetingUrl.BuildLauncherUrl("https://teams.microsoft.com/l/meetup-join/abc");

        var query = HttpUtility.ParseQueryString(new Uri(result).Query);

        Assert.Equal("false", query["msLaunch"]);
        Assert.Equal("meetup-join", query["type"]);
        Assert.Equal("true", query["directDl"]);
        Assert.Equal("true", query["suppressPrompt"]);
    }

    // ---- IsLikelyTeamsUrl -------------------------------------------------------------------------

    [Theory]
    [InlineData("https://teams.microsoft.com/l/meetup-join/abc")]
    [InlineData("https://teams.live.com/meet/123")]
    [InlineData("https://emea.teams.microsoft.com/l/meetup-join/abc")] // sub-domain
    [InlineData("HTTPS://Teams.Microsoft.Com/l/meetup-join/abc")]      // case-insensitive
    [InlineData("  https://teams.microsoft.com/l/meetup-join/abc  ")]  // trimmed
    public void IsLikelyTeamsUrl_True_ForTeamsLinks(string url)
    {
        Assert.True(TeamsMeetingUrl.IsLikelyTeamsUrl(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-url")]
    [InlineData("ftp://teams.microsoft.com/x")]                         // wrong scheme
    [InlineData("https://example.com/meeting")]                        // wrong host
    [InlineData("https://evil.com/?x=teams.microsoft.com")]            // host only in query, not authority
    [InlineData("https://teams.microsoft.com.evil.com/x")]             // suffix-spoof
    [InlineData("https://notteams.microsoft.com/x")]                   // not a real sub-domain boundary
    public void IsLikelyTeamsUrl_False_ForNonTeamsLinks(string? url)
    {
        Assert.False(TeamsMeetingUrl.IsLikelyTeamsUrl(url));
    }
}
