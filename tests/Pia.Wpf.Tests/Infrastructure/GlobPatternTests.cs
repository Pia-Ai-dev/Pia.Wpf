using Pia.Infrastructure;
using Xunit;

namespace Pia.Tests.Infrastructure;

/// <summary>
/// Covers <see cref="GlobPattern.Compile"/>: the anchoring an ignore rule uses, so a bare name matches a
/// trailing segment at any depth while a slash-bearing pattern is pinned to the search base.
/// </summary>
public sealed class GlobPatternTests
{
    [Fact]
    public void Star_StaysWithinOneSegment()
    {
        var rx = GlobPattern.Compile("docs/*.md");

        Assert.Matches(rx, "docs/readme.md");
        Assert.DoesNotMatch(rx, "docs/guide/readme.md");
    }

    [Fact]
    public void Question_MatchesExactlyOneNonSeparatorChar()
    {
        var rx = GlobPattern.Compile("a?c");

        Assert.Matches(rx, "abc");
        Assert.DoesNotMatch(rx, "ac");
        Assert.DoesNotMatch(rx, "abbc");
        Assert.DoesNotMatch(rx, "a/c");
    }

    [Fact]
    public void DoubleStarSlash_CrossesDirectories()
    {
        var rx = GlobPattern.Compile("docs/**/*.md");

        Assert.Matches(rx, "docs/readme.md");
        Assert.Matches(rx, "docs/guide/deep/readme.md");
        Assert.DoesNotMatch(rx, "src/docs/readme.md");
    }

    [Fact]
    public void TrailingDoubleStar_MatchesAcrossSeparators()
    {
        var rx = GlobPattern.Compile("docs/**");

        Assert.Matches(rx, "docs/readme.md");
        Assert.Matches(rx, "docs/guide/deep/readme.md");
        Assert.DoesNotMatch(rx, "src/readme.md");
    }

    [Fact]
    public void CharacterClass_MatchesEitherMember()
    {
        var rx = GlobPattern.Compile("[Dd]ebug");

        Assert.Matches(rx, "Debug");
        Assert.Matches(rx, "debug");
        Assert.DoesNotMatch(rx, "Xebug");
    }

    [Fact]
    public void NegatedClass_NeverMatchesASeparator()
    {
        var rx = GlobPattern.Compile("x[!a]y");

        Assert.Matches(rx, "xby");
        Assert.DoesNotMatch(rx, "xay");
        Assert.DoesNotMatch(rx, "x/y");
    }

    [Fact]
    public void BareName_MatchesAtAnyDepth()
    {
        var rx = GlobPattern.Compile("*.md");

        Assert.Matches(rx, "readme.md");
        Assert.Matches(rx, "docs/readme.md");
        Assert.Matches(rx, "docs/guide/readme.md");
        Assert.DoesNotMatch(rx, "readme.txt");
    }

    [Fact]
    public void SlashBearingPattern_IsAnchoredToTheBase()
    {
        var rx = GlobPattern.Compile("docs/readme.md");

        Assert.Matches(rx, "docs/readme.md");
        Assert.DoesNotMatch(rx, "src/docs/readme.md");
    }

    [Fact]
    public void LeadingSlash_IsTrimmedAndAnchors()
    {
        var rx = GlobPattern.Compile("/readme.md");

        Assert.Matches(rx, "readme.md");
        Assert.DoesNotMatch(rx, "docs/readme.md");
    }

    [Fact]
    public void TrailingSlash_MatchesEverythingUnderTheFolder()
    {
        // A candidate is always a file path, so a bare "docs/" would be unsatisfiable.
        var rx = GlobPattern.Compile("docs/");

        Assert.Matches(rx, "docs/readme.md");
        Assert.Matches(rx, "docs/sub/a.md");
        Assert.DoesNotMatch(rx, "docs");
        Assert.DoesNotMatch(rx, "src/docs/readme.md");
    }

    [Fact]
    public void BackslashSeparators_ReadAsPathSeparators()
    {
        var rx = GlobPattern.Compile(@"docs\*.md");

        Assert.Matches(rx, "docs/readme.md");
        Assert.DoesNotMatch(rx, "src/docs/readme.md");
        Assert.DoesNotMatch(rx, "docs/sub/readme.md");
    }

    [Fact]
    public void Braces_AreLiteral_NotABraceSet()
    {
        var rx = GlobPattern.Compile("*.{md,txt}");

        Assert.DoesNotMatch(rx, "readme.md");
        Assert.DoesNotMatch(rx, "readme.txt");
        Assert.Matches(rx, "readme.{md,txt}");
    }
}
