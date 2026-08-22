using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services.LiveTranscription;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

/// <summary>The attribution fixture scores segments at their stream position, so an offset that is
/// merely plausible is worse than none — these pin it exactly.</summary>
public class SileroVadDetectorStreamPositionTests
{
    private const int WindowSize = 512;

    [Fact]
    public void StartSample_IsZero_WhenSpeechOpensOnTheFirstWindow()
    {
        var sut = new SileroVadDetector(modelPath: string.Empty, NullLogger.Instance);
        var segments = Capture(sut);

        sut.Process(Loud(32));
        sut.Process(Silent(32));

        Assert.Equal(0L, Assert.Single(segments).StartSample);
    }

    [Fact]
    public void StartSample_ReachesBackOverThePreroll()
    {
        var sut = new SileroVadDetector(modelPath: string.Empty, NullLogger.Instance);
        var segments = Capture(sut);

        // 20 windows of silence, so the 16-window preroll no longer reaches sample 0.
        sut.Process(Silent(20));
        sut.Process(Loud(32));
        sut.Process(Silent(32));

        Assert.Equal((20 - 16) * (long)WindowSize, Assert.Single(segments).StartSample);
    }

    [Fact]
    public void ConsecutiveSegments_AreContiguousInTheStream()
    {
        var sut = new SileroVadDetector(modelPath: string.Empty, NullLogger.Instance);
        var segments = Capture(sut);

        sut.Process(Loud(32));
        sut.Process(Silent(32));
        sut.Process(Loud(32));
        sut.Process(Silent(32));

        Assert.Equal(2, segments.Count);
        Assert.Equal(
            segments[0].StartSample + segments[0].Samples.Length,
            segments[1].StartSample);
    }

    [Fact]
    public void StartSample_AdvancesAcrossTheMaxSegmentFlush()
    {
        var sut = new SileroVadDetector(modelPath: string.Empty, NullLogger.Instance);
        var segments = Capture(sut);

        // Past the 20 s cap in one go, so the first segment closes on the cap rather than on silence.
        sut.Process(Loud(700));
        sut.Process(Silent(32));

        Assert.Equal(2, segments.Count);
        Assert.Equal(0L, segments[0].StartSample);
        Assert.Equal(20 * 16000L, segments[0].Samples.Length);
        Assert.Equal(20 * 16000L, segments[1].StartSample);
    }

    [Fact]
    public void Drain_ReportsTheStartOfTheTrailingSegment()
    {
        var sut = new SileroVadDetector(modelPath: string.Empty, NullLogger.Instance);
        var segments = Capture(sut);

        sut.Process(Silent(20));
        sut.Process(Loud(32));
        sut.Drain();

        Assert.Equal((20 - 16) * (long)WindowSize, Assert.Single(segments).StartSample);
    }

    private static List<VadSegment> Capture(SileroVadDetector sut)
    {
        var segments = new List<VadSegment>();
        sut.OnSegment += segments.Add;
        return segments;
    }

    private static float[] Loud(int windows)
    {
        // 0.3 amplitude sine is about -10 dBFS RMS, well above the -35 dBFS speech threshold.
        var samples = new float[WindowSize * windows];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = 0.3f * MathF.Sin(2f * MathF.PI * 440f * i / 16000f);
        return samples;
    }

    private static float[] Silent(int windows) => new float[WindowSize * windows];
}
