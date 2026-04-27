using System.Globalization;
using Pia.Converters;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Converters;

public class SpeakerToDisplayNameConverterTests
{
    [Fact]
    public void You_ReturnsLiteralYou_RegardlessOfLabel()
    {
        var sut = new SpeakerToDisplayNameConverter();
        var result = sut.Convert(
            new object[] { TranscriptSpeaker.You, "Speaker 1" },
            typeof(string),
            null,
            CultureInfo.InvariantCulture);
        Assert.Equal("you", result);
    }

    [Fact]
    public void Them_ReturnsSpeakerLabel_WhenSet()
    {
        var sut = new SpeakerToDisplayNameConverter();
        var result = sut.Convert(
            new object[] { TranscriptSpeaker.Them, "Speaker 2" },
            typeof(string),
            null,
            CultureInfo.InvariantCulture);
        Assert.Equal("Speaker 2", result);
    }

    [Fact]
    public void Them_NullOrWhitespaceLabel_ReturnsGenericFallback()
    {
        var sut = new SpeakerToDisplayNameConverter();
        var resultNull = sut.Convert(
            new object[] { TranscriptSpeaker.Them, null! },
            typeof(string),
            null,
            CultureInfo.InvariantCulture);
        var resultEmpty = sut.Convert(
            new object[] { TranscriptSpeaker.Them, "  " },
            typeof(string),
            null,
            CultureInfo.InvariantCulture);
        Assert.Equal("Speaker", resultNull);
        Assert.Equal("Speaker", resultEmpty);
    }
}
