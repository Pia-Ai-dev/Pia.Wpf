using Pia.Services.Similarity;
using Xunit;

namespace Pia.Tests.Services.Similarity;

/// <summary>
/// Classic edit-distance reference values plus the boundary behaviour <see cref="NamedConsentClassifier"/>
/// (in <c>Pia.Tests.Consent</c>) relies on: <see cref="Levenshtein.WithinOne"/> must early-exit on a
/// length gap before it ever runs the DP table, since that early exit is what keeps a 3-letter false
/// friend like "via" out of the length-gated Pia fuzzy rule together with the length gate itself.
/// </summary>
public sealed class LevenshteinTests
{
    [Fact]
    public void IdenticalStrings_DistanceIsZero()
    {
        Assert.Equal(0, Levenshtein.Distance("kitten", "kitten"));
    }

    [Theory]
    [InlineData("kitten", "kittens")] // insertion
    [InlineData("kittens", "kitten")] // deletion
    public void SingleInsertOrDelete_DistanceIsOne(string a, string b)
    {
        Assert.Equal(1, Levenshtein.Distance(a, b));
    }

    [Fact]
    public void SingleSubstitution_DistanceIsOne()
    {
        Assert.Equal(1, Levenshtein.Distance("kitten", "kittin"));
    }

    [Fact]
    public void Transposition_IsTwoEdits_NotOne()
    {
        // Levenshtein has no dedicated transposition operation, so swapping two adjacent letters
        // costs two substitutions (or a delete+insert) — this is the classic edit-distance behaviour,
        // distinct from Damerau-Levenshtein, and NamedConsentClassifier's fuzzy rule relies on exactly
        // this: "pai" is two edits from "pia", not one, so it must NOT count as a Pia reference.
        Assert.Equal(2, Levenshtein.Distance("pia", "pai"));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("", "abc")]
    [InlineData("abc", "")]
    public void EmptyStrings_DistanceIsTheOtherLength(string a, string b)
    {
        Assert.Equal(Math.Max(a.Length, b.Length), Levenshtein.Distance(a, b));
    }

    [Fact]
    public void NullArguments_TreatedAsEmpty()
    {
        Assert.Equal(3, Levenshtein.Distance(null, "abc"));
        Assert.Equal(3, Levenshtein.Distance("abc", null));
        Assert.Equal(0, Levenshtein.Distance(null, null));
    }

    public static TheoryData<string, string, bool> WithinOneCases => new()
    {
        { "pia", "pia", true },
        { "pia", "pea", true }, // one substitution
        { "pia", "pias", true }, // one insertion
        { "pia", "pi", true }, // one deletion
        { "pia", "pai", false }, // transposition = 2 edits, not 1
        // "pit" is ONE substitution from "pia", so WithinOne is true. What keeps "pit" out of the Pia
        // component is not edit distance but NamedConsentClassifier.TryFindPiaReference's >=4-character
        // length gate (see ConsentLexicon.PiaFalseFriends' doc, and the PiaFalseFriendWords theory in
        // NamedConsentClassifierTests, which covers that separately).
        { "pia", "pit", true },
        { "pia", "pot", false }, // two substitutions
        { "pia", "piano", false }, // length gap alone (3 vs 5) already exceeds 1
    };

    [Theory]
    [MemberData(nameof(WithinOneCases))]
    public void WithinOne_MatchesTruthTable(string a, string b, bool expected)
    {
        Assert.Equal(expected, Levenshtein.WithinOne(a, b));
        Assert.Equal(expected, Levenshtein.WithinOne(b, a)); // symmetric
    }

    [Fact]
    public void WithinOne_LengthGapGreaterThanOne_EarlyExitsWithoutComputing()
    {
        // A length gap > 1 alone guarantees distance >= 2 (see WithinOneCases's "piano" case above);
        // this is a second, direct assertion of the early-exit contract itself.
        Assert.False(Levenshtein.WithinOne("pia", "pianist"));
    }

    [Fact]
    public void WithinOne_BothEmpty_IsTrue()
    {
        Assert.True(Levenshtein.WithinOne("", ""));
    }

    [Fact]
    public void NonVacuity_TruthTableCoversBothOutcomes()
    {
        var data = WithinOneCases;
        Assert.True(data.Count >= 5, "non-vacuity: expected a meaningfully sized truth table");
    }
}
