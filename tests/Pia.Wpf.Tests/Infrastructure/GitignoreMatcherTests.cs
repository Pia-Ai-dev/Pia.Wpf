using Pia.Infrastructure;
using Xunit;

namespace Pia.Tests.Infrastructure;

/// <summary>
/// Covers the practical <c>.gitignore</c> subset <see cref="GitignoreMatcher"/> implements: comments,
/// blanks, negation (last-match-wins), leading-<c>/</c> anchoring, trailing-<c>/</c> dir-only patterns,
/// and the <c>*</c>/<c>?</c>/<c>**</c> wildcards. The load-bearing invariant is segment (not substring)
/// matching — "bin" must never match "cabinet".
/// </summary>
public sealed class GitignoreMatcherTests
{
    [Fact]
    public void CommentsAndBlanks_ProduceEmptyMatcher()
    {
        var m = GitignoreMatcher.FromLines(["# a comment", "", "   ", "\t"]);
        Assert.True(m.IsEmpty);
        Assert.False(m.IsIgnored("anything", isDirectory: true));
    }

    [Fact]
    public void EmptyMatcher_IgnoresNothing()
    {
        var m = GitignoreMatcher.FromLines([]);
        Assert.False(m.IsIgnored("bin", isDirectory: true));
        Assert.False(m.IsIgnored("a/b/c.txt", isDirectory: false));
    }

    [Fact]
    public void BareDirPattern_MatchesAtAnyDepth()
    {
        var m = GitignoreMatcher.FromLines(["bin/"]);
        Assert.True(m.IsIgnored("bin", isDirectory: true));
        Assert.True(m.IsIgnored("src/bin", isDirectory: true));
        Assert.True(m.IsIgnored("a/b/bin", isDirectory: true));
    }

    [Fact]
    public void SegmentMatch_NotSubstring()
    {
        var m = GitignoreMatcher.FromLines(["bin/", "obj/"]);
        // "cabinet" contains "bin"; "object.txt" contains "obj" — neither is a whole-segment match.
        Assert.False(m.IsIgnored("cabinet", isDirectory: true));
        Assert.False(m.IsIgnored("object.txt", isDirectory: false));
        Assert.False(m.IsIgnored("src/cabinet", isDirectory: true));
    }

    [Fact]
    public void DirOnlyPattern_DoesNotMatchFileOfSameName()
    {
        var m = GitignoreMatcher.FromLines(["bin/"]);
        Assert.True(m.IsIgnored("bin", isDirectory: true));
        Assert.False(m.IsIgnored("bin", isDirectory: false)); // a *file* named bin is not ignored
    }

    [Fact]
    public void NoTrailingSlashPattern_MatchesFileAndDirectory()
    {
        var m = GitignoreMatcher.FromLines(["node_modules"]);
        Assert.True(m.IsIgnored("node_modules", isDirectory: true));
        Assert.True(m.IsIgnored("node_modules", isDirectory: false));
    }

    [Fact]
    public void LeadingSlash_AnchorsToRoot()
    {
        var m = GitignoreMatcher.FromLines(["/build"]);
        Assert.True(m.IsIgnored("build", isDirectory: true));
        Assert.False(m.IsIgnored("src/build", isDirectory: true)); // anchored: only the root-level one
    }

    [Fact]
    public void InteriorSlash_AnchorsToRoot()
    {
        var m = GitignoreMatcher.FromLines(["src/obj/"]);
        Assert.True(m.IsIgnored("src/obj", isDirectory: true));
        Assert.False(m.IsIgnored("obj", isDirectory: true)); // not the same anchored path
    }

    [Fact]
    public void StarGlob_MatchesExtensionAtAnyDepth()
    {
        var m = GitignoreMatcher.FromLines(["*.log"]);
        Assert.True(m.IsIgnored("app.log", isDirectory: false));
        Assert.True(m.IsIgnored("a/b/c.log", isDirectory: false));
        Assert.False(m.IsIgnored("a/b/c.txt", isDirectory: false));
    }

    [Fact]
    public void QuestionMark_MatchesSingleChar()
    {
        var m = GitignoreMatcher.FromLines(["file?.txt"]);
        Assert.True(m.IsIgnored("file1.txt", isDirectory: false));
        Assert.False(m.IsIgnored("file12.txt", isDirectory: false));
    }

    [Fact]
    public void DoubleStar_SpansDirectories()
    {
        var m = GitignoreMatcher.FromLines(["logs/**"]);
        Assert.True(m.IsIgnored("logs/a/b.txt", isDirectory: false));

        var any = GitignoreMatcher.FromLines(["**/temp"]);
        Assert.True(any.IsIgnored("temp", isDirectory: true));
        Assert.True(any.IsIgnored("a/b/temp", isDirectory: true));
    }

    [Fact]
    public void Negation_LastMatchWins()
    {
        var m = GitignoreMatcher.FromLines(["*.log", "!keep.log"]);
        Assert.False(m.IsIgnored("keep.log", isDirectory: false)); // re-included by the later !rule
        Assert.True(m.IsIgnored("other.log", isDirectory: false));
    }

    [Fact]
    public void Match_IsCaseInsensitive()
    {
        var m = GitignoreMatcher.FromLines([".git/"]);
        Assert.True(m.IsIgnored(".GIT", isDirectory: true));
    }

    [Fact]
    public void CharacterClass_MatchesAlternatives()
    {
        // The stock VisualStudio .gitignore is built from these; they must resolve, not be literal.
        var m = GitignoreMatcher.FromLines(["[Dd]ebug/", "[Rr]elease/"]);
        Assert.True(m.IsIgnored("Debug", isDirectory: true));
        Assert.True(m.IsIgnored("debug", isDirectory: true));
        Assert.True(m.IsIgnored("src/Release", isDirectory: true));
        Assert.False(m.IsIgnored("Prebug", isDirectory: true));
    }

    [Fact]
    public void CharacterClass_Range()
    {
        var m = GitignoreMatcher.FromLines(["file[0-9].txt"]);
        Assert.True(m.IsIgnored("file7.txt", isDirectory: false));
        Assert.False(m.IsIgnored("fileA.txt", isDirectory: false));
    }

    [Fact]
    public void CharacterClass_Negated_ExcludesSeparator()
    {
        // A negated class must not match the path separator, or "x[!y]" would span directories.
        var m = GitignoreMatcher.FromLines(["x[!y]"]);
        Assert.True(m.IsIgnored("xa", isDirectory: false));
        Assert.False(m.IsIgnored("xy", isDirectory: false));
        Assert.False(m.IsIgnored("x/z", isDirectory: false)); // separator not swallowed by [!y]
    }

    [Fact]
    public void InteriorDoubleStar_DoesNotCrossSeparators()
    {
        // git treats a non-slash-delimited "**" as a single "*" (segment-local), unlike "a/**/b".
        var m = GitignoreMatcher.FromLines(["a**b"]);
        Assert.True(m.IsIgnored("axb", isDirectory: false));
        Assert.True(m.IsIgnored("foo/axb", isDirectory: false));
        Assert.False(m.IsIgnored("a/x/b", isDirectory: false));
    }

    [Fact]
    public void PathologicalWildcardPattern_MatchesInLinearTime()
    {
        // Regression guard for the ReDoS fix: without RegexOptions.NonBacktracking this catastrophically
        // backtracks and never returns, hanging the (synchronous) @Files enumeration. With it, matching
        // is linear and completes immediately. The test would time out if the fix regressed.
        var m = GitignoreMatcher.FromLines([new string('*', 1) + string.Concat(Enumerable.Repeat("a*", 20)) + "b"]);
        var input = new string('a', 200); // no 'b' → no match, worst case for backtracking
        Assert.False(m.IsIgnored(input, isDirectory: false));
    }

    [Fact]
    public void BackslashSeparators_AreNormalized()
    {
        var m = GitignoreMatcher.FromLines(["bin/"]);
        Assert.True(m.IsIgnored("src\\bin", isDirectory: true));
    }
}
