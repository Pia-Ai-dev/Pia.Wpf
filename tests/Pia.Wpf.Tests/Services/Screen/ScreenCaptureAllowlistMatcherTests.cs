using Pia.Services.Screen;
using Xunit;

namespace Pia.Tests.Services.Screen;

/// <summary>The rule an unattended capture rests on: an entry authorises a target only while exactly one open
/// window matches it.</summary>
public class ScreenCaptureAllowlistMatcherTests
{
    private static ScreenCaptureAllowlistEntry Entry(string process, string title = "") =>
        new(Guid.NewGuid(), process, title, DateTimeOffset.UtcNow);

    [Theory]
    [InlineData(" Outlook.EXE ", "Outlook")]
    [InlineData("outlook", "outlook")]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    public void NormalizeProcessName_TrimsAndStripsExe(string? input, string expected) =>
        Assert.Equal(expected, ScreenCaptureAllowlistMatcher.NormalizeProcessName(input));

    [Fact]
    public void Matches_ProcessIsCaseInsensitive_AndExtensionBlind() =>
        Assert.True(ScreenCaptureAllowlistMatcher.Matches(Entry("outlook"), "OUTLOOK.exe", "Inbox"));

    [Fact]
    public void Matches_EmptyTitlePattern_MatchesAnyTitle()
    {
        Assert.True(ScreenCaptureAllowlistMatcher.Matches(Entry("outlook"), "outlook", "anything at all"));
        Assert.True(ScreenCaptureAllowlistMatcher.Matches(Entry("outlook"), "outlook", ""));
    }

    [Fact]
    public void Matches_TitleIsCaseInsensitiveSubstring()
    {
        Assert.True(ScreenCaptureAllowlistMatcher.Matches(Entry("outlook", "inbox"), "outlook", "Inbox - Outlook"));
        Assert.False(ScreenCaptureAllowlistMatcher.Matches(Entry("outlook", "inbox"), "outlook", "Calendar - Outlook"));
    }

    /// <summary>The field is labelled "Title contains", so a user typing a star means a star.</summary>
    [Fact]
    public void Matches_StarAndQuestionMarkAreLiteral()
    {
        Assert.False(ScreenCaptureAllowlistMatcher.Matches(Entry("outlook", "In*ox"), "outlook", "Inbox"));
        Assert.True(ScreenCaptureAllowlistMatcher.Matches(Entry("outlook", "In*ox"), "outlook", "In*ox - Outlook"));
    }

    /// <summary>Enumerating an elevated window can return no process name, and that must not match a blank entry.</summary>
    [Fact]
    public void Matches_BlankProcessEntry_NeverMatches() =>
        Assert.False(ScreenCaptureAllowlistMatcher.Matches(Entry(""), "", "Inbox"));

    [Fact]
    public void Resolve_NotListed_WhenNoEntryMatchesTheCandidate()
    {
        var candidate = new AllowlistWindow("notepad", "Untitled");
        var result = ScreenCaptureAllowlistMatcher.Resolve([Entry("outlook")], candidate, [candidate]);

        Assert.Equal(AllowlistVerdict.NotListed, result.Verdict);
        Assert.Null(result.Entry);
    }

    [Fact]
    public void Resolve_Allowed_WhenExactlyOneVisibleWindowMatches()
    {
        var entry = Entry("outlook");
        var candidate = new AllowlistWindow("outlook", "Inbox - Outlook");
        var windows = new[] { candidate, new AllowlistWindow("notepad", "Untitled - Notepad") };

        var result = ScreenCaptureAllowlistMatcher.Resolve([entry], candidate, windows);

        Assert.Equal(AllowlistVerdict.Allowed, result.Verdict);
        Assert.Equal(entry, result.Entry);
        Assert.Equal(1, result.MatchCount);
    }

    [Fact]
    public void Resolve_Ambiguous_WhenTwoWindowsMatchTheOnlyEntry()
    {
        var candidate = new AllowlistWindow("outlook", "Inbox - Outlook");
        var windows = new[] { candidate, new AllowlistWindow("outlook", "Calendar - Outlook") };

        var result = ScreenCaptureAllowlistMatcher.Resolve([Entry("outlook")], candidate, windows);

        Assert.Equal(AllowlistVerdict.Ambiguous, result.Verdict);
        Assert.Null(result.Entry);
        Assert.Equal(2, result.MatchCount);
    }

    [Fact]
    public void Resolve_Allowed_WhenANarrowerEntryDisambiguates()
    {
        var narrow = Entry("outlook", "Inbox");
        var candidate = new AllowlistWindow("outlook", "Inbox - Outlook");
        var windows = new[] { candidate, new AllowlistWindow("outlook", "Calendar - Outlook") };

        var result = ScreenCaptureAllowlistMatcher.Resolve([Entry("outlook"), narrow], candidate, windows);

        Assert.Equal(AllowlistVerdict.Allowed, result.Verdict);
        Assert.Equal(narrow, result.Entry);
    }

    /// <summary>Two windows of one program with the same title are two windows, not one named window.</summary>
    [Fact]
    public void Resolve_Ambiguous_WhenTwoIdenticalWindowsAreOpen()
    {
        var candidate = new AllowlistWindow("notepad", "Untitled - Notepad");

        var result = ScreenCaptureAllowlistMatcher.Resolve(
            [Entry("notepad")], candidate, [candidate, candidate]);

        Assert.Equal(AllowlistVerdict.Ambiguous, result.Verdict);
        Assert.Equal(2, result.MatchCount);
    }

    [Fact]
    public void Resolve_NotListed_WhenTheCandidateIsNotAmongTheVisibleWindows()
    {
        var candidate = new AllowlistWindow("outlook", "Inbox - Outlook");

        var result = ScreenCaptureAllowlistMatcher.Resolve(
            [Entry("outlook")], candidate, [new AllowlistWindow("outlook", "Calendar - Outlook")]);

        Assert.Equal(AllowlistVerdict.NotListed, result.Verdict);
        Assert.Equal(0, result.MatchCount);
    }
}
