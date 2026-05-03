using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services.LiveTranscription;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

public class SileroVadDetectorSpeechEventsTests
{
    [Fact]
    public void OnSpeechStarted_FiresOnce_AtSegmentOpen()
    {
        var sut = new SileroVadDetector(modelPath: string.Empty, NullLogger.Instance);
        int starts = 0;
        sut.OnSpeechStarted += () => starts++;

        // Push enough loud audio to open a segment. The detector consumes 512-sample windows
        // and opens on the first window above the energy threshold.
        sut.Process(BuildLoudWindow(WindowsCount: 4));

        Assert.Equal(1, starts);
    }

    [Fact]
    public void OnSpeechEnded_FiresOnce_AfterSilenceClosesSegment()
    {
        var sut = new SileroVadDetector(modelPath: string.Empty, NullLogger.Instance);
        int starts = 0, ends = 0;
        sut.OnSpeechStarted += () => starts++;
        sut.OnSpeechEnded += () => ends++;

        // Open a segment, then run enough silence to satisfy the close hysteresis (16 windows).
        sut.Process(BuildLoudWindow(WindowsCount: 32));   // ~1 s of loud audio (well above 0.5 s min)
        sut.Process(BuildSilentWindow(WindowsCount: 32)); // ~1 s of silence — > 16 windows = close

        Assert.Equal(1, starts);
        Assert.Equal(1, ends);
    }

    [Fact]
    public void Drain_FiresOnSpeechEnded_WhenOpenSegmentIsTooShortToFlush()
    {
        var sut = new SileroVadDetector(modelPath: string.Empty, NullLogger.Instance);
        int ends = 0;
        sut.OnSpeechEnded += () => ends++;

        // Open a segment with not enough audio to meet MinSegmentSamples (8000 = 0.5 s).
        // 4 loud windows = 2048 samples, comfortably below the threshold.
        sut.Process(BuildLoudWindow(WindowsCount: 4));
        sut.Drain();

        Assert.Equal(1, ends);
    }

    [Fact]
    public void Drain_DoesNotFireOnSpeechEnded_WhenNoSegmentWasOpen()
    {
        var sut = new SileroVadDetector(modelPath: string.Empty, NullLogger.Instance);
        int ends = 0;
        sut.OnSpeechEnded += () => ends++;

        sut.Process(BuildSilentWindow(WindowsCount: 4));
        sut.Drain();

        Assert.Equal(0, ends);
    }

    private static float[] BuildLoudWindow(int WindowsCount)
    {
        // 0.3 amplitude sine ≈ -10 dBFS RMS — well above the -35 dBFS speech threshold.
        const int WindowSize = 512;
        var samples = new float[WindowSize * WindowsCount];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = 0.3f * MathF.Sin(2f * MathF.PI * 440f * i / 16000f);
        return samples;
    }

    private static float[] BuildSilentWindow(int WindowsCount)
    {
        const int WindowSize = 512;
        return new float[WindowSize * WindowsCount];
    }
}
