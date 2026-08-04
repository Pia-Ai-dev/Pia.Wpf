using System.Globalization;
using Pia.Converters;
using Pia.Localization;
using Pia.Models;
using Xunit;

namespace Pia.Tests.Converters;

/// <summary>
/// <see cref="SpeakerToDisplayNameConverter.Resolve"/> now returns the localized <c>Speaker_Me</c> /
/// <c>Speaker_Them</c> resources instead of the old hardcoded "you"/"them" literals (design §3.7). The
/// invariant-culture <see cref="LocalizationSource"/> falls back to the bracketed key
/// (<c>"[Speaker_Me]"</c>) when a resx entry does not exist, so these assertions hold whether or not the
/// integration phase has landed the new resx keys yet — they assert against whatever the shared
/// lookup returns, not a hardcoded English string.
/// </summary>
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
        // A non-null counterpart name ("Alex") and a null label are both supplied precisely to prove
        // the You branch returns before either is consulted — i.e. the "me" label is unconditional,
        // not a fallback.
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
