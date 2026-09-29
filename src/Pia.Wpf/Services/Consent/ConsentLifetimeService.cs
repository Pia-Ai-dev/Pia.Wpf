using System.IO;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Infrastructure.Vault;
using Pia.Logging;
using Pia.Services.Interfaces;
using Pia.Services.Wiki;

namespace Pia.Services.Consent;

/// <summary>
/// Deletes a v2 session's evidence folder once no copy Pia manages is left: a vault note naming the session, a
/// topic page derived from one, or a summary chat. Roots are parameters so a test never touches the real profile.
/// </summary>
public sealed class ConsentLifetimeService : IDisposable
{
    // The chat id is reported before the chat's row is written, so a young summary entry cannot be checked yet.
    internal static readonly TimeSpan SummaryGrace = TimeSpan.FromMinutes(10);

    private readonly string _evidenceRoot;
    private readonly IVaultStore _vault;
    private readonly ConsentVaultScan _scan;
    private readonly IngestStateStore _ingestState;
    private readonly IAssistantChatService _chats;
    private readonly IConsentEvidenceStore _evidenceStore;
    private readonly IDirectTranscriptionService _transcription;
    private readonly IConsentLiveSessions _liveSessions;
    private readonly TimeProvider _clock;
    private readonly bool _dataRootsOverridden;
    private readonly Func<bool> _isShuttingDown;
    private readonly ILogger<ConsentLifetimeService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _overrideLogged;

    /// <param name="dataRootsOverridden">The vault and the chats then live in a throwaway profile while the evidence
    /// folder does not, so nothing may be judged by them.</param>
    public ConsentLifetimeService(
        string evidenceRoot,
        IVaultStore vault,
        IngestStateStore ingestState,
        IAssistantChatService chats,
        IConsentEvidenceStore evidenceStore,
        IDirectTranscriptionService transcription,
        IConsentLiveSessions liveSessions,
        TimeProvider clock,
        bool dataRootsOverridden,
        Func<bool> isShuttingDown,
        ILogger<ConsentLifetimeService> logger)
    {
        _evidenceRoot = evidenceRoot;
        _vault = vault;
        _scan = new ConsentVaultScan(vault, logger);
        _ingestState = ingestState;
        _chats = chats;
        _evidenceStore = evidenceStore;
        _transcription = transcription;
        _liveSessions = liveSessions;
        _clock = clock;
        _dataRootsOverridden = dataRootsOverridden;
        _isShuttingDown = isShuttingDown;
        _logger = logger;

        _transcription.SessionEnded += OnSessionEnded;
        _liveSessions.SessionEnded += OnSessionEnded;
    }

    /// <summary>Judges every v2 folder except those of the transcript still open and of a recording still running.</summary>
    public async Task<ConsentLifetimeOutcome> SweepAsync(CancellationToken cancellationToken = default)
    {
        if (IsSuspended()) return ConsentLifetimeOutcome.None;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var folders = ListSessionFolders();
            if (folders.Count == 0) return ConsentLifetimeOutcome.None;

            // Read after listing: a session that starts in between would otherwise have a folder but no live id.
            var live = new HashSet<string>(_transcription.TranscriptSessionIds, StringComparer.OrdinalIgnoreCase);
            live.UnionWith(_liveSessions.Snapshot());
            var ended = folders.Where(folder => !live.Contains(Path.GetFileName(folder))).ToList();

            var outcome = await JudgeAsync(ended, cancellationToken).ConfigureAwait(false);
            outcome = outcome with { Kept = outcome.Kept + folders.Count - ended.Count };
            LogOutcome("sweep", outcome);
            return outcome;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Deletes the ended session's folder unless a managed copy of its transcript exists.</summary>
    public async Task<ConsentLifetimeOutcome> HandleSessionEndedAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!ConsentEvidenceStore.IsSessionFolderName(sessionId) || IsSuspended()) return ConsentLifetimeOutcome.None;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var folder = Path.Combine(_evidenceRoot, sessionId);
            // No marker: nobody consented, or a v1 folder, which ages out on the retention window instead.
            if (!File.Exists(Path.Combine(folder, ConsentEvidenceStore.SessionMarkerFileName)))
                return ConsentLifetimeOutcome.None;

            var outcome = await JudgeAsync([folder], cancellationToken).ConfigureAwait(false);
            LogOutcome("session end", outcome);
            return outcome;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _transcription.SessionEnded -= OnSessionEnded;
        _liveSessions.SessionEnded -= OnSessionEnded;
    }

    // Raised under the transcription service's start/stop lock, by a recording's end, and by window teardown at exit:
    // nothing here may call back into the raiser or block it, and at exit the next startup sweep takes the folder.
    private void OnSessionEnded(object? sender, string sessionId)
    {
        if (_isShuttingDown()) return;

        Task.Run(() => HandleSessionEndedAsync(sessionId)).SafeFireAndForget(_logger);
    }

    private bool IsSuspended()
    {
        if (_dataRootsOverridden)
        {
            if (Interlocked.Exchange(ref _overrideLogged, 1) == 0)
                _logger.LogInformation("Consent evidence lifetime is off: the data directories are overridden");
            return true;
        }

        return _isShuttingDown();
    }

    private List<string> ListSessionFolders()
    {
        try
        {
            if (!Directory.Exists(_evidenceRoot)) return [];
            return Directory.GetDirectories(_evidenceRoot)
                .Where(folder => File.Exists(Path.Combine(folder, ConsentEvidenceStore.SessionMarkerFileName)))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to list the consent evidence folders");
            return [];
        }
    }

    private async Task<ConsentLifetimeOutcome> JudgeAsync(IReadOnlyList<string> folders, CancellationToken cancellationToken)
    {
        if (folders.Count == 0) return ConsentLifetimeOutcome.None;

        var scan = await _scan.ScanAsync(cancellationToken).ConfigureAwait(false);
        var now = _clock.GetUtcNow();
        int kept = 0, deleted = 0, skipped = 0;

        foreach (var folder in folders)
        {
            var sessionId = Path.GetFileName(folder);
            var liveness = await AssessAsync(sessionId, scan, now, cancellationToken).ConfigureAwait(false);
            if (liveness is Liveness.Alive)
            {
                kept++;
                _logger.SensitiveDebug("Consent evidence of session {SessionId} kept", sessionId);
                continue;
            }

            // Deletion cannot be undone, so a folder the evidence cannot rule on stays for the next sweep.
            if (liveness is Liveness.Unknown || cancellationToken.IsCancellationRequested || _isShuttingDown())
            {
                skipped++;
                continue;
            }

            try
            {
                Directory.Delete(folder, recursive: true);
                deleted++;
                _logger.SensitiveDebug("Consent evidence of session {SessionId} deleted", sessionId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++;
                // By type only: the message carries the folder's path, and so the session id.
                _logger.LogWarning("Failed to delete a consent evidence folder ({Error})", ex.GetType().Name);
                _logger.SensitiveDebug("Failed to delete the evidence of session {SessionId}: {Exception}", sessionId, ex);
            }
        }

        return new ConsentLifetimeOutcome(kept, deleted, skipped, scan.NotesScanned, scan.NotesUnreadable);
    }

    private async Task<Liveness> AssessAsync(
        string sessionId, ConsentVaultScanResult scan, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (scan.NotesBySession.ContainsKey(sessionId)) return Liveness.Alive;

        var copies = await _evidenceStore.ReadCopiesAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (copies is null) return Liveness.Unknown;

        var undecided = !scan.IsComplete;
        foreach (var copy in copies)
        {
            try
            {
                switch (copy.Kind)
                {
                    case ConsentCopy.SummaryRequestedKind when now - copy.At < SummaryGrace:
                        return Liveness.Alive;
                    case ConsentCopy.ChatKind when copy.ChatId is { } chatId:
                        if (now - copy.At < SummaryGrace
                            || await _chats.ExistsAsync(chatId, cancellationToken).ConfigureAwait(false))
                            return Liveness.Alive;
                        break;
                    case ConsentCopy.VaultKind when copy.Ref is { } reference:
                        if (await HasDerivedPageAsync(reference).ConfigureAwait(false))
                            return Liveness.Alive;
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                undecided = true;
                _logger.LogWarning("Failed to check a consent copy ({Kind}, {Error})", copy.Kind, ex.GetType().Name);
                _logger.SensitiveDebug("Failed to check a copy of session {SessionId}: {Exception}", sessionId, ex);
            }
        }

        return undecided ? Liveness.Unknown : Liveness.Gone;
    }

    // Any page left keeps the note alive: the ingest's own gate wants all of them, but it only re-runs a source.
    private async Task<bool> HasDerivedPageAsync(string reference)
    {
        var entry = await _ingestState.GetAsync(reference.Trim().Replace('\\', '/').TrimStart('/')).ConfigureAwait(false);
        if (entry is null) return false;

        foreach (var page in entry.TouchedPages)
        {
            if (await _vault.ReadAsync(page).ConfigureAwait(false) is not null) return true;
        }
        return false;
    }

    private void LogOutcome(string trigger, ConsentLifetimeOutcome outcome) =>
        _logger.LogInformation(
            "Consent evidence lifetime ({Trigger}): sessions kept {Kept}, deleted {Deleted}, skipped {Skipped}; "
            + "vault notes scanned {Scanned}, unreadable {Unreadable}",
            trigger, outcome.Kept, outcome.Deleted, outcome.Skipped, outcome.NotesScanned, outcome.NotesUnreadable);

    private enum Liveness
    {
        Alive,
        Gone,
        Unknown,
    }
}

/// <summary>Skipped means the folder could not be judged or deleted and waits for the next sweep.</summary>
public sealed record ConsentLifetimeOutcome(int Kept, int Deleted, int Skipped, int NotesScanned, int NotesUnreadable)
{
    public static ConsentLifetimeOutcome None { get; } = new(0, 0, 0, 0, 0);
}
