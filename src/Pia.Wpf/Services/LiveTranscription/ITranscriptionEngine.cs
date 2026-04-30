namespace Pia.Services.LiveTranscription;

/// <summary>
/// Backend-agnostic recognizer over a single 16 kHz mono float[] segment.
/// </summary>
public interface ITranscriptionEngine : IAsyncDisposable
{
    Task<string> TranscribeAsync(float[] samples16kMono, CancellationToken cancellationToken);
}
