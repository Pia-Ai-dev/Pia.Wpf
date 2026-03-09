using Pia.Models;

namespace Pia.Services.Interfaces;

public interface ITtsService
{
    bool IsReady { get; }
    bool IsPlaying { get; }
    bool HasVoiceLoaded { get; }
    Task SpeakAsync(string text, CancellationToken cancellationToken = default);
    void Stop();
    Task InitializeAsync(IProgress<TtsDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TtsVoice>> GetAvailableVoicesAsync(CancellationToken cancellationToken = default);
    Task DownloadVoiceAsync(string voiceKey, IProgress<TtsDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task SetVoiceAsync(string voiceKey, CancellationToken cancellationToken = default);
    event EventHandler<bool>? IsPlayingChanged;

    // Filler phrases for voice mode
    bool HasFillers { get; }
    Task PreGenerateFillersAsync(CancellationToken cancellationToken = default);
    Task PlayFillerAsync(CancellationToken cancellationToken = default);

    // Sentence-chunked playback for streaming TTS
    Task SpeakChunkedAsync(IAsyncEnumerable<string> sentences, CancellationToken cancellationToken = default);
}

public record TtsDownloadProgress(string Stage, int PercentComplete);
