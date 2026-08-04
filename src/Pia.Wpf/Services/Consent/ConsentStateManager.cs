using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Pia.Logging;

namespace Pia.Services.Consent;

/// <summary>
/// Session-scoped, thread-safe implementation of <see cref="IConsentStateManager"/>. Internal mutable
/// state never leaves the lock — every public member returns an immutable
/// <see cref="SpeakerConsentEntry"/> snapshot, because the manager has two writers in v1 (the background
/// forward loop and a UI-thread revoke) and handing out a live, mutable object would be an unsynchronised
/// shared reference no lock could protect.
/// </summary>
public sealed class ConsentStateManager : IConsentStateManager
{
    /// <summary>Internal mutable per-speaker state. Never leaves <see cref="_lock"/> by reference.</summary>
    private sealed class MutableEntry
    {
        public required string SpeakerLabel { get; set; }
        public required DateTimeOffset FirstDetected { get; init; }
        public ConsentState State { get; set; } = ConsentState.Unknown;
        public string? ExtractedName { get; set; }
        public ConsentEvidence? Evidence { get; set; }

        public SpeakerConsentEntry ToSnapshot() =>
            new(SpeakerLabel, FirstDetected, State, ExtractedName, Evidence);
    }

    private readonly ILogger<ConsentStateManager> _logger;
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, MutableEntry> _entries = new(StringComparer.Ordinal);
    private readonly object _lock = new();

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
            return GetOrCreateNoLock(speakerLabel).ToSnapshot();
        }
    }

    public bool TryGet(string speakerLabel, [MaybeNullWhen(false)] out SpeakerConsentEntry entry)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(speakerLabel, out var mutable))
            {
                entry = mutable.ToSnapshot();
                return true;
            }
        }
        entry = null!;
        return false;
    }

    public ConsentState CurrentState(string speakerLabel)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(speakerLabel, out var e) ? e.State : ConsentState.Unknown;
        }
    }

    public void Grant(string speakerLabel, string? extractedName, ConsentEvidence evidence)
    {
        ConsentState oldState;
        ConsentState newState;
        lock (_lock)
        {
            var entry = GetOrCreateNoLock(speakerLabel);
            oldState = entry.State;

            if (oldState == ConsentState.Granted)
            {
                // Idempotent: no state change, no event, existing evidence untouched.
                return;
            }

            newState = ConsentState.Granted;
            entry.State = newState;
            entry.ExtractedName = extractedName;
            entry.Evidence = evidence;
        }

        _logger.LogInformation("Consent state {Old} -> {New}", oldState, newState);
        _logger.SensitiveInformation("Consent granted for label {Label}, name {Name}", speakerLabel, extractedName);
        Raise(speakerLabel, oldState, newState, extractedName);
    }

    public bool Revoke(string speakerLabel)
    {
        ConsentState oldState;
        string? extractedName;
        lock (_lock)
        {
            // TryGetValue, not GetOrCreateNoLock: a revoke aimed at an unknown label must not CREATE an
            // Unknown entry under it. The revoke command carries a UI-facing label, which can be an
            // extracted personal name — inserting a phantom entry keyed by that name would put personal
            // data into Snapshot() for a speaker who was never even detected.
            if (!_entries.TryGetValue(speakerLabel, out var entry))
                return false;

            oldState = entry.State;

            if (oldState != ConsentState.Granted)
            {
                // No-op: only a Granted speaker can be revoked.
                return false;
            }

            entry.State = ConsentState.Revoked;
            // Evidence and ExtractedName are deliberately preserved.
            extractedName = entry.ExtractedName;
        }

        _logger.LogInformation("Consent state {Old} -> Revoked", oldState);
        _logger.SensitiveDebug("Consent revoked for label {Label}", speakerLabel);
        Raise(speakerLabel, oldState, ConsentState.Revoked, extractedName);
        return true;
    }

    public bool Rename(string oldLabel, string newLabel)
    {
        if (string.IsNullOrWhiteSpace(newLabel))
            return false;

        lock (_lock)
        {
            if (!_entries.TryGetValue(oldLabel, out var entry))
                return false;

            if (!string.Equals(oldLabel, newLabel, StringComparison.Ordinal) && _entries.ContainsKey(newLabel))
                return false;

            _entries.Remove(oldLabel);
            entry.SpeakerLabel = newLabel;
            _entries[newLabel] = entry;
        }

        // Deliberately no StateChanged raise: the consent decision itself did not change.
        _logger.SensitiveInformation("Consent rename: {Old} -> {New}", oldLabel, newLabel);
        return true;
    }

    public void ResetSession()
    {
        int count;
        lock (_lock)
        {
            count = _entries.Count;
            _entries.Clear();
        }
        // Session-scoped clear on a DI-singleton manager; nothing about individual speakers is logged here.
        _logger.LogInformation("Consent session reset, cleared {Count} entries", count);
    }

    public IReadOnlyList<SpeakerConsentEntry> Snapshot()
    {
        lock (_lock)
        {
            return _entries.Values
                .OrderBy(e => e.FirstDetected)
                .ThenBy(e => e.SpeakerLabel, StringComparer.Ordinal)
                .Select(e => e.ToSnapshot())
                .ToList();
        }
    }

    private MutableEntry GetOrCreateNoLock(string speakerLabel)
    {
        if (!_entries.TryGetValue(speakerLabel, out var entry))
        {
            entry = new MutableEntry { SpeakerLabel = speakerLabel, FirstDetected = _clock.GetUtcNow() };
            _entries[speakerLabel] = entry;
        }
        return entry;
    }

    private void Raise(string label, ConsentState oldState, ConsentState newState, string? extractedName)
    {
        var handler = StateChanged;
        if (handler is null) return;

        var args = new ConsentStateChangedEventArgs(label, oldState, newState, extractedName);

        // Invoke each subscriber independently: a plain `handler.Invoke(...)` is one multicast
        // call, so a throwing subscriber would stop .NET from ever reaching the ones after it in
        // the invocation list, even with this whole call wrapped in try/catch.
        foreach (var subscriber in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler<ConsentStateChangedEventArgs>)subscriber).Invoke(this, args);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ConsentStateChanged subscriber threw");
                _logger.SensitiveDebug("ConsentStateChanged subscriber threw for label {Label}", label);
            }
        }
    }
}
