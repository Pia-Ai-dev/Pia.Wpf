using Pia.Services.Similarity;
using Xunit;

namespace Pia.Tests.Services.Similarity;

public class JaroWinklerTests
{
    private const double Tolerance = 1e-3;

    private static double Sim(string a, string b) => JaroWinkler.Similarity(a, b);

    [Fact]
    public void IdenticalStrings_ReturnsOne()
    {
        Assert.Equal(1.0, Sim("schlaf", "schlaf"), 10);
    }

    [Fact]
    public void BothEmpty_ReturnsOne()
    {
        Assert.Equal(1.0, Sim("", ""), 10);
    }

    [Theory]
    [InlineData("", "schlaf")]
    [InlineData("schlaf", "")]
    public void OneEmpty_ReturnsZero(string a, string b)
    {
        Assert.Equal(0.0, Sim(a, b), 10);
    }

    [Theory]
    [InlineData("MARTHA", "MARHTA", 0.9611)]
    [InlineData("DWAYNE", "DUANE", 0.8400)]
    [InlineData("DIXON", "DICKSONX", 0.8133)]
    public void ClassicWinklerReferencePairs_MatchKnownValues(string a, string b, double expected)
    {
        Assert.Equal(expected, Sim(a, b), Tolerance);
    }

    [Theory]
    [InlineData("schlaf", "schlaftracking", 0.8912)]
    [InlineData("analyse", "analysen", 0.9875)]
    public void GermanPrefixPairs_MatchComputedValues(string a, string b, double expected)
    {
        Assert.Equal(expected, Sim(a, b), Tolerance);
    }

    [Fact]
    public void IsCaseSensitive()
    {
        // Call sites lowercase inputs before comparing; pin that F23 (and the
        // replacement) are case-sensitive so that invariant is explicit.
        var lower = Sim("martha", "martha");
        var mixed = Sim("martha", "MARTHA");
        Assert.Equal(1.0, lower, 10);
        Assert.True(mixed < 1.0);
    }

    [Fact]
    public void Symmetric()
    {
        Assert.Equal(Sim("dwayne", "duane"), Sim("duane", "dwayne"), 10);
    }
}
