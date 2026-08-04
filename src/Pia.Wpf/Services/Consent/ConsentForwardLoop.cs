using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Pia.Logging;
using Pia.Models;

namespace Pia.Services.Consent;

/// <summary>
/// Per-session context the forward loop needs but does not own: which session evidence/audit lines
/// belong to, which speech-to-text model produced the text, and which language the classifier should
/// try first.
/// </summary>
/// <param name="SessionId">Current session id (rotates on <c>EndSessionAsync</c>, not on a pause).</param>
/// <param name="SttModelId">Identifier of the speech-to-text model producing the utterance text.</param>
/// <param name="LanguageHint">The session's configured speech language, passed straight to the classifier.</param>
public sealed record ConsentSessionContext(string SessionId, string SttModelId, TargetSpeechLanguage LanguageHint);

/// <summary>
/// What <see cref="ConsentForwardLoop.ProcessAsync"/> did with one utterance. Purely observational —
/// nothing outside the gate branches on it except tests.
/// </summary>
public enum ConsentGateOutcome
{
    /// <summary>Microphone speech — always trusted, emitted unconditionally.</summary>
    EmitMic,

    /// <summary>Loopback speech from an already-consented speaker.</summary>
    EmitConsented,

    /// <summary>The consent sentence itself, emitted in-band under the (possibly renamed) final label.</summary>
    EmitConsentGrant,

    /// <summary>Loopback speech with no diarizer label — unattributable, dropped unconditionally (D1 fix).</summary>
    DropUnlabeled,

    /// <summary>Loopback speech from a speaker who has not (yet, or no longer) consented.</summary>
    DropUnconsented,

    /// <summary>Loopback speech from a speaker whose consent was withdrawn.</summary>
    DropRevoked,
}

/// <summary>
/// THE privacy boundary of direct transcription. Sole reader of the per-session raw utterance channel;
/// decides, utterance by utterance, whether transcribed text may ever reach the public channel (and
/// therefore the UI, the saved transcript, or any log). Everything that is not explicitly emitted here
/// is dropped and never observed again — there is no buffer, no replay, and no bypass.
///
/// <para>Single-threaded by construction: <see cref="RunAsync"/> is the sole reader of the raw channel
/// for the lifetime of one run, so there is no gate/grant race to reason about.</para>
/// </summary>
public sealed class ConsentForwardLoop
{
    private readonly IConsentStateManager _consent;
    private readonly INamedConsentClassifier _classifier;
    private readonly IConsentAuditLog _auditLog;
    private readonly IConsentEvidenceStore _evidenceStore;
    private readonly ILogger<ConsentForwardLoop> _logger;

    private readonly object _samplesLock = new();
    private readonly List<VoiceSample> _voiceSamples = new();

    private int _droppedUnlabeledCount;
    private int _droppedUnconsentedCount;
    private int _droppedRevokedCount;

    public ConsentForwardLoop(
        IConsentStateManager consentStateManager,
        INamedConsentClassifier classifier,
        IConsentAuditLog auditLog,
        IConsentEvidenceStore evidenceStore,
        ILogger<ConsentForwardLoop> logger)
    {
        _consent = consentStateManager;
        _classifier = classifier;
        _auditLog = auditLog;
        _evidenceStore = evidenceStore;
        _logger = logger;
    }

    /// <summary>
    /// Raised when a loopback speaker's consent state changes as a direct result of processing an
    /// utterance (i.e. a grant). Raised on the forward-loop's own thread; subscribers marshal
    /// themselves. Not raised for a revoke — that is driven directly by the host service.
    /// </summary>
    public event EventHandler<ConsentStateChangedEventArgs>? SpeakerConsentChanged;

    /// <summary>Batched count of loopback utterances dropped for carrying no diarizer label (D1).</summary>
    public int DroppedUnlabeledCount => Volatile.Read(ref _droppedUnlabeledCount);

    /// <summary>Batched count of loopback utterances dropped because their speaker had not consented.</summary>
    public int DroppedUnconsentedCount => Volatile.Read(ref _droppedUnconsentedCount);

    /// <summary>Batched count of loopback utterances dropped because their speaker's consent was revoked.</summary>
    public int DroppedRevokedCount => Volatile.Read(ref _droppedRevokedCount);

    /// <summary>Snapshot of every measured sample recorded on an emit path so far this session.</summary>
    public IReadOnlyList<VoiceSample> VoiceSamples
    {
        get
        {
            lock (_samplesLock)
            {
                return _voiceSamples.ToArray();
            }
        }
    }

    /// <summary>
    /// Reads <paramref name="raw"/> until it completes, gating every utterance through
    /// <see cref="ProcessAsync"/>. A single bad utterance (a classifier throw, a transient write
    /// failure) is logged and does not stop the loop — only channel completion or cancellation does.
    /// </summary>
    public async Task RunAsync(
        ConsentSessionContext context,
        ChannelReader<TranscriptUtterance> raw,
        ChannelWriter<TranscriptUtterance> sink,
        Func<string, string, bool> renameSpeaker,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var utterance in raw.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await ProcessAsync(context, utterance, sink, renameSpeaker, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Consent forward loop: processing one utterance threw; continuing");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Consent forward loop failed");
        }
    }

    /// <summary>
    /// The gate. Order is load-bearing: <see cref="TranscriptSpeaker.You"/> is checked FIRST because
    /// the mic engine has no diarizer and therefore produces a <c>null</c>
    /// <see cref="TranscriptUtterance.SpeakerLabel"/> for every utterance — a null-label check placed
    /// first would silently drop the entire microphone side. The null-label drop that follows is
    /// unconditional: besides a sub-1.5s segment there are two further producers of a null label (no
    /// diarizer at all, and a diarizer exception the engine swallows), so the drop must not assume
    /// anything about segment length.
    /// </summary>
    internal async Task<ConsentGateOutcome> ProcessAsync(
        ConsentSessionContext context,
        TranscriptUtterance utterance,
        ChannelWriter<TranscriptUtterance> sink,
        Func<string, string, bool> renameSpeaker,
        CancellationToken cancellationToken)
    {
        if (utterance.Speaker == TranscriptSpeaker.You)
        {
            await WriteAsync(sink, utterance, cancellationToken).ConfigureAwait(false);
            RecordSample(utterance);
            return ConsentGateOutcome.EmitMic;
        }

        // D1 fix: unconditional, regardless of why the label is missing.
        if (string.IsNullOrWhiteSpace(utterance.SpeakerLabel))
        {
            Interlocked.Increment(ref _droppedUnlabeledCount);
            _logger.LogInformation("Dropped an unattributable loopback utterance (no diarizer label)");
            _logger.SensitiveDebug("Dropped unlabeled loopback utterance text: '{Text}'", utterance.Text);
            return ConsentGateOutcome.DropUnlabeled;
        }

        var label = utterance.SpeakerLabel;

        switch (_consent.CurrentState(label))
        {
            case ConsentState.Granted:
                await WriteAsync(sink, utterance, cancellationToken).ConfigureAwait(false);
                RecordSample(utterance);
                return ConsentGateOutcome.EmitConsented;

            case ConsentState.Revoked:
                // A revoked speaker must not be reclassified — otherwise a repeated consent sentence
                // would silently resurrect them. Re-consent after revoke is a v2 UI action.
                Interlocked.Increment(ref _droppedRevokedCount);
                _logger.LogInformation("Dropped an utterance from a revoked speaker");
                _logger.SensitiveDebug("Dropped utterance from revoked speaker {Label}", label);
                return ConsentGateOutcome.DropRevoked;

            case ConsentState.Unknown:
                return await ClassifyAndMaybeGrantAsync(context, utterance, label, sink, renameSpeaker, cancellationToken)
                    .ConfigureAwait(false);

            default:
                // Fail-closed: any future enum member not explicitly handled above drops.
                Interlocked.Increment(ref _droppedUnconsentedCount);
                return ConsentGateOutcome.DropUnconsented;
        }
    }

    private async Task<ConsentGateOutcome> ClassifyAndMaybeGrantAsync(
        ConsentSessionContext context,
        TranscriptUtterance utterance,
        string label,
        ChannelWriter<TranscriptUtterance> sink,
        Func<string, string, bool> renameSpeaker,
        CancellationToken cancellationToken)
    {
        NamedConsentResult result;
        try
        {
            result = _classifier.Classify(utterance.Text, context.LanguageHint);
        }
        catch (Exception ex)
        {
            // Fail-closed: a throwing classifier must not accidentally let text through.
            _logger.LogWarning(ex, "Named consent classifier threw; dropping (fail-closed)");
            Interlocked.Increment(ref _droppedUnconsentedCount);
            return ConsentGateOutcome.DropUnconsented;
        }

        if (!result.IsConsent || result.Confidence < NamedConsentClassifier.GrantConfidenceThreshold)
        {
            Interlocked.Increment(ref _droppedUnconsentedCount);
            _logger.LogInformation(
                "Dropped an unconsented loopback utterance (confidence={Confidence:F2})", result.Confidence);
            _logger.SensitiveDebug("Dropped unconsented utterance text for {Label}: '{Text}'", label, utterance.Text);
            return ConsentGateOutcome.DropUnconsented;
        }

        var evidence = new ConsentEvidence(
            label,
            result.ExtractedName,
            utterance.Text,
            result.Language,
            result.Confidence,
            utterance.Timestamp,
            context.SttModelId);

        _consent.Grant(label, result.ExtractedName, evidence);

        // Resolve the final label: rename may fail (collision, diarizer refusal) — in which case the
        // grant still stands under the original diarizer label, and the name lives only in the
        // evidence/consent entry.
        var finalLabel = label;
        if (!string.IsNullOrWhiteSpace(result.ExtractedName) && renameSpeaker(label, result.ExtractedName))
        {
            finalLabel = result.ExtractedName;
        }

        try
        {
            await _evidenceStore.SaveGrantAsync(context.SessionId, evidence, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The grant still stands — a disk/DPAPI failure must not silently discard a speaker's
            // consent. Never log the sentence or the name here.
            _logger.LogError(ex, "Failed to persist consent evidence for a grant");
            _auditLog.Append(new AuditEvent(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                ConsentAuditEventTypes.EvidenceWriteFailed,
                label,
                null));
        }

        // Audit uses the ORIGINAL diarizer label, never the extracted name: the name is personal data
        // and must live only in the DPAPI-protected evidence file, not in the plaintext audit trail.
        _auditLog.Append(new AuditEvent(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            ConsentAuditEventTypes.ConsentGranted,
            label,
            new Dictionary<string, object?>
            {
                ["confidence"] = result.Confidence,
                ["language"] = result.Language,
                ["hasName"] = !string.IsNullOrWhiteSpace(result.ExtractedName),
            }));

        // Raised BEFORE the sink write so a UI subscriber can relabel its chip before the consent
        // utterance (already carrying the new label) arrives. `finalLabel` is the AUTHORITATIVE key (the
        // one the consent map is keyed by and the gate reads); the original diarizer label rides along so
        // a subscriber can still find the row it created at detection time. When the rename was refused
        // these two differ, and a subscriber that keyed its UI off ExtractedName instead would end up
        // pointing at a key the consent map does not have — making a later revoke a silent no-op.
        RaiseSpeakerConsentChanged(new ConsentStateChangedEventArgs(
            finalLabel, ConsentState.Unknown, ConsentState.Granted, result.ExtractedName, label));

        var consentUtterance = utterance with { SpeakerLabel = finalLabel };
        await WriteAsync(sink, consentUtterance, cancellationToken).ConfigureAwait(false);
        RecordSample(consentUtterance);
        return ConsentGateOutcome.EmitConsentGrant;
    }

    private void RaiseSpeakerConsentChanged(ConsentStateChangedEventArgs args)
    {
        var handler = SpeakerConsentChanged;
        if (handler is null) return;
        try
        {
            handler.Invoke(this, args);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SpeakerConsentChanged subscriber threw");
        }
    }

    /// <summary>
    /// Discards every measured sample recorded under <paramref name="speakerLabel"/>. Called on a
    /// Granted -&gt; Revoked transition: revocation removes that speaker's bubbles and journal entries from
    /// the transcript, so leaving their samples behind would keep their name, utterance count and speaking
    /// time in the voice-stats flyout and in the YAML front matter of the file the user saves — and would
    /// keep diluting every other speaker's share with speech the saved document no longer contains.
    /// </summary>
    /// <returns>How many samples were removed (0 when the label had none).</returns>
    public int RemoveSamplesFor(string speakerLabel)
    {
        lock (_samplesLock)
        {
            return _voiceSamples.RemoveAll(
                s => string.Equals(s.SpeakerLabel, speakerLabel, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Re-keys every already-measured sample from <paramref name="oldLabel"/> to
    /// <paramref name="newLabel"/>. Called only after BOTH the diarizer and the consent map have accepted
    /// a rename, so the statistics stay one row per person: <c>VoiceStatsCalculator</c> groups by label, so
    /// without this a mid-session rename reported one speaker as two rows with split totals and halved
    /// shares.
    /// </summary>
    public void RenameSamples(string oldLabel, string newLabel)
    {
        lock (_samplesLock)
        {
            for (var i = 0; i < _voiceSamples.Count; i++)
            {
                if (string.Equals(_voiceSamples[i].SpeakerLabel, oldLabel, StringComparison.Ordinal))
                    _voiceSamples[i] = _voiceSamples[i] with { SpeakerLabel = newLabel };
            }
        }
    }

    private void RecordSample(TranscriptUtterance emitted)
    {
        var sample = new VoiceSample(emitted.Speaker, emitted.SpeakerLabel, emitted.DurationSeconds ?? 0d);
        lock (_samplesLock)
        {
            _voiceSamples.Add(sample);
        }
    }

    private static async Task WriteAsync(
        ChannelWriter<TranscriptUtterance> sink, TranscriptUtterance utterance, CancellationToken cancellationToken)
    {
        try
        {
            await sink.WriteAsync(utterance, cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            // The public channel was completed during shutdown — nothing more to deliver.
        }
    }
}
