using Microsoft.Extensions.Logging;

namespace Pia.Services.Consent;

public sealed class ConsentStateManager : IConsentStateManager
{
    private readonly ILogger<ConsentStateManager> _logger;
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, SpeakerConsentEntry> _entries = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public TimeSpan PromptTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public float GrantConfidenceThreshold { get; set; } = 0.9f;

    public event EventHandler<ConsentStateChangedEventArgs>? StateChanged;

    public ConsentStateManager(ILogger<ConsentStateManager> logger, TimeProvider clock)
    {
        _logger = logger;
        _clock = clock;
    }

    public SpeakerConsentEntry GetOrCreate(string speakerLabel)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(speakerLabel, out var entry))
            {
                entry = new SpeakerConsentEntry(speakerLabel, _clock.GetUtcNow());
                _entries[speakerLabel] = entry;
            }
            return entry;
        }
    }

    public bool TryGet(string speakerLabel, out SpeakerConsentEntry entry)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(speakerLabel, out entry!);
        }
    }

    public ConsentState CurrentState(string speakerLabel)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(speakerLabel, out var e) ? e.State : ConsentState.Unknown;
        }
    }

    public void MarkPrompted(string speakerLabel)
    {
        ConsentState oldState;
        SpeakerConsentEntry entry;
        lock (_lock)
        {
            entry = GetOrCreateNoLock(speakerLabel);
            oldState = entry.State;
            entry.State = ConsentState.Prompted;
            entry.PromptedAt = _clock.GetUtcNow();
        }
        _logger.LogInformation("Consent {Label}: {Old} -> Prompted", speakerLabel, oldState);
        Raise(speakerLabel, oldState, ConsentState.Prompted);
    }

    public void RecordClassification(
        string speakerLabel,
        ConsentClassification classification,
        string transcriptText,
        string promptHash,
        string promptText,
        string sttModelId)
    {
        ConsentState oldState;
        ConsentState newState;
        lock (_lock)
        {
            var entry = GetOrCreateNoLock(speakerLabel);
            oldState = entry.State;

            if (classification.Confidence < GrantConfidenceThreshold)
            {
                newState = ConsentState.Ambiguous;
            }
            else
            {
                newState = classification.Decision switch
                {
                    ConsentDecision.Grant => ConsentState.Granted,
                    ConsentDecision.Deny => ConsentState.Denied,
                    _ => ConsentState.Ambiguous,
                };
            }

            entry.State = newState;
            entry.Evidence = new ConsentEvidence(
                transcriptText,
                classification.Confidence,
                _clock.GetUtcNow(),
                promptHash,
                promptText,
                sttModelId);
        }
        _logger.LogInformation("Consent {Label}: {Old} -> {New} (confidence={Conf})",
            speakerLabel, oldState, newState, classification.Confidence);
        Raise(speakerLabel, oldState, newState);
    }

    public void SetEmbedding(string speakerLabel, float[] embedding)
    {
        if (embedding is null || embedding.Length == 0) return;
        lock (_lock)
        {
            var entry = GetOrCreateNoLock(speakerLabel);
            entry.Embedding = (float[])embedding.Clone();
        }
    }

    public void Revoke(string speakerLabel)
    {
        ConsentState oldState;
        lock (_lock)
        {
            var entry = GetOrCreateNoLock(speakerLabel);
            oldState = entry.State;
            entry.State = ConsentState.Revoked;
        }
        _logger.LogInformation("Consent {Label}: {Old} -> Revoked", speakerLabel, oldState);
        Raise(speakerLabel, oldState, ConsentState.Revoked);
    }

    public void Rename(string oldLabel, string newLabel)
    {
        if (oldLabel == newLabel) return;
        lock (_lock)
        {
            if (!_entries.TryGetValue(oldLabel, out var entry)) return;
            _entries.Remove(oldLabel);
            entry.SpeakerLabel = newLabel;
            _entries[newLabel] = entry;
        }
        _logger.LogInformation("Consent rename: {Old} -> {New}", oldLabel, newLabel);
    }

    public void SweepTimeouts()
    {
        var transitions = new List<(string label, ConsentState oldState)>();
        lock (_lock)
        {
            var now = _clock.GetUtcNow();
            foreach (var entry in _entries.Values)
            {
                if (entry.State == ConsentState.Prompted &&
                    entry.PromptedAt is { } promptedAt &&
                    now - promptedAt > PromptTimeout)
                {
                    transitions.Add((entry.SpeakerLabel, entry.State));
                    entry.State = ConsentState.Timeout;
                }
            }
        }

        foreach (var (label, oldState) in transitions)
        {
            _logger.LogInformation("Consent {Label}: {Old} -> Timeout", label, oldState);
            Raise(label, oldState, ConsentState.Timeout);
        }
    }

    private SpeakerConsentEntry GetOrCreateNoLock(string speakerLabel)
    {
        if (!_entries.TryGetValue(speakerLabel, out var entry))
        {
            entry = new SpeakerConsentEntry(speakerLabel, _clock.GetUtcNow());
            _entries[speakerLabel] = entry;
        }
        return entry;
    }

    private void Raise(string label, ConsentState oldState, ConsentState newState)
    {
        try { StateChanged?.Invoke(this, new ConsentStateChangedEventArgs(label, oldState, newState)); }
        catch (Exception ex) { _logger.LogError(ex, "ConsentStateChanged subscriber threw for {Label}", label); }
    }
}
