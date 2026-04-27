using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services.LiveTranscription;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

/// <summary>
/// Integration tests for SpeakerIdentificationService. Skipped unless the speaker-embedding
/// model has been pre-downloaded.
/// </summary>
public class SpeakerIdentificationServiceTests
{
    private static bool ModelMissing => !LiveTranscriptionModels.IsSpeakerEmbeddingAvailable();

    private static SpeakerIdentificationService CreateSut()
        => new(LiveTranscriptionModels.SpeakerEmbeddingModelPath, matchThreshold: 0.70f, NullLogger.Instance);

    [Fact]
    public void SameAudioTwice_ReturnsSameLabel()
    {
        if (ModelMissing) return;
        using var sut = CreateSut();

        var audio = SyntheticSpeech(seed: 1, seconds: 1.5f);
        var first = sut.IdentifyOrRegister(audio, sampleRate: 16000);
        var second = sut.IdentifyOrRegister(audio, sampleRate: 16000);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Reset_RestartsTheSpeakerCounter()
    {
        if (ModelMissing) return;
        using var sut = CreateSut();

        var label1 = sut.IdentifyOrRegister(SyntheticSpeech(seed: 1, seconds: 1.5f), 16000);
        sut.Reset();
        var label2 = sut.IdentifyOrRegister(SyntheticSpeech(seed: 2, seconds: 1.5f), 16000);

        // First label after reset must be "Speaker 1" again.
        Assert.Equal(label1, label2);
    }

    [Fact]
    public void Rename_UpdatesFutureLabels()
    {
        if (ModelMissing) return;
        using var sut = CreateSut();

        var audio = SyntheticSpeech(seed: 7, seconds: 1.5f);
        var original = sut.IdentifyOrRegister(audio, 16000);

        Assert.True(sut.Rename(original, "Marco"));
        var afterRename = sut.IdentifyOrRegister(audio, 16000);
        Assert.Equal("Marco", afterRename);
    }

    [Fact]
    public void Rename_UnknownLabel_ReturnsFalse()
    {
        if (ModelMissing) return;
        using var sut = CreateSut();
        Assert.False(sut.Rename("Speaker 99", "Bogus"));
    }

    /// <summary>
    /// Generates pseudo-speech 16 kHz mono Float32 audio. We blend several harmonics with
    /// pseudo-random envelope modulation so the embedding extractor produces a plausible
    /// speaker-like vector rather than degenerating to zero.
    /// </summary>
    private static float[] SyntheticSpeech(int seed, float seconds)
    {
        const int sr = 16000;
        var rng = new Random(seed);
        int n = (int)(sr * seconds);
        var f0 = 110.0 + rng.NextDouble() * 80;
        var samples = new float[n];
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)sr;
            double s = 0.6 * Math.Sin(2 * Math.PI * f0 * t)
                     + 0.3 * Math.Sin(2 * Math.PI * 2 * f0 * t)
                     + 0.15 * Math.Sin(2 * Math.PI * 3 * f0 * t);
            // Slow envelope so the signal isn't a pure tone.
            double env = 0.7 + 0.3 * Math.Sin(2 * Math.PI * 4 * t);
            samples[i] = (float)(s * env * 0.4);
        }
        return samples;
    }
}
