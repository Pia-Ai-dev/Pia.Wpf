using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure;
using Pia.Logging;

namespace Pia.Services.Consent;

/// <summary>
/// DPAPI-protected, write-only persistence of consent evidence (Art. 7 GDPR Nachweispflicht). One file
/// per speaker per session for the grant, plus a separate revocation file appended beside it — the
/// grant file itself is never modified or deleted.
///
/// <para>This is the D7 fix: the old branch's equivalent write path always passed an empty evidence
/// path and never persisted anything. Both public methods THROW on any encryption or I/O failure —
/// a silent failure here is exactly the defect being fixed.</para>
/// </summary>
public sealed class ConsentEvidenceStore : IConsentEvidenceStore
{
    private sealed record GrantEnvelope(string Schema, string SessionId, ConsentEvidence Evidence);

    private sealed record RevocationEnvelope(string Schema, string SessionId, string SpeakerLabel, DateTimeOffset RevokedAt);

    private const string GrantSchema = "pia-consent-evidence/v1";
    private const string RevocationSchema = "pia-consent-revocation/v1";

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    /// <summary>Default root: <c>%LOCALAPPDATA%\Pia\ConsentEvidence</c>.</summary>
    public static string DefaultRootDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Pia", "ConsentEvidence");

    private readonly string _rootDirectory;
    private readonly DpapiHelper _dpapi;
    private readonly ILogger<ConsentEvidenceStore> _logger;

    public ConsentEvidenceStore(string rootDirectory, DpapiHelper dpapi, ILogger<ConsentEvidenceStore> logger)
    {
        _rootDirectory = rootDirectory;
        _dpapi = dpapi;
        _logger = logger;
    }

    public async Task SaveGrantAsync(string sessionId, ConsentEvidence evidence, CancellationToken cancellationToken = default)
    {
        var envelope = new GrantEnvelope(GrantSchema, sessionId, evidence);
        var json = JsonSerializer.Serialize(envelope, JsonOpts);
        var protectedJson = Protect(json);

        var sessionDir = Path.Combine(_rootDirectory, sessionId);
        Directory.CreateDirectory(sessionDir);
        var path = Path.Combine(sessionDir, $"{SanitizeFileName(evidence.SpeakerLabel)}.json");

        await File.WriteAllTextAsync(path, protectedJson, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Consent evidence saved for session {SessionId}: {Outcome}", sessionId, true);
        _logger.SensitiveDebug("Consent evidence saved for label {Label}", evidence.SpeakerLabel);
    }

    public async Task SaveRevocationAsync(string sessionId, string speakerLabel, DateTimeOffset revokedAt, CancellationToken cancellationToken = default)
    {
        var envelope = new RevocationEnvelope(RevocationSchema, sessionId, speakerLabel, revokedAt);
        var json = JsonSerializer.Serialize(envelope, JsonOpts);
        var protectedJson = Protect(json);

        var sessionDir = Path.Combine(_rootDirectory, sessionId);
        Directory.CreateDirectory(sessionDir);
        var path = Path.Combine(sessionDir, $"{SanitizeFileName(speakerLabel)}.revoked.json");

        await File.WriteAllTextAsync(path, protectedJson, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Consent revocation saved for session {SessionId}: {Outcome}", sessionId, true);
        _logger.SensitiveDebug("Consent revocation saved for label {Label}", speakerLabel);
    }

    /// <summary>
    /// Encrypts <paramref name="plainText"/> and throws when DPAPI silently failed. <see cref="DpapiHelper"/>
    /// returns <see cref="string.Empty"/> instead of throwing on a <c>CryptographicException</c>/
    /// <c>FormatException</c> — for our always-non-empty JSON input, an empty result means the write
    /// would otherwise be a silent, unrecoverable, empty evidence file. That is precisely the gap this
    /// store exists to close, so it is treated as a failure here.
    /// </summary>
    private string Protect(string plainText)
    {
        var protectedText = _dpapi.Encrypt(plainText);
        if (string.IsNullOrEmpty(protectedText))
        {
            throw new InvalidOperationException("DPAPI protection of consent evidence failed");
        }
        return protectedText;
    }

    private static string SanitizeFileName(string label)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = label;
        foreach (var ch in invalid)
        {
            sanitized = sanitized.Replace(ch, '_');
        }
        return sanitized;
    }
}
