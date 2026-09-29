namespace Pia.Services.Consent;

/// <summary>The copies a revoked session's transcript went into, and deleting or annotating the ones Pia manages.</summary>
public interface IConsentCopyService
{
    Task<ConsentCopyInventory> FindAsync(IReadOnlyList<string> sessionIds, CancellationToken cancellationToken = default);

    /// <summary>Deletes nothing unless the note sits in the vault and its front matter still names one of the sessions.</summary>
    Task<bool> DeleteNoteAsync(string reference, IReadOnlyCollection<string> sessionIds, CancellationToken cancellationToken = default);

    Task<bool> DeleteChatAsync(Guid chatId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes <paramref name="record"/> into the note's consent block, narrowed to the sessions the note names; a
    /// speaker the note already lists keeps its <c>shownAs</c>.
    /// </summary>
    Task<bool> RecordRevocationAsync(string reference, ConsentRecord record, CancellationToken cancellationToken = default);
}

/// <param name="Exports">One per file, latest save first. Pia cannot reach them.</param>
/// <param name="Notes">Vault-relative notes whose front matter names one of the sessions.</param>
/// <param name="Chats">Summary chats that are still stored, latest first.</param>
/// <param name="CopiesLogUnreadable">A session's copies log could not be read, so exports and chats may be missing.</param>
/// <param name="VaultUnchecked">A note could not be read or the vault is not there, so notes may be missing.</param>
public sealed record ConsentCopyInventory(
    IReadOnlyList<ConsentCopy> Exports,
    IReadOnlyList<string> Notes,
    IReadOnlyList<ConsentCopy> Chats,
    bool CopiesLogUnreadable,
    bool VaultUnchecked = false)
{
    public bool IsEmpty =>
        Exports.Count == 0 && Notes.Count == 0 && Chats.Count == 0 && !CopiesLogUnreadable && !VaultUnchecked;
}
