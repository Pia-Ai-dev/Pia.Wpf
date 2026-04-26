namespace Pia.Services.LiveTranscription;

/// <summary>
/// Backend-agnostic recognizer over a single 16 kHz mono float[] segment.
/// One instance is shared across mic + loopback engines for the lifetime of a meeting.
/// </summary>
public interface ITranscriptionEngine : IAsyncDisposable
{
    Task<string> TranscribeAsync(float[] samples16kMono, CancellationToken cancellationToken);
}
