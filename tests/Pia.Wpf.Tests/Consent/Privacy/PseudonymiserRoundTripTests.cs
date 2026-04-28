using Pia.Services.Consent.Privacy;
using Xunit;

namespace Pia.Wpf.Tests.Consent.Privacy;

public sealed class PseudonymiserRoundTripTests
{
    private readonly Pseudonymiser _sut = new(new RegexPiiDetector());

    [Fact]
    public void Apply_ReplacesPiiWithPlaceholders()
    {
        var map = new PseudonymisationMap();
        var result = _sut.Apply("Anna Schmidt schreibt an john@example.com.", map);
        Assert.DoesNotContain("Anna Schmidt", result);
        Assert.DoesNotContain("john@example.com", result);
        Assert.Contains("[NAME-1]", result);
        Assert.Contains("[EMAIL-1]", result);
    }

    [Fact]
    public void Apply_SameValueGetsSamePlaceholder()
    {
        var map = new PseudonymisationMap();
        var result = _sut.Apply("john@example.com told john@example.com.", map);
        Assert.Equal("[EMAIL-1] told [EMAIL-1].", result);
        Assert.Equal(1, map.Count);
    }

    [Fact]
    public void RoundTrip_RecoversOriginal()
    {
        var map = new PseudonymisationMap();
        const string original = "Anna Schmidt aus Hauptstraße 12 schreibt an john@example.com — IBAN DE89370400440532013000.";
        var pseudonymised = _sut.Apply(original, map);
        var reversed = _sut.Reverse(pseudonymised, map);
        Assert.Equal(original, reversed);
    }

    [Fact]
    public void Reverse_LeavesUnknownPlaceholdersIntact()
    {
        var map = new PseudonymisationMap();
        var result = _sut.Reverse("Got [NAME-7] from cloud", map);
        Assert.Equal("Got [NAME-7] from cloud", result);
    }

    [Fact]
    public void RoundTrip_OnEmptyText_IsNoOp()
    {
        var map = new PseudonymisationMap();
        Assert.Equal("", _sut.Apply("", map));
        Assert.Equal("", _sut.Reverse("", map));
    }
}
