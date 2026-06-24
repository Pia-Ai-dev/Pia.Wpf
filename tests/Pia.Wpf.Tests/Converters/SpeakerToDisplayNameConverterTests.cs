using System.Globalization;
using Pia.Converters;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Converters;

public class SpeakerToDisplayNameConverterTests
{
    [Fact]
    public void You_ReturnsLiteralYou_RegardlessOfCounterpart()
    {
        var sut = new SpeakerToDisplayNameConverter();
        var result = sut.Convert(
            new object[] { TranscriptSpeaker.You, null!, "Alex" },
            typeof(string),
            null,
            CultureInfo.InvariantCulture);
        Assert.Equal("you", result);
    }

    [Fact]
    public void Them_ReturnsCounterpart()
    {
        var sut = new SpeakerToDisplayNameConverter();
        var result = sut.Convert(
            new object[] { TranscriptSpeaker.Them, null!, "Alex" },
            typeof(string),
            null,
            CultureInfo.InvariantCulture);
        Assert.Equal("Alex", result);
    }

    [Fact]
    public void Them_NullOrWhitespaceCounterpart_ReturnsThemFallback()
    {
        var sut = new SpeakerToDisplayNameConverter();
        var resultNull = sut.Convert(
            new object[] { TranscriptSpeaker.Them, null!, null! },
            typeof(string),
            null,
            CultureInfo.InvariantCulture);
        var resultEmpty = sut.Convert(
            new object[] { TranscriptSpeaker.Them, null!, "  " },
            typeof(string),
            null,
            CultureInfo.InvariantCulture);
        Assert.Equal("them", resultNull);
        Assert.Equal("them", resultEmpty);
    }

    [Fact]
    public void Them_SpeakerLabel_Wins()
    {
        var sut = new SpeakerToDisplayNameConverter();
        var result = sut.Convert(
            new object[] { TranscriptSpeaker.Them, "Speaker 2", null! },
            typeof(string),
            null,
            CultureInfo.InvariantCulture);
        Assert.Equal("Speaker 2", result);
    }

    [Fact]
    public void Them_SpeakerLabel_TakesPrecedenceOverCounterpart()
    {
        var sut = new SpeakerToDisplayNameConverter();
        var result = sut.Convert(
            new object[] { TranscriptSpeaker.Them, "Speaker 2", "Alex" },
            typeof(string),
            null,
            CultureInfo.InvariantCulture);
        Assert.Equal("Speaker 2", result);
    }
}
