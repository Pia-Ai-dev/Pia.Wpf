using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure;
using Pia.Paths;
using Pia.Logging;

namespace Pia.Services.Consent;

/// <summary>
/// DPAPI-protected consent evidence, per session folder: a marker, the grants or a host's acknowledgement, revocations
/// and a copies log. Evidence writes throw on failure, because a silent one would leave no proof at all.
/// </summary>
public sealed class ConsentEvidenceStore : IConsentEvidenceStore
{
    public const string SessionMarkerFileName = "session.json";
    public const string CopiesFileName = "copies.json";
    public const string HostAcknowledgementFileName = "host-ack.json";

    private sealed record SessionEnvelope(string Schema, ConsentSessionMarker Session);

    private sealed record GrantEnvelope(string Schema, string SessionId, ConsentEvidence Evidence);

    private sealed record RevocationEnvelope(string Schema, string SessionId, string SpeakerLabel, DateTimeOffset RevokedAt);

    private sealed record CopyEnvelope(string Schema, string SessionId, ConsentCopy Copy);

    private sealed record HostAcknowledgementEnvelope(
        string Schema,
        string SessionId,
        DateTimeOffset AcknowledgedAt,
        int NoticeVersion,
        IReadOnlyList<string> NoticePurposes,
        string NoticeLanguage);

    private const string SessionSchema = "pia-consent-session/v1";
    private const string GrantSchema = "pia-consent-evidence/v2";
    private const string RevocationSchema = "pia-consent-revocation/v1";
    private const string CopySchema = "pia-consent-copy/v1";
    private const string HostAcknowledgementSchema = "pia-consent-host-ack/v1";

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    // One protected line per entry, so an append never has to decrypt and rewrite what is already there.
    private readonly SemaphoreSlim _copiesGate = new(1, 1);

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
        var protectedGrant = Protect(JsonSerializer.Serialize(
            new GrantEnvelope(GrantSchema, session.SessionId, evidence), JsonOpts));
        var first = await WriteIntoSessionAsync(
            session, $"{SanitizeFileName(evidence.SpeakerLabel)}.json", protectedGrant, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Consent evidence saved (first in session: {First})", first);
        _logger.SensitiveDebug(
            "Consent evidence saved for session {SessionId}, label {Label}", session.SessionId, evidence.SpeakerLabel);
    }

    public async Task SaveHostAcknowledgementAsync(
        ConsentSessionMarker session, DateTimeOffset acknowledgedAt, CancellationToken cancellationToken = default)
    {
        var protectedAcknowledgement = Protect(JsonSerializer.Serialize(
            new HostAcknowledgementEnvelope(
                HostAcknowledgementSchema,
                session.SessionId,
                acknowledgedAt,
                session.NoticeVersion,
                session.NoticePurposes,
                session.NoticeLanguage),
            JsonOpts));
        await WriteIntoSessionAsync(session, HostAcknowledgementFileName, protectedAcknowledgement, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("Meeting host acknowledgement saved as consent evidence ({Kind})", session.Kind);
        _logger.SensitiveDebug("Meeting host acknowledgement saved for session {SessionId}", session.SessionId);
    }

    // The marker comes with the session's first record, not at prepare: a warmup nobody consents to leaves no folder.
    private async Task<bool> WriteIntoSessionAsync(
        ConsentSessionMarker session, string fileName, string protectedContent, CancellationToken cancellationToken)
    {
        var sessionDir = Path.Combine(_rootDirectory, session.SessionId);
        var markerPath = Path.Combine(sessionDir, SessionMarkerFileName);
        var protectedMarker = File.Exists(markerPath)
            ? null
            : Protect(JsonSerializer.Serialize(new SessionEnvelope(SessionSchema, session), JsonOpts));

        Directory.CreateDirectory(sessionDir);
        if (protectedMarker is not null)
            await File.WriteAllTextAsync(markerPath, protectedMarker, cancellationToken).ConfigureAwait(false);

        await File.WriteAllTextAsync(Path.Combine(sessionDir, fileName), protectedContent, cancellationToken).ConfigureAwait(false);
        return protectedMarker is not null;
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

    public async Task AppendCopyAsync(string sessionId, ConsentCopy copy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(copy);
        if (!IsSessionFolderName(sessionId)) return;

        var sessionDir = Path.Combine(_rootDirectory, sessionId);
        try
        {
            await _copiesGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Without a marker nobody consented, and creating the folder here would leave one nothing sweeps.
                if (!File.Exists(Path.Combine(sessionDir, SessionMarkerFileName))) return;

                var line = Protect(JsonSerializer.Serialize(new CopyEnvelope(CopySchema, sessionId, copy), JsonOpts));
                await File.AppendAllTextAsync(
                    Path.Combine(sessionDir, CopiesFileName), line + "\n", cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _copiesGate.Release();
            }

            _logger.LogInformation("Consent copy logged ({Kind})", copy.Kind);
            _logger.SensitiveDebug("Consent copy logged for session {SessionId}: {Copy}", sessionId, copy);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to log a consent copy ({Kind})", copy.Kind);
        }
    }

    public async Task<IReadOnlyList<ConsentCopy>?> ReadCopiesAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!IsSessionFolderName(sessionId)) return [];

        var path = Path.Combine(_rootDirectory, sessionId, CopiesFileName);
        string[] lines;
        // Under the append gate: whichever side opens the file second would otherwise fail on a sharing violation.
        await _copiesGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path)) return [];
            lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // By type only: the message carries the log's path, and so the session id.
            _logger.LogWarning("Failed to read a consent copies log ({Error})", ex.GetType().Name);
            _logger.SensitiveDebug("Failed to read the copies log of session {SessionId}: {Exception}", sessionId, ex);
            return null;
        }
        finally
        {
            _copiesGate.Release();
        }

        var copies = new List<ConsentCopy>(lines.Length);
        var unreadable = 0;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var envelope = JsonSerializer.Deserialize<CopyEnvelope>(_dpapi.Decrypt(line.Trim()), JsonOpts);
                if (envelope?.Copy is { } copy) copies.Add(copy);
                else unreadable++;
            }
            catch (JsonException)
            {
                unreadable++;
            }
        }

        // A torn final line from a crash mid-append must not hide the entries before it.
        if (unreadable > 0)
            _logger.LogWarning("Skipped {Count} unreadable consent copy entries", unreadable);
        return copies;
    }

    // A session id names a folder; one read back from a user-editable note must not reach outside the root.
    internal static bool IsSessionFolderName(string? sessionId)
        => !string.IsNullOrWhiteSpace(sessionId)
           && sessionId is not ("." or "..")
           && sessionId.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

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
