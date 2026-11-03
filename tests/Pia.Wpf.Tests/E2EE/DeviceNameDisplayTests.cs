namespace Pia.Tests.E2EE;

using System.Globalization;
using Pia.Services.E2EE;
using Xunit;

public class DeviceNameDisplayTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("DESKTOP-4711", "DESKTOP-4711")]
    [InlineData("Marco's Laptop", "Marco's Laptop")]
    [InlineData("  padded   name  ", "padded name")]
    [InlineData("Ger\u00e4t \u65e5\u672c", "Ger\u00e4t \u65e5\u672c")]
    public void Sanitize_KeepsPlainNames(string? raw, string expected)
    {
        var result = DeviceNameDisplay.Sanitize(raw);

        Assert.Equal(expected, result.Text);
        Assert.False(result.HadHiddenCharacters);
    }

    [Theory]
    [InlineData("Laptop\r\n\r\nFingerprint: ABCD-EF01-2345-6789", "Laptop Fingerprint: ABCD-EF01-2345-6789")]
    [InlineData("Laptop\u2028Fingerprint", "Laptop Fingerprint")]
    [InlineData("Laptop\tA", "Laptop A")]
    [InlineData("abc\u202Efdp.exe", "abcfdp.exe")]
    [InlineData("\u2066Lap\u200Btop\u2069\uFEFF", "Laptop")]
    [InlineData("Lap\u2060top\uE000", "Laptop")]
    public void Sanitize_StripsLineBreaksBidiAndInvisibleCharacters(string raw, string expected)
    {
        var result = DeviceNameDisplay.Sanitize(raw);

        Assert.Equal(expected, result.Text);
        Assert.True(result.HadHiddenCharacters);
    }

    [Fact]
    public void Sanitize_CapsStackedCombiningMarks()
    {
        var zalgo = "Z" + new string('\u0336', 40) + "a" + new string('\u0301', 40);

        var result = DeviceNameDisplay.Sanitize(zalgo);

        Assert.Equal("Z\u0336\u0336a\u0301\u0301", result.Text);
        Assert.True(result.HadHiddenCharacters);
    }

    [Fact]
    public void Sanitize_KeepsOrdinaryAccents()
    {
        var result = DeviceNameDisplay.Sanitize("Rene\u0301");

        Assert.Equal("Rene\u0301", result.Text);
        Assert.False(result.HadHiddenCharacters);
    }

    [Fact]
    public void Sanitize_TruncatesLongNamesWithEllipsis()
    {
        var result = DeviceNameDisplay.Sanitize(new string('W', 5000));

        Assert.Equal(new string('W', DeviceNameDisplay.MaxTextElements - 1) + "\u2026", result.Text);
        Assert.False(result.HadHiddenCharacters);
    }

    [Fact]
    public void Sanitize_TruncationNeverSplitsASurrogatePair()
    {
        var emoji = "\U0001F600";

        var result = DeviceNameDisplay.Sanitize(string.Concat(Enumerable.Repeat(emoji, 200)));

        var expected = string.Concat(Enumerable.Repeat(emoji, DeviceNameDisplay.MaxTextElements - 1)) + "\u2026";
        Assert.Equal(expected, result.Text);
        Assert.Equal(DeviceNameDisplay.MaxTextElements, new StringInfo(result.Text).LengthInTextElements);
    }

    [Fact]
    public void Sanitize_OnlyHiddenCharactersYieldsEmpty()
    {
        var result = DeviceNameDisplay.Sanitize("\u202E\u200B\r\n");

        Assert.Equal("", result.Text);
        Assert.True(result.HadHiddenCharacters);
    }
}
