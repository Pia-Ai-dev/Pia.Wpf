using Pia.Services.MeetingAttendee;
using Xunit;

namespace Pia.Tests.Services.MeetingAttendee;

/// <summary>
/// Unit tests for the pure, PID-matching predicate extracted from <see cref="TeamsMeetingSession"/>'s
/// process scan (the rest of the session drives a live Playwright browser and is not unit-testable here).
/// </summary>
public class TeamsMeetingSessionTests
{
    [Fact]
    public void IsLaunchedBrowserProcess_WhenMatchPathKnown_RequiresExactPathMatch()
    {
        const string match = @"C:\cache\chromium-1\chrome-win64\chrome.exe";

        Assert.True(TeamsMeetingSession.IsLaunchedBrowserProcess(match, match));
        // Case-insensitive (Windows paths).
        Assert.True(TeamsMeetingSession.IsLaunchedBrowserProcess(match, match.ToUpperInvariant()));
        // The user's own Chrome at a different path is excluded.
        Assert.False(TeamsMeetingSession.IsLaunchedBrowserProcess(
            match, @"C:\Program Files\Google\Chrome\Application\chrome.exe"));
    }

    [Fact]
    public void IsLaunchedBrowserProcess_WhenMatchPathKnown_ButModuleUnreadable_Excludes()
    {
        // A protected/exited process whose module path could not be read cannot be confirmed ours.
        Assert.False(TeamsMeetingSession.IsLaunchedBrowserProcess(@"C:\cache\chrome.exe", null));
    }

    [Fact]
    public void IsLaunchedBrowserProcess_WhenMatchPathNull_AcceptsAny_RelyingOnSnapshotDiff()
    {
        // App Paths resolution failed (null match path): we cannot disambiguate by path, so every
        // same-named process is a candidate and the pre-launch snapshot diff narrows it.
        Assert.True(TeamsMeetingSession.IsLaunchedBrowserProcess(null, @"C:\any\msedge.exe"));
        Assert.True(TeamsMeetingSession.IsLaunchedBrowserProcess(null, null));
    }
}
