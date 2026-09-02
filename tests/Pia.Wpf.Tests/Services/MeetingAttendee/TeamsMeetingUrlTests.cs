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
    // ---- ExtractFromText -------------------------------------------------------------------------

    // The shape a real Outlook Teams invite writes into PR_BODY, GUIDs redacted: two joinable links,
    // plus links that sit on a Teams host or merely look like one and must not be picked.
    private const string MeetupJoinUrl =
        "https://teams.microsoft.com/l/meetup-join/19%3ameeting_ZGVjb3k%40thread.v2/0"
        + "?context=%7b%22Tid%22%3a%2200000000-0000-0000-0000-000000000001%22"
        + "%2c%22Oid%22%3a%2200000000-0000-0000-0000-000000000002%22%7d";

    private const string ShortJoinUrl = "https://teams.microsoft.com/meet/368400251931177?p=1HSbqlBpMrcHsvZhWY";

    private static string InviteBody() => string.Join("\n",
        "________________________________________________________________________________",
        "Microsoft Teams meeting ",
        "Join: " + ShortJoinUrl + " ",
        "Meeting ID: 368 400 251 931 177 ",
        "Passcode: 7vb2KS7F ",
        "________________________________",
        "",
        "Ben\u00F6tigen Sie Hilfe? <https://aka.ms/JoinTeamsMeeting?omkt=de-DE>  | System reference <"
            + MeetupJoinUrl + ">  ",
        "Dial in by phone ",
        "+49 69 365057559,,332996648# <tel:+4969365057559,,332996648#>  Deutschland, Frankfurt ",
        "Find a local number <https://dialin.teams.cloud.microsoft/dfe5b9cc?id=332996648>  ",
        "Phone conference ID: 332 996 648# ",
        "For organizers: Besprechungsoptionen <https://teams.microsoft.com/meetingOptions/"
            + "?organizerId=00000000-0000-0000-0000-000000000002&language=de-DE>  ",
        "________________________________________________________________________________");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ExtractFromText_ReturnsNull_ForNullOrWhitespace(string? input)
    {
        Assert.Null(TeamsMeetingUrl.ExtractFromText(input));
    }

    [Fact]
    public void ExtractFromText_PrefersTheClassicDeepLink_OverTheShortJoinLink()
    {
        // Escapes included: %3a/%40/%7b carry the meeting context and must survive verbatim.
        Assert.Equal(MeetupJoinUrl, TeamsMeetingUrl.ExtractFromText(InviteBody()));
    }

    [Fact]
    public void ExtractFromText_FallsBackToTheShortJoinLink_WhenItIsTheOnlyOne()
    {
        var body = "Microsoft Teams meeting\nJoin: " + ShortJoinUrl + "\nPasscode: 7vb2KS7F";

        Assert.Equal(ShortJoinUrl, TeamsMeetingUrl.ExtractFromText(body));
    }

    [Fact]
    public void ExtractFromText_SkipsTheOrganizerAndDialInPages()
    {
        var body = "For organizers: <https://teams.microsoft.com/meetingOptions/?organizerId=x>\n"
            + "Reset PIN <https://teams.microsoft.com/usp/pstnconferencing>\n"
            + "Launcher <https://teams.microsoft.com/dl/launcher/launcher.html?url=x>";

        Assert.Null(TeamsMeetingUrl.ExtractFromText(body));
    }

    [Fact]
    public void ExtractFromText_SkipsHostsThatAreNotTeams()
    {
        var body = "Help <https://aka.ms/JoinTeamsMeeting?omkt=de-DE>\n"
            + "Dial in <https://dialin.teams.cloud.microsoft/dfe5b9cc?id=332996648>\n"
            + "Spoof <https://evil.example/?x=teams.microsoft.com/l/meetup-join/19>";

        Assert.Null(TeamsMeetingUrl.ExtractFromText(body));
    }

    [Fact]
    public void ExtractFromText_DropsTrailingSentencePunctuation()
    {
        Assert.Equal(ShortJoinUrl, TeamsMeetingUrl.ExtractFromText("Join at " + ShortJoinUrl + "."));
    }

    [Fact]
    public void ExtractFromText_DoesNotUnfoldAPlainTextBody()
    {
        // A mail body indents its own wrapped lines. Unfolding here would splice the next line onto the
        // URL, which is why the unfold is gated on the iCalendar marker.
        var body = "Join here: " + ShortJoinUrl + "\n and bring your notes.";

        Assert.Equal(ShortJoinUrl, TeamsMeetingUrl.ExtractFromText(body));
    }

    [Fact]
    public void ExtractFromText_UnfoldsAnIcalendarDescription()
    {
        var calendar = string.Join("\r\n",
            "BEGIN:VCALENDAR",
            "BEGIN:VEVENT",
            "SUMMARY:Standup\\, daily",
            "DESCRIPTION:Join here: https://teams.microsoft.com/l/meetup-jo",
            " in/19%3ameeting_ZGVjb3k%40thread.v2/0?context=x",
            "END:VEVENT",
            "END:VCALENDAR");

        Assert.Equal(
            "https://teams.microsoft.com/l/meetup-join/19%3ameeting_ZGVjb3k%40thread.v2/0?context=x",
            TeamsMeetingUrl.ExtractFromText(calendar));
    }

    [Fact]
    public void ExtractFromText_ReadsTheFoldedSkypeTeamsProperty()
    {
        var calendar = string.Join("\r\n",
            "BEGIN:VCALENDAR",
            "BEGIN:VEVENT",
            "X-MICROSOFT-SKYPETEAMSMEETINGURL:https://teams.microsoft.com/l/meetup-join/19%3ame",
            " eting_ZGVjb3k%40thread.v2/0?context=y",
            "END:VEVENT",
            "END:VCALENDAR");

        Assert.Equal(
            "https://teams.microsoft.com/l/meetup-join/19%3ameeting_ZGVjb3k%40thread.v2/0?context=y",
            TeamsMeetingUrl.ExtractFromText(calendar));
    }

    [Fact]
    public void ExtractFromText_ReturnsNull_WhenTheMailCarriesNoLink()
    {
        Assert.Null(TeamsMeetingUrl.ExtractFromText("Subject: Lunch\n===\n\nSee you at one."));
    }
}
