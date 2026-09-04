using Pia.Services.LiveTranscription;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

/// <summary>
/// Whisper and Parakeet must NOT offer the streaming contract — the consumer feature-detects on it,
/// and a false positive would feed audio into an engine that cannot produce partials.
/// </summary>
public class StreamingEngineContractTests
{
    [Fact]
    public void Only_the_nemotron_engine_advertises_streaming()
    {
        Assert.True(typeof(IStreamingTranscriptionEngine).IsAssignableFrom(typeof(NemotronStreamingEngine)));
        Assert.False(typeof(IStreamingTranscriptionEngine).IsAssignableFrom(typeof(WhisperSherpaEngine)));
        Assert.False(typeof(IStreamingTranscriptionEngine).IsAssignableFrom(typeof(ParakeetSherpaEngine)));
    }

    [Fact]
    public void A_streaming_session_is_disposable_so_the_native_stream_is_released()
    {
        Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(IStreamingSession)));
    }

    /// <summary>
    /// Reset clears the encoder cache, so the next utterance's first frames would arrive cold —
    /// the same state that cost "Alles" on the bundle's de.wav. The session re-warms with exactly
    /// one chunk of silence, matching the segment-final path's lead-in.
    /// </summary>
    [Fact]
    public void The_post_reset_warmup_is_one_chunk_of_silence()
    {
        var warmup = NemotronStreamingEngine.CacheWarmupSilence();

        Assert.Equal(16000 * 560 / 1000, warmup.Length);
        Assert.All(warmup, v => Assert.Equal(0f, v));
    }
}
