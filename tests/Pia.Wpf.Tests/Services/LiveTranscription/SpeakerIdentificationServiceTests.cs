using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services.LiveTranscription;
using Xunit;
using static Pia.Tests.Services.LiveTranscription.SpeakerSegments;

namespace Pia.Tests.Services.LiveTranscription;

/// <summary>A segment below the match threshold still gets its nearest label, and says so, so a consent gate
/// can refuse to treat it as that speaker.</summary>
public sealed class SpeakerIdentificationServiceTests
{
    private static SpeakerIdentificationService Create(int maxSpeakers = 0) =>
        new(new DegreeEmbeddingExtractor(), matchThreshold: 0.50f, maxSpeakers,
            NullLogger<SpeakerIdentificationService>.Instance);

    [Fact]
    public void AMatchAboveTheThreshold_IsNotBelowIt()
    {
        using var sut = Create();
        sut.IdentifyOrRegisterSegment(Seg(0), 16000);

        var match = sut.IdentifyOrRegisterSegment(Seg(25), 16000); // cos 25° ≈ 0.91

        Assert.Equal("Speaker 1", match.Label);
        Assert.False(match.BelowMatchThreshold);
    }

    [Fact]
    public void AMatchInTheBorderlineBand_KeepsTheNearestLabelButIsBelowTheThreshold()
    {
        using var sut = Create();
        sut.IdentifyOrRegisterSegment(Seg(0), 16000);

        var borderline = sut.IdentifyOrRegisterSegment(Seg(62), 16000); // cos 62° ≈ 0.47

        Assert.Equal("Speaker 1", borderline.Label);
        Assert.True(borderline.BelowMatchThreshold);
    }

    [Fact]
    public void AMatchForcedAtTheSpeakerCap_IsBelowTheThreshold()
    {
        using var sut = Create(maxSpeakers: 1);
        sut.IdentifyOrRegisterSegment(Seg(0), 16000);

        var forced = sut.IdentifyOrRegisterSegment(Seg(90), 16000);

        Assert.Equal("Speaker 1", forced.Label);
        Assert.True(forced.BelowMatchThreshold);
    }

    [Fact]
    public void ANewSpeaker_IsNotBelowTheThreshold()
    {
        using var sut = Create();
        sut.IdentifyOrRegisterSegment(Seg(0), 16000);

        var fresh = sut.IdentifyOrRegisterSegment(Seg(90), 16000);

        Assert.Equal("Speaker 2", fresh.Label);
        Assert.False(fresh.BelowMatchThreshold);
    }
}
