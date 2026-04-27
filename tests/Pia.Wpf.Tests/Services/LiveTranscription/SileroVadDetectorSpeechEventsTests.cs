using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services.LiveTranscription;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

public class SileroVadDetectorSpeechEventsTests
{
    [Fact]
    public void OnSpeechStarted_FiresOnce_AtSegmentOpen()
    {
        var sut = new SileroVadDetector(NullLogger.Instance);
        int starts = 0;
        sut.OnSpeechStarted += () => starts++;

        // Push enough loud audio to open a segment. The detector consumes 512-sample windows
        // and opens on the first window above the energy threshold.
        FeedWindows(sut, BuildLoudWindow(WindowsCount: 4));

        Assert.Equal(1, starts);
    }

    [Fact]
    public void OnSpeechEnded_FiresOnce_AfterSilenceClosesSegment()
    {
        var sut = new SileroVadDetector(NullLogger.Instance);
        int starts = 0, ends = 0;
        sut.OnSpeechStarted += () => starts++;
        sut.OnSpeechEnded += () => ends++;

        // Open a segment, then run enough silence to satisfy the close hysteresis (16 windows).
        FeedWindows(sut, BuildLoudWindow(WindowsCount: 32));   // ~1 s of loud audio (well above 0.5 s min)
        FeedWindows(sut, BuildSilentWindow(WindowsCount: 32)); // ~1 s of silence — > 16 windows = close

        Assert.Equal(1, starts);
        Assert.Equal(1, ends);
    }

    [Fact]
    public void Drain_FiresOnSpeechEnded_WhenOpenSegmentIsTooShortToFlush()
    {
        var sut = new SileroVadDetector(NullLogger.Instance);
        int ends = 0;
        sut.OnSpeechEnded += () => ends++;

        // Open a segment with not enough audio to meet MinSegmentSamples (8000 = 0.5 s).
        // 4 loud windows = 2048 samples, comfortably below the threshold.
        FeedWindows(sut, BuildLoudWindow(WindowsCount: 4));
        sut.Drain();

        Assert.Equal(1, ends);
    }

    [Fact]
    public void Drain_DoesNotFireOnSpeechEnded_WhenNoSegmentWasOpen()
    {
        var sut = new SileroVadDetector(NullLogger.Instance);
        int ends = 0;
        sut.OnSpeechEnded += () => ends++;

        FeedWindows(sut, BuildSilentWindow(WindowsCount: 4));
        sut.Drain();

        Assert.Equal(0, ends);
    }

    // The detector's pre-windowing ring buffer is sized for production hops (~50 ms @ 16 kHz);
    // the tests synthesise multi-second buffers up front, so feed them in window-sized chunks.
    private static void FeedWindows(SileroVadDetector sut, float[] samples)
    {
        const int WindowSize = 512;
        for (int offset = 0; offset < samples.Length; offset += WindowSize)
        {
            var len = Math.Min(WindowSize, samples.Length - offset);
            sut.Process(samples.AsSpan(offset, len));
        }
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
