namespace Pia.Services.LiveTranscription;

/// <summary>
/// Within-session speaker identification for live transcription. Computes a speaker
/// embedding from a speech segment, matches against known speakers, and either returns
/// the matched display label or registers a new "Speaker N" label for an unseen voice.
/// State is per meeting — call <see cref="Reset"/> on session start/stop.
/// </summary>
public interface ISpeakerIdentificationService : IDisposable
{
    /// <summary>
    /// Compute the embedding for <paramref name="segmentSamples"/> (16 kHz mono float32),
    /// match against the in-session known-speaker manager, and return a stable display label.
    /// New voices are registered as "Speaker 1", "Speaker 2", … in order of first appearance.
    /// </summary>
    string IdentifyOrRegister(float[] segmentSamples, int sampleRate);

    /// <summary>
    /// Compute the segment's embedding and identify-or-register, returning both. Used by
    /// the blocklist filter so the engine pipeline can run a similarity check before the
    /// segment reaches the consent gate / ring buffer.
    /// </summary>
    (string Label, float[] Embedding) IdentifyOrRegisterWithEmbedding(float[] segmentSamples, int sampleRate);

    /// <summary>
    /// Rename a display label so all subsequent <see cref="IdentifyOrRegister"/> calls for
    /// the same voice return <paramref name="newLabel"/>. Returns true if a label matching
    /// <paramref name="oldLabel"/> existed and was renamed.
    /// </summary>
    bool Rename(string oldLabel, string newLabel);

    /// <summary>
    /// Drop all known-speaker state. Called on meeting start and stop so each meeting begins
    /// with a fresh "Speaker 1" pool.
    /// </summary>
    void Reset();
}
