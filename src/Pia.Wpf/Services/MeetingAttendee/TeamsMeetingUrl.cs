using System.Collections.Specialized;
using System.Web;

namespace Pia.Services.MeetingAttendee;

/// <summary>
/// Pure helpers for the Teams meeting-join URL handling. Kept network-free and side-effect-free so
/// the launcher-URL transform is unit-testable in isolation (the actual redirect-follow lives in
/// <see cref="TeamsMeetingSession"/>, which calls <see cref="BuildLauncherUrl"/> on the resolved
/// final URL).
/// </summary>
public static class TeamsMeetingUrl
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
    /// the desktop web-join selectors used here (<c>joinOnWeb</c>, "Type your name", "Join now",
    /// <c>hangup-button</c>) target the desktop DOM, which the mobile page changes.
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
}
