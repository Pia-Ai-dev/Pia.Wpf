using System.IO;
using Microsoft.Extensions.Logging;
using Pia.Helpers;
using Pia.Infrastructure.Vault;
using Pia.Logging;
using Pia.Services.Interfaces;

namespace Pia.Services.Consent;

public sealed class ConsentCopyService : IConsentCopyService
{
    private readonly IVaultStore _vault;
    private readonly ConsentVaultScan _scan;
    private readonly IIngestScheduler _ingest;
    private readonly IAssistantChatService _chats;
    private readonly IConsentEvidenceStore _evidenceStore;
    private readonly ILogger<ConsentCopyService> _logger;

    public ConsentCopyService(
        IVaultStore vault,
        IIngestScheduler ingest,
        IAssistantChatService chats,
        IConsentEvidenceStore evidenceStore,
        ILogger<ConsentCopyService> logger)
    {
        _vault = vault;
        _scan = new ConsentVaultScan(vault, logger);
        _ingest = ingest;
        _chats = chats;
        _evidenceStore = evidenceStore;
        _logger = logger;
    }

    public async Task<ConsentCopyInventory> FindAsync(IReadOnlyList<string> sessionIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionIds);

        var scan = await _scan.ScanAsync(cancellationToken).ConfigureAwait(false);
        var notes = sessionIds
            .SelectMany(id => scan.NotesBySession.TryGetValue(id, out var references) ? references : [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var logged = new List<ConsentCopy>();
        var logUnreadable = false;
        foreach (var sessionId in sessionIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var copies = await _evidenceStore.ReadCopiesAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (copies is null) logUnreadable = true;
            else logged.AddRange(copies);
        }

        // Every session of the transcript logs the same save, so one file or chat appears once per session.
        var exports = logged
            .Where(copy => copy.Kind == ConsentCopy.ExportKind && !string.IsNullOrWhiteSpace(copy.Path))
            .GroupBy(copy => copy.Path!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.MaxBy(copy => copy.At)!)
            .OrderByDescending(copy => copy.At)
            .ToList();

        var chats = new List<ConsentCopy>();
        var loggedChats = logged
            .Where(copy => copy.Kind == ConsentCopy.ChatKind && copy.ChatId is not null)
            .GroupBy(copy => copy.ChatId!.Value)
            .Select(group => group.MaxBy(copy => copy.At)!)
            .OrderByDescending(copy => copy.At);
        foreach (var chat in loggedChats)
        {
            try
            {
                if (await _chats.ExistsAsync(chat.ChatId!.Value, cancellationToken).ConfigureAwait(false))
                    chats.Add(chat);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Failed to check a summary chat of a revoked session ({Error})", ex.GetType().Name);
            }
        }

        // A note only exists after a logged vault save, so without one an incomplete scan hides nothing.
        var vaultUnchecked = !scan.IsComplete && (logUnreadable || logged.Any(copy => copy.Kind == ConsentCopy.VaultKind));

        _logger.LogInformation(
            "Copies of a revoked transcript: exports {Exports}, vault notes {Notes}, chats {Chats}, copies log unreadable {Unreadable}, "
            + "vault unchecked {Unchecked}",
            exports.Count, notes.Count, chats.Count, logUnreadable, vaultUnchecked);
        return new ConsentCopyInventory(exports, notes, chats, logUnreadable, VaultUnchecked: vaultUnchecked);
    }

    public async Task<bool> DeleteNoteAsync(
        string reference, IReadOnlyCollection<string> sessionIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionIds);

        var named = _scan.ReadSessions(reference);
        if (named is null || !named.Any(id => sessionIds.Contains(id, StringComparer.OrdinalIgnoreCase)))
        {
            _logger.LogWarning("A vault note was not deleted: it is gone or no longer names the revoked session");
            _logger.SensitiveDebug("Vault note {Ref} not deleted: gone or no longer names the revoked session", reference);
            return false;
        }

        try
        {
            await _vault.DeleteAsync(reference).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Failed to delete a vault note of a revoked session ({Error})", ex.GetType().Name);
            _logger.SensitiveDebug("Failed to delete vault note {Ref}: {Exception}", reference, ex);
            return false;
        }

        // Queued behind any ingest in flight, which may well be this note's own.
        _ingest.RemoveAsync(reference, CancellationToken.None).SafeFireAndForget(_logger);
        _logger.LogInformation("Deleted a vault note of a revoked session");
        _logger.SensitiveDebug("Deleted vault note {Ref} of a revoked session", reference);
        return true;
    }

    public async Task<bool> DeleteChatAsync(Guid chatId, CancellationToken cancellationToken = default)
    {
        try
        {
            await _chats.DeleteAsync(chatId, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Deleted a summary chat of a revoked session");
            _logger.SensitiveDebug("Deleted summary chat {ChatId} of a revoked session", chatId);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Failed to delete a summary chat of a revoked session ({Error})", ex.GetType().Name);
            _logger.SensitiveDebug("Failed to delete summary chat {ChatId}: {Exception}", chatId, ex);
            return false;
        }
    }

    public async Task<bool> RecordRevocationAsync(
        string reference, ConsentRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var named = _scan.ReadSessions(reference);
        if (named is null) return false;

        // A note saved before a retry holds none of the later session's words, and its block must not claim them.
        var sessions = record.SessionIds.Where(id => named.Contains(id, StringComparer.OrdinalIgnoreCase)).ToList();
        var narrowed = record with
        {
            SessionIds = sessions,
            Consents = record.Consents
                .Where(consent => consent.Session is null || sessions.Contains(consent.Session, StringComparer.OrdinalIgnoreCase))
                .ToList(),
        };
        // An empty record renders no block, and writing it would strip the note's own.
        if (sessions.Count == 0 || narrowed.IsEmpty) return false;

        try
        {
            var note = await _vault.ReadAsync(reference).ConfigureAwait(false);
            if (note is null) return false;

            var rewritten = ConsentFrontMatter.ReplaceConsents(note.RawText, narrowed);
            if (string.Equals(rewritten, note.RawText, StringComparison.Ordinal)) return true;

            await _vault.WriteAtomicAsync(reference, rewritten).ConfigureAwait(false);
            _logger.LogInformation("Recorded a revocation in a kept vault note");
            _logger.SensitiveDebug("Recorded a revocation in vault note {Ref}", reference);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Failed to record a revocation in a kept vault note ({Error})", ex.GetType().Name);
            _logger.SensitiveDebug("Failed to record a revocation in vault note {Ref}: {Exception}", reference, ex);
            return false;
        }
    }
}
