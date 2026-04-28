using Pia.Services.LiveTranscription;

namespace Pia.Services.Consent;

/// <summary>
/// Coordinates the post-detection consent flow when a new speaker joins. Different
/// security profiles plug in different orchestrators:
///   * <see cref="StrategyAOrchestrator"/> — pause every running engine, prompt, then
///     resume on Grant or block on Deny/Timeout (spec §3.9 Strategy A, "Pause & Re-Consent").
///   * <see cref="StrategyBOrchestrator"/> — selective recording: existing Granted speakers
///     keep flowing while the new speaker waits for consent (spec §3.9 Strategy B).
///
/// The orchestrator does NOT perform consent classification itself — that stays in
/// <see cref="LiveTranscription.LiveMeetingService"/>. Its only job is timing/flow-control.
/// </summary>
public interface IConsentOrchestrator
{
    void RegisterEngine(LiveTranscriptionEngineService engine);
    void UnregisterEngine(LiveTranscriptionEngineService engine);
    Task OnNewSpeakerJoinedAsync(string speakerLabel, CancellationToken cancellationToken = default);
}
