namespace Pia.Services.Consent;

/// <summary>
/// Session-scoped blocklist of voices that have explicitly declined consent (or timed out
/// or revoked). Audio matching any blocked embedding is dropped before reaching the ring
/// buffer (spec §3.9 blocklist filter).
/// </summary>
public interface IBlocklistFilter
{
    /// <summary>
    /// Add the speaker's current embedding to the blocklist. Subsequent VAD segments
    /// matching this voice will be dropped.
    /// </summary>
    void BlockSpeaker(string speakerLabel);

    /// <summary>
    /// Whether the embedding matches any blocked speaker's voice.
    /// </summary>
    bool ShouldDrop(float[] embedding);
}
