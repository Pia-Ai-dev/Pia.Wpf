using System.Collections.Specialized;
using System.Text.RegularExpressions;
using System.Web;

namespace Pia.Services.MeetingAttendee;

/// <summary>
/// Pure helpers for the Teams meeting-join URL handling. Kept network-free and side-effect-free so
/// the launcher-URL transform is unit-testable in isolation (the actual redirect-follow lives in
/// <see cref="TeamsMeetingSession"/>, which calls <see cref="BuildLauncherUrl"/> on the resolved
/// final URL).
/// </summary>
public static partial class TeamsMeetingUrl
{
    // Hosts we accept as a Teams meeting link. Matched as exact host or sub-domain suffix (never as a
    // substring) so a hostile URL like "https://evil.com/?x=teams.microsoft.com" is rejected.
    private static readonly string[] TeamsHosts = ["teams.microsoft.com", "teams.live.com"];

    /// <summary>
    /// Lightweight, network-free validation that <paramref name="url"/> looks like a Teams meeting
    /// link: an absolute http/https URL whose host is (or is a sub-domain of) a known Teams host.
    /// Used by the ViewModel to gate the Join command — it intentionally does not verify the meeting
    /// exists (that only happens once the browser actually joins).
    /// </summary>
    public static bool IsLikelyTeamsUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

        var host = uri.Host;
        return TeamsHosts.Any(h => string.Equals(host, h, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Rewrites the resolved Teams launcher URL so the browser goes straight to the web-join flow
    /// instead of popping the native "open in Teams app?" dialog (which Playwright cannot dismiss
    /// because it is an OS-level prompt outside the page).
    ///
    /// Mirrors the blueprint's transform (<c>join-procedure.ts</c>): strip <c>msLaunch=true</c> and
    /// set <c>msLaunch=false &amp; type=meetup-join &amp; directDl=true &amp; suppressPrompt=true</c>,
    /// while preserving every other query parameter (the meeting context lives in those params).
    ///
    /// Note: the blueprint also adds <c>enableMobilePage=true</c>; we deliberately omit it because
    /// the web-join selectors in <see cref="TeamsMeetingSession"/> target the desktop DOM, which the
    /// mobile page changes.
    /// </summary>
    /// <param name="resolvedUrl">The final URL after following the meeting-URL redirect(s).</param>
    /// <returns>The rewritten absolute URL to navigate to.</returns>
    public static string BuildLauncherUrl(string resolvedUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedUrl);

        var builder = new UriBuilder(resolvedUrl);

        // Parse, overwrite the launch-controlling params, keep the rest.
        // UriBuilder.Query returns the query INCLUDING the leading '?' (and "" when empty); strip it
        // so the first key never gets corrupted into "?key" — which would make Set() append a
        // duplicate instead of overwriting. TrimStart is harmless when no '?' is present.
        NameValueCollection query = HttpUtility.ParseQueryString(builder.Query.TrimStart('?'));
        query.Set("msLaunch", "false");
        query.Set("type", "meetup-join");
        query.Set("directDl", "true");
        query.Set("suppressPrompt", "true");

        builder.Query = query.ToString();
        return builder.Uri.ToString();
    }

    // Ranked best-first: the classic deep link is the shape the join form's placeholder shows and the
    // shape BuildLauncherUrl's type=meetup-join assumes, and it carries no passcode.
    private const string MeetupJoinPath = "/l/meetup-join/";
    private const string ShortJoinPath = "/meet/";

    // Organizer-only and dial-in pages sit on a Teams host but are not joinable.
    private static readonly string[] NonJoinPaths = ["/meetingoptions", "/dl/", "/usp/"];

    private const string ICalendarMarker = "BEGIN:VCALENDAR";

    /// <summary>
    /// Picks the meeting link out of an invite's text — a rendered mail body or a raw iCalendar file.
    /// A Teams invite carries several links on the same host (the join link, the organizer's meeting
    /// options, dial-in help), so the first match is not good enough; candidates are filtered through
    /// <see cref="IsLikelyTeamsUrl"/> and then ranked. Returns null when nothing joinable is present.
    /// </summary>
    public static string? ExtractFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        if (text.Contains(ICalendarMarker, StringComparison.OrdinalIgnoreCase))
        {
            // Guarded on the marker on purpose: a plain-text mail body indents its own wrapped lines,
            // and unfolding those would splice a real URL apart.
            text = IcalFoldRegex().Replace(text, string.Empty).Replace(@"\,", ",", StringComparison.Ordinal);

            var declared = SkypeTeamsUrlRegex().Match(text);
            if (declared.Success && TryClassify(declared.Groups[1].Value, out var declaredUrl, out _))
                return declaredUrl;
        }

        string? best = null;
        var bestRank = int.MaxValue;

        foreach (Match match in UrlCandidateRegex().Matches(text))
        {
            if (!TryClassify(match.Value, out var candidate, out var rank) || rank >= bestRank) continue;

            best = candidate;
            bestRank = rank;
            if (rank == 0) break;
        }

        return best;
    }

    /// <summary>
    /// Cleans one candidate and scores it. The cleaned string is returned verbatim rather than
    /// re-serialised from the <see cref="Uri"/>: a join link's <c>%3a</c>/<c>%40</c>/<c>%7b</c> escapes
    /// carry the meeting context and must survive untouched.
    /// </summary>
    private static bool TryClassify(string raw, out string url, out int rank)
    {
        url = string.Empty;
        rank = int.MaxValue;

        // Sentence punctuation and the angle brackets a mail body wraps links in are not part of the link.
        var candidate = raw.Trim().TrimEnd('.', ',', ';', ':', ')', ']', '}', '>', '"', '\'');
        if (!IsLikelyTeamsUrl(candidate)) return false;

        var path = new Uri(candidate).AbsolutePath;
        if (NonJoinPaths.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return false;

        url = candidate;
        rank = path.StartsWith(MeetupJoinPath, StringComparison.OrdinalIgnoreCase) ? 0
            : path.StartsWith(ShortJoinPath, StringComparison.OrdinalIgnoreCase) ? 1
            : 2;
        return true;
    }

    // The class stops before the '>' of the <https://…> wrapper Outlook puts around a body's links.
    [GeneratedRegex("""https?://[^\s<>"']+""", RegexOptions.IgnoreCase)]
    private static partial Regex UrlCandidateRegex();

    // RFC 5545 folding: a newline followed by one space or tab continues the previous line, so a long
    // join URL reaches us split across lines.
    [GeneratedRegex(@"\r?\n[ \t]")]
    private static partial Regex IcalFoldRegex();

    [GeneratedRegex(@"^X-MICROSOFT-SKYPETEAMSMEETINGURL:(.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex SkypeTeamsUrlRegex();
}
