using Pia.Services.MeetingAttendee;
using Xunit;

namespace Pia.Tests.Services.MeetingAttendee;

/// <summary>
/// Unit tests for the pieces of <see cref="TeamsMeetingSession"/> reachable without a live browser: the
/// PID-matching predicate, and the shape of the join-path selectors that a localized Teams client breaks.
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

    public static TheoryData<string, string[]> JoinPathSelectors() => new()
    {
        { nameof(TeamsMeetingSession.NameInputSelectors), TeamsMeetingSession.NameInputSelectors },
        { nameof(TeamsMeetingSession.JoinNowSelectors), TeamsMeetingSession.JoinNowSelectors },
    };

    [Theory]
    [MemberData(nameof(JoinPathSelectors))]
    public void JoinPathSelectors_LeadWithALocaleProofSelector(string name, string[] selectors)
    {
        // The German prejoin renders "Geben Sie Ihren Namen ein", so a text match cannot be the
        // primary net even though the forced locale usually keeps it working.
        Assert.NotEmpty(selectors);

        var first = selectors[0];
        Assert.True(
            first.Contains("data-tid", StringComparison.Ordinal) || first.StartsWith('#'),
            $"{name}[0] must be attribute- or id-based, but was '{first}'.");
        Assert.DoesNotContain(":has-text(", first);
        Assert.DoesNotContain("placeholder=", first);
    }

    [Theory]
    [MemberData(nameof(JoinPathSelectors))]
    public void JoinPathSelectors_HaveNoStructuralCatchAll(string name, string[] selectors)
    {
        // A bare ":visible" would match whatever input/button happens to be on screen and act on the
        // wrong element successfully — worse than the clean timeout it would be replacing.
        var structural = selectors.Where(s => s.Contains(":visible", StringComparison.Ordinal)).ToArray();
        Assert.True(
            structural.Length == 0,
            $"{name} must not use a structural catch-all: {string.Join(", ", structural)}");
    }

    [Theory]
    [MemberData(nameof(JoinPathSelectors))]
    public void JoinPathSelectors_AreDistinct(string name, string[] selectors)
    {
        Assert.True(
            selectors.Length == selectors.Distinct(StringComparer.Ordinal).Count(),
            $"{name} has duplicate entries.");
    }
}
