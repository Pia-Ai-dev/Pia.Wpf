using System.IO;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure;

namespace Pia.Services.Consent.Snippet;

/// <summary>
/// Captures the speaker's response audio (10–15 s window starting at prompt-end) when
/// <see cref="SecurityProfile.PersistConsentAudioSnippet"/> is true. Spec exception to the
/// "no audio persisted" rule — the persistence is intentionally loud: every write emits
/// a <c>SNIPPET_PERSISTED</c> audit event.
///
/// Audio encoding: WAV (libopus is not a project dependency; documented fallback).
/// On-disk: <c>session_&lt;uuid&gt;/consent/speaker_&lt;id&gt;_grant.wav.enc</c>, AES-256-GCM
/// via <see cref="SessionEncryption"/>.
/// </summary>
public sealed class ConsentSnippetRecorder
{
    public const string AudioContainer = "wav"; // documented fallback (no libopus dependency)

    private readonly SessionEncryption _encryption;
    private readonly IConsentAuditLog _auditLog;
    private readonly TimeProvider _clock;
    private readonly ILogger<ConsentSnippetRecorder> _logger;

    public ConsentSnippetRecorder(
        SessionEncryption encryption,
        IConsentAuditLog auditLog,
        TimeProvider clock,
        ILogger<ConsentSnippetRecorder> logger)
    {
        _encryption = encryption;
        _auditLog = auditLog;
        _clock = clock;
        _logger = logger;
    }

    public string? Persist(
        SecurityProfile profile,
        string sessionDirectory,
        string speakerLabel,
        ReadOnlySpan<byte> wavBytes)
    {
        if (!profile.PersistConsentAudioSnippet)
        {
            _logger.LogDebug("Snippet recorder: profile flag off — skipping persistence for {Label}", speakerLabel);
            return null;
        }
        if (wavBytes.Length == 0)
        {
            _logger.LogWarning("Snippet recorder: empty audio for {Label}; nothing to persist", speakerLabel);
            return null;
        }

        var consentDir = Path.Combine(sessionDirectory, "consent");
        Directory.CreateDirectory(consentDir);

        var safeId = SanitiseLabel(speakerLabel);
        var path = Path.Combine(consentDir, $"speaker_{safeId}_grant.{AudioContainer}.enc");
        var encrypted = _encryption.Encrypt(wavBytes);
        File.WriteAllBytes(path, encrypted);

        _logger.LogWarning(
            "SNIPPET PERSISTED: {Label} → {Path} (encrypted, {Bytes} bytes plaintext)",
            speakerLabel, path, wavBytes.Length);

        _auditLog.Append(new AuditEvent(
            Guid.NewGuid(), _clock.GetUtcNow(), "SNIPPET_PERSISTED",
            speakerLabel,
            new Dictionary<string, object?>
            {
                ["path"] = path,
                ["container"] = AudioContainer,
                ["plaintextBytes"] = wavBytes.Length,
            }));

        return path;
    }

    /// <summary>Removes the persisted snippet for a speaker (used by RevocationService).</summary>
    public bool Delete(string sessionDirectory, string speakerLabel)
    {
        var path = Path.Combine(
            sessionDirectory, "consent",
            $"speaker_{SanitiseLabel(speakerLabel)}_grant.{AudioContainer}.enc");
        if (!File.Exists(path)) return false;
        File.Delete(path);
        _logger.LogInformation("Snippet deleted: {Path}", path);
        return true;
    }

    private static string SanitiseLabel(string label)
    {
        var chars = label.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-').ToArray();
        return chars.Length == 0 ? "unknown" : new string(chars);
    }
}
