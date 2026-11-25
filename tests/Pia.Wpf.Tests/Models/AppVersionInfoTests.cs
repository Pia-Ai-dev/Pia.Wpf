using System.Net.Http;
using System.Text.RegularExpressions;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Models;

public class AppVersionInfoTests
{
    // The server's reading: a Pia/ token at a word start, then major.minor of one to four digits each.
    private static readonly Regex PiaDesktopToken = new(
        @"(?<![A-Za-z0-9])Pia/\d{1,4}\.\d{1,4}(?!\d)", RegexOptions.CultureInvariant);

    // Any of these makes the server file the sign-in under "Other".
    private static readonly string[] AutomationMarkers = ["bot", "spider", "crawl", "slurp", "headless", "+http"];

    private const int ServerStoredLength = 512;

    public static TheoryData<string> UserAgents() =>
    [
        AppVersionInfo.UserAgent,
        AppVersionInfo.UserAgentFor("0.0"),
        AppVersionInfo.UserAgentFor("1.4.451-beta"),
    ];

    [Theory]
    [MemberData(nameof(UserAgents))]
    public void UserAgent_HasTheShapeTheServerReads(string userAgent)
    {
        Assert.InRange(userAgent.Length, 1, ServerStoredLength);
        Assert.Matches(PiaDesktopToken, userAgent);
        Assert.All(AutomationMarkers, marker => Assert.DoesNotContain(marker, userAgent, StringComparison.OrdinalIgnoreCase));

        using var request = new HttpRequestMessage();
        Assert.True(request.Headers.UserAgent.TryParseAdd(userAgent), $"'{userAgent}' is not a valid User-Agent");
    }

    [Fact]
    public void UserAgent_CarriesTheBuildVersion()
    {
        Assert.StartsWith($"Pia/{AppVersionInfo.Version} (Windows NT ", AppVersionInfo.UserAgent, StringComparison.Ordinal);
    }
}
