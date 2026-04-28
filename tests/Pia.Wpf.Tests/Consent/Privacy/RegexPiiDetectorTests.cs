using Pia.Services.Consent.Privacy;
using Xunit;

namespace Pia.Wpf.Tests.Consent.Privacy;

public sealed class RegexPiiDetectorTests
{
    private readonly RegexPiiDetector _sut = new();

    [Theory]
    [InlineData("Reach me at john.doe@example.com please.", PiiType.Email, "john.doe@example.com")]
    [InlineData("IBAN: DE89370400440532013000", PiiType.Iban, "DE89370400440532013000")]
    [InlineData("Call +49 30 1234567 today.", PiiType.Phone, "+49 30 1234567")]
    [InlineData("Lieferung an Hauptstraße 12 erfolgt morgen.", PiiType.Address, "Hauptstraße 12")]
    [InlineData("Karte 4111 1111 1111 1111 wurde belastet.", PiiType.CreditCard, "4111 1111 1111 1111")]
    [InlineData("Anna Schmidt nimmt teil.", PiiType.Name, "Anna Schmidt")]
    public void Detect_FindsPii(string text, PiiType expectedType, string expectedValue)
    {
        var spans = _sut.Detect(text);
        Assert.Contains(spans, s => s.Type == expectedType && s.Value.Trim() == expectedValue);
    }

    [Theory]
    [InlineData("The quick brown fox jumps over the lazy dog.")]
    [InlineData("Wir treffen uns um zehn Uhr.")]
    [InlineData("0123")]
    public void Detect_NoFalsePositives_OnInnocuousText(string text)
    {
        var spans = _sut.Detect(text);
        Assert.Empty(spans);
    }

    [Fact]
    public void Detect_RejectsInvalidLuhn()
    {
        var spans = _sut.Detect("Karte 1234 5678 9012 3456 ungültig.");
        Assert.DoesNotContain(spans, s => s.Type == PiiType.CreditCard);
    }

    [Fact]
    public void Detect_OverlappingSpans_ResolvedDeterministically()
    {
        var text = "Mail anna.schmidt@example.com bitte.";
        var spans = _sut.Detect(text);
        Assert.Single(spans);
        Assert.Equal(PiiType.Email, spans[0].Type);
    }
}
