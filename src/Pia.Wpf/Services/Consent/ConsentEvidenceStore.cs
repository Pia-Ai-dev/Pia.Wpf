using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure;
using Pia.Paths;
using Pia.Logging;

namespace Pia.Services.Consent;

/// <summary>
/// DPAPI-protected, write-only consent evidence: per session folder a marker, one grant file per speaker and a
/// revocation file beside it. Every write throws on failure, because a silent one would leave no proof at all.
/// </summary>
public sealed class ConsentEvidenceStore : IConsentEvidenceStore
{
    public const string SessionMarkerFileName = "session.json";

    private sealed record SessionEnvelope(string Schema, ConsentSessionMarker Session);

    private sealed record GrantEnvelope(string Schema, string SessionId, ConsentEvidence Evidence);

    private sealed record RevocationEnvelope(string Schema, string SessionId, string SpeakerLabel, DateTimeOffset RevokedAt);

    private const string SessionSchema = "pia-consent-session/v1";
    private const string GrantSchema = "pia-consent-evidence/v2";
    private const string RevocationSchema = "pia-consent-revocation/v1";

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    /// <summary>Default root: <c>%LOCALAPPDATA%\Pia\ConsentEvidence</c>.</summary>
    public static string DefaultRootDirectory => PiaPaths.ConsentEvidenceDirectory;

    private readonly string _rootDirectory;
    private readonly DpapiHelper _dpapi;
    private readonly ILogger<ConsentEvidenceStore> _logger;

    public ConsentEvidenceStore(string rootDirectory, DpapiHelper dpapi, ILogger<ConsentEvidenceStore> logger)
    {
        _rootDirectory = rootDirectory;
        _dpapi = dpapi;
        _logger = logger;
    }

    public async Task SaveGrantAsync(ConsentSessionMarker session, ConsentEvidence evidence, CancellationToken cancellationToken = default)
    {
        var sessionDir = Path.Combine(_rootDirectory, session.SessionId);
        var markerPath = Path.Combine(sessionDir, SessionMarkerFileName);

        // The marker comes with the first grant, not at prepare: a warmup that nobody consents to leaves no folder.
        var protectedMarker = File.Exists(markerPath)
            ? null
            : Protect(JsonSerializer.Serialize(new SessionEnvelope(SessionSchema, session), JsonOpts));
        var protectedGrant = Protect(JsonSerializer.Serialize(
            new GrantEnvelope(GrantSchema, session.SessionId, evidence), JsonOpts));

        Directory.CreateDirectory(sessionDir);
        if (protectedMarker is not null)
            await File.WriteAllTextAsync(markerPath, protectedMarker, cancellationToken).ConfigureAwait(false);

        var path = Path.Combine(sessionDir, $"{SanitizeFileName(evidence.SpeakerLabel)}.json");
        await File.WriteAllTextAsync(path, protectedGrant, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Consent evidence saved (first in session: {First})", protectedMarker is not null);
        _logger.SensitiveDebug(
            "Consent evidence saved for session {SessionId}, label {Label}", session.SessionId, evidence.SpeakerLabel);
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

        _logger.LogInformation("Consent revocation saved");
        _logger.SensitiveDebug("Consent revocation saved for session {SessionId}, label {Label}", sessionId, speakerLabel);
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
