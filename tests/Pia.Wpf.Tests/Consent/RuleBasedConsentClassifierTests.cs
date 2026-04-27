using Pia.Services.Consent;
using Xunit;

namespace Pia.Wpf.Tests.Consent;

public sealed class RuleBasedConsentClassifierTests
{
    private readonly IConsentClassifier _sut = new RuleBasedConsentClassifier();

    [Theory]
    [InlineData("ja")]
    [InlineData("Ja, gerne.")]
    [InlineData("einverstanden")]
    [InlineData("kein Problem")]
    [InlineData("yes")]
    [InlineData("sure, go ahead")]
    public void GrantPhrases_ReturnGrantWithHighConfidence(string text)
    {
        var result = _sut.Classify(text);
        Assert.Equal(ConsentDecision.Grant, result.Decision);
        Assert.True(result.Confidence >= 0.9f, $"confidence was {result.Confidence}");
    }

    [Theory]
    [InlineData("nein")]
    [InlineData("nicht einverstanden")]
    [InlineData("auf keinen Fall")]
    [InlineData("no")]
    [InlineData("absolutely not")]
    public void DenyPhrases_ReturnDenyWithHighConfidence(string text)
    {
        var result = _sut.Classify(text);
        Assert.Equal(ConsentDecision.Deny, result.Decision);
        Assert.True(result.Confidence >= 0.9f);
    }

    [Theory]
    [InlineData("vielleicht")]
    [InlineData("ich weiß nicht")]
    [InlineData("warum genau?")]
    [InlineData("was meinen Sie damit")]
    public void AmbiguousPhrases_ReturnAmbiguous(string text)
    {
        var result = _sut.Classify(text);
        Assert.Equal(ConsentDecision.Ambiguous, result.Decision);
    }

    [Fact]
    public void EmptyInput_ReturnsAmbiguousWithLowConfidence()
    {
        var result = _sut.Classify("");
        Assert.Equal(ConsentDecision.Ambiguous, result.Decision);
        Assert.True(result.Confidence < 0.5f);
    }

    [Fact]
    public void GrantAndDenyTogether_ReturnsAmbiguous()
    {
        var result = _sut.Classify("ja aber nein eigentlich");
        Assert.Equal(ConsentDecision.Ambiguous, result.Decision);
    }
}
