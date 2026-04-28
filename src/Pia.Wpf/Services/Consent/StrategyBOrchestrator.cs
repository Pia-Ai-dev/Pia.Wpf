using Pia.Services.LiveTranscription;

namespace Pia.Services.Consent;

/// <summary>
/// Strategy B (selective recording): the gate already drops audio from the new speaker
/// until they grant consent, while existing Granted speakers keep flowing. The
/// orchestrator therefore has nothing to coordinate beyond what the gate does inline.
/// </summary>
public sealed class StrategyBOrchestrator : IConsentOrchestrator
{
    public void RegisterEngine(LiveTranscriptionEngineService engine) { }
    public void UnregisterEngine(LiveTranscriptionEngineService engine) { }
    public Task OnNewSpeakerJoinedAsync(string speakerLabel, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
