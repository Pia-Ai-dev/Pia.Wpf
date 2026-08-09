using System.Globalization;
using Pia.Converters;
using Pia.Localization;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Converters;

/// <summary>Asserted against the shared lookup, not a hardcoded English string: <c>LocalizationSource</c>
/// falls back to the bracketed key when a resx entry is missing.</summary>
public class SpeakerToDisplayNameConverterTests
{
    [Fact]
    public void You_ReturnsTheLocalizedMeLabel_RegardlessOfCounterpart()
    {
        var sut = new SpeakerToDisplayNameConverter();
        var result = sut.Convert(
            new object[] { TranscriptSpeaker.You, null!, "Alex" },
            typeof(string),
            null,
            CultureInfo.InvariantCulture);
        // A counterpart name and a null label are both supplied to prove the You branch returns before
        // either is consulted.
        Assert.Equal(LocalizationSource.Instance["Speaker_Me"], result);
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
    public void Them_NullOrWhitespaceCounterpart_ReturnsTheLocalizedThemFallback()
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
        Assert.Equal(LocalizationSource.Instance["Speaker_Them"], resultNull);
        Assert.Equal(LocalizationSource.Instance["Speaker_Them"], resultEmpty);
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
