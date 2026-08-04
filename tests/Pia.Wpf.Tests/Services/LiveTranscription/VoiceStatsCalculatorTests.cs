using Pia.Models;
using Pia.Services.LiveTranscription;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

/// <summary>
/// Exercises the pure aggregation in <see cref="VoiceStatsCalculator"/>. All samples here are
/// synthesised (no real audio), so these measure the arithmetic and grouping/ordering rules only.
/// </summary>
public class VoiceStatsCalculatorTests
{
    [Fact]
    public void Compute_EmptyInput_ReturnsEmptyList()
    {
        var result = VoiceStatsCalculator.Compute(Array.Empty<VoiceSample>());
        Assert.Empty(result);
    }

    [Fact]
    public void Compute_SingleSpeaker_ShareIsOne()
    {
        var samples = new[]
        {
            new VoiceSample(TranscriptSpeaker.Them, "Speaker 1", 5.0),
            new VoiceSample(TranscriptSpeaker.Them, "Speaker 1", 3.0),
        };

        var result = VoiceStatsCalculator.Compute(samples);

        Assert.Single(result);
        var stat = result[0];
        Assert.Equal(2, stat.UtteranceCount);
        Assert.Equal(8.0, stat.TotalSpeechSeconds, precision: 6);
        Assert.Equal(4.0, stat.MeanUtteranceSeconds, precision: 6);
        Assert.Equal(1.0, stat.ShareOfMeasuredSpeech, precision: 6);
    }

    [Fact]
    public void Compute_TwoSpeakers_ComputesSharesAndMeans()
    {
        var samples = new[]
        {
            new VoiceSample(TranscriptSpeaker.You, null, 20.0),
            new VoiceSample(TranscriptSpeaker.You, null, 10.0),
            new VoiceSample(TranscriptSpeaker.Them, "Speaker 1", 10.0),
        };

        var result = VoiceStatsCalculator.Compute(samples);

        Assert.Equal(2, result.Count);

        var me = result.Single(s => s.Speaker == TranscriptSpeaker.You);
        Assert.Equal(2, me.UtteranceCount);
        Assert.Equal(30.0, me.TotalSpeechSeconds, precision: 6);
        Assert.Equal(15.0, me.MeanUtteranceSeconds, precision: 6);
        Assert.Equal(0.75, me.ShareOfMeasuredSpeech, precision: 6);

        var them = result.Single(s => s.Speaker == TranscriptSpeaker.Them);
        Assert.Equal(1, them.UtteranceCount);
        Assert.Equal(10.0, them.TotalSpeechSeconds, precision: 6);
        Assert.Equal(10.0, them.MeanUtteranceSeconds, precision: 6);
        Assert.Equal(0.25, them.ShareOfMeasuredSpeech, precision: 6);
    }

    [Fact]
    public void Compute_AllZeroDurations_ShareIsZero_NoNaN()
    {
        var samples = new[]
        {
            new VoiceSample(TranscriptSpeaker.Them, "Speaker 1", 0.0),
            new VoiceSample(TranscriptSpeaker.Them, "Speaker 1", 0.0),
        };

        var result = VoiceStatsCalculator.Compute(samples);

        Assert.Single(result);
        var stat = result[0];
        Assert.Equal(2, stat.UtteranceCount);
        Assert.Equal(0.0, stat.TotalSpeechSeconds);
        Assert.Equal(0.0, stat.MeanUtteranceSeconds);
        Assert.Equal(0.0, stat.ShareOfMeasuredSpeech);
        Assert.False(double.IsNaN(stat.ShareOfMeasuredSpeech), "non-vacuity: share must never be NaN when total speech is zero");
        Assert.False(double.IsNaN(stat.MeanUtteranceSeconds), "non-vacuity: mean must never be NaN when total speech is zero");
    }

    [Fact]
    public void Compute_NullAndEmptyLabel_GroupTogether()
    {
        var samples = new[]
        {
            new VoiceSample(TranscriptSpeaker.Them, null, 4.0),
            new VoiceSample(TranscriptSpeaker.Them, "", 6.0),
        };

        var result = VoiceStatsCalculator.Compute(samples);

        Assert.Single(result);
        Assert.Equal(2, result[0].UtteranceCount);
        Assert.Equal(10.0, result[0].TotalSpeechSeconds, precision: 6);
    }

    [Fact]
    public void Compute_MicAndLoopbackLabel_AreSeparateGroups()
    {
        var samples = new[]
        {
            new VoiceSample(TranscriptSpeaker.You, null, 5.0),
            new VoiceSample(TranscriptSpeaker.Them, "Speaker 1", 5.0),
        };

        var result = VoiceStatsCalculator.Compute(samples);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, s => s.Speaker == TranscriptSpeaker.You && s.SpeakerLabel is null);
        Assert.Contains(result, s => s.Speaker == TranscriptSpeaker.Them && s.SpeakerLabel == "Speaker 1");
    }

    [Fact]
    public void Compute_Ordering_IsByTotalDescendingThenLabelOrdinal()
    {
        var samples = new[]
        {
            new VoiceSample(TranscriptSpeaker.Them, "Speaker 2", 5.0),
            new VoiceSample(TranscriptSpeaker.Them, "Speaker 1", 5.0),
            new VoiceSample(TranscriptSpeaker.Them, "Speaker 3", 20.0),
        };

        var result = VoiceStatsCalculator.Compute(samples);

        Assert.Equal(3, result.Count);
        Assert.Equal("Speaker 3", result[0].SpeakerLabel); // highest total first
        Assert.Equal("Speaker 1", result[1].SpeakerLabel); // tie on total, ordinal label order
        Assert.Equal("Speaker 2", result[2].SpeakerLabel);
    }

    [Fact]
    public void Compute_NegativeDuration_ClampedToZero()
    {
        // A negative duration should never occur in practice; the calculator defends against it
        // rather than letting a bad sample corrupt totals/shares with a negative number.
        var samples = new[]
        {
            new VoiceSample(TranscriptSpeaker.Them, "Speaker 1", -5.0),
            new VoiceSample(TranscriptSpeaker.Them, "Speaker 1", 10.0),
        };

        var result = VoiceStatsCalculator.Compute(samples);

        Assert.Single(result);
        Assert.Equal(10.0, result[0].TotalSpeechSeconds, precision: 6);
        Assert.True(result[0].TotalSpeechSeconds >= 0, "non-vacuity: total speech seconds must never go negative");
    }
}
