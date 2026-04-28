using Microsoft.Extensions.Logging;
using Pia.Services.LiveTranscription;

namespace Pia.Services.Consent;

/// <summary>
/// Strategy A — Pause & Re-Consent (spec §3.9). On every new speaker:
///   1. Pause every registered engine (every Granted speaker stops being transcribed).
///   2. The caller runs the consent prompt + classifier flow for the new speaker.
///   3. We listen on <see cref="IConsentStateManager.StateChanged"/>:
///      - Granted  → resume all engines.
///      - Denied / Timeout / Revoked → resume all engines and add the speaker's voice
///        embedding to the session blocklist via <see cref="IBlocklistFilter"/>.
///
/// The orchestrator emits <c>STRATEGY_A_PAUSED</c> / <c>STRATEGY_A_RESUMED</c> audit
/// events with paused-duration metadata.
/// </summary>
public sealed class StrategyAOrchestrator : IConsentOrchestrator, IDisposable
{
    private readonly IConsentStateManager _consentMgr;
    private readonly IConsentAuditLog _auditLog;
    private readonly IBlocklistFilter? _blocklist;
    private readonly ILogger<StrategyAOrchestrator> _logger;

    private readonly object _lock = new();
    private readonly List<LiveTranscriptionEngineService> _engines = new();
    private readonly Dictionary<string, DateTimeOffset> _pauseStartedAt = new(StringComparer.Ordinal);

    public StrategyAOrchestrator(
        IConsentStateManager consentMgr,
        IConsentAuditLog auditLog,
        ILogger<StrategyAOrchestrator> logger,
        IBlocklistFilter? blocklist = null)
    {
        _consentMgr = consentMgr;
        _auditLog = auditLog;
        _blocklist = blocklist;
        _logger = logger;
        _consentMgr.StateChanged += OnConsentStateChanged;
    }

    public void RegisterEngine(LiveTranscriptionEngineService engine)
    {
        lock (_lock) { if (!_engines.Contains(engine)) _engines.Add(engine); }
    }

    public void UnregisterEngine(LiveTranscriptionEngineService engine)
    {
        lock (_lock) _engines.Remove(engine);
    }

    public async Task OnNewSpeakerJoinedAsync(string speakerLabel, CancellationToken cancellationToken = default)
    {
        LiveTranscriptionEngineService[] snapshot;
        lock (_lock)
        {
            // Idempotent: if already paused for this speaker, don't re-pause.
            if (_pauseStartedAt.ContainsKey(speakerLabel)) return;
            _pauseStartedAt[speakerLabel] = DateTimeOffset.UtcNow;
            snapshot = _engines.ToArray();
        }

        foreach (var engine in snapshot)
        {
            try { await engine.PauseAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "PauseAsync threw during Strategy A pause"); }
        }
        _auditLog.Append(new AuditEvent(
            Guid.NewGuid(), DateTimeOffset.UtcNow, "STRATEGY_A_PAUSED", speakerLabel,
            new Dictionary<string, object?> { ["engine_count"] = snapshot.Length }
        ));
        _logger.LogInformation(
            "Strategy A paused {Count} engine(s) for new speaker {Label}",
            snapshot.Length, speakerLabel);
    }

    private void OnConsentStateChanged(object? sender, ConsentStateChangedEventArgs e)
    {
        var terminal = e.NewState is ConsentState.Granted
            or ConsentState.Denied
            or ConsentState.Timeout
            or ConsentState.Revoked;
        if (!terminal) return;

        DateTimeOffset? pauseStart = null;
        LiveTranscriptionEngineService[] snapshot;
        lock (_lock)
        {
            if (_pauseStartedAt.TryGetValue(e.SpeakerLabel, out var t))
            {
                pauseStart = t;
                _pauseStartedAt.Remove(e.SpeakerLabel);
            }
            snapshot = _engines.ToArray();
        }
        if (pauseStart is null) return; // we never paused for this speaker

        // Don't await: state-change handlers fire on the consent manager's thread; spawn
        // resume/audit work on the thread pool so the manager stays responsive.
        _ = Task.Run(async () =>
        {
            foreach (var engine in snapshot)
            {
                try { await engine.ResumeAsync().ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "ResumeAsync threw during Strategy A resume"); }
            }
            var pausedMs = (long)(DateTimeOffset.UtcNow - pauseStart.Value).TotalMilliseconds;
            _auditLog.Append(new AuditEvent(
                Guid.NewGuid(), DateTimeOffset.UtcNow, "STRATEGY_A_RESUMED", e.SpeakerLabel,
                new Dictionary<string, object?>
                {
                    ["engine_count"] = snapshot.Length,
                    ["paused_ms"] = pausedMs,
                    ["outcome"] = e.NewState.ToString(),
                }));
            _logger.LogInformation(
                "Strategy A resumed {Count} engine(s) after {Ms} ms; outcome={Outcome} for {Label}",
                snapshot.Length, pausedMs, e.NewState, e.SpeakerLabel);

            // Block the speaker for the rest of the session if they did not grant.
            if (e.NewState is ConsentState.Denied or ConsentState.Timeout or ConsentState.Revoked)
            {
                try { _blocklist?.BlockSpeaker(e.SpeakerLabel); }
                catch (Exception ex) { _logger.LogWarning(ex, "Blocklist add threw for {Label}", e.SpeakerLabel); }
            }
        });
    }

    public void Dispose()
    {
        _consentMgr.StateChanged -= OnConsentStateChanged;
    }
}
