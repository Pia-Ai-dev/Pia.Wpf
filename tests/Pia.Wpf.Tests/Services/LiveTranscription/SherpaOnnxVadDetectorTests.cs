using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services.LiveTranscription;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

/// <summary>
/// Integration tests for the sherpa-onnx VAD wrapper. Skipped by default unless the Silero
/// model has been pre-downloaded into the standard cache directory by running the app at
/// least once or invoking the "Download Silero VAD" settings command.
/// </summary>
public class SherpaOnnxVadDetectorTests
{
    private static bool ModelMissing => !LiveTranscriptionModels.IsSileroVadAvailable();

    [Fact]
    public void Constructs_AndDisposes_WithoutThrowing()
    {
        if (ModelMissing) return; // skip — no model on this machine
        using var vad = new SherpaOnnxVadDetector(
            LiveTranscriptionModels.SileroVadModelPath,
            NullLogger.Instance);
        // Smoke: ctor must not throw when given a valid model path.
    }

    [Fact]
    public void Silence_DoesNotProduceSegments()
    {
        if (ModelMissing) return;
        using var vad = new SherpaOnnxVadDetector(
            LiveTranscriptionModels.SileroVadModelPath,
            NullLogger.Instance);

        var observed = new List<float[]>();
        vad.OnSegment += s => observed.Add(s);

        // 3 s of pure silence at 16 kHz mono.
        var silence = new float[16000 * 3];
        vad.Process(silence);
        vad.Drain();

        Assert.Empty(observed);
    }

    [Fact]
    public void WhiteNoise_BurstFollowedBySilence_ProducesAtLeastOneSegment()
    {
        if (ModelMissing) return;
        using var vad = new SherpaOnnxVadDetector(
            LiveTranscriptionModels.SileroVadModelPath,
            NullLogger.Instance);

        var observed = new List<float[]>();
        vad.OnSegment += s => observed.Add(s);

        // ~1 s of moderately-loud white noise (Silero treats noise like speech in this
        // amplitude range, which is enough to satisfy the speech-start threshold).
        var rng = new Random(42);
        var noise = new float[16000];
        for (int i = 0; i < noise.Length; i++) noise[i] = (float)(rng.NextDouble() * 2 - 1) * 0.4f;
        vad.Process(noise);

        // ~1 s of trailing silence to close the segment.
        vad.Process(new float[16000]);
        vad.Drain();

        Assert.NotEmpty(observed);
    }
}
