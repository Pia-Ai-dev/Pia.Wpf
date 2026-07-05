using Pia.Models;
using Pia.Models.Vault;

namespace Pia.Services.Interfaces;

public record MemorySummary(Guid Id, string Type, string Label);

public record RecallHit(string FilePath, string Heading, string Snippet, float Score);

/// <summary>
/// Outcome of <see cref="IMemoryService.RememberAsync"/> (vault write path). <see cref="Reference"/>
/// is a <c>path#heading</c> address (e.g. <c>memory/contacts.md#John Smith</c>) for the
/// <see cref="UpsertBand.Edit"/>/<see cref="UpsertBand.Create"/> bands, or a bare path for freeform
/// types. For <see cref="UpsertBand.Ambiguous"/> no write happened: <see cref="Reference"/> is empty
/// and <see cref="Candidates"/> (matching section slugs, score-descending) is non-empty.
/// </summary>
public record RememberOutcome(UpsertBand Band, string Reference, IReadOnlyList<string> Candidates);

/// <summary>
/// A single-pass view of the vault for the Memory screen: the section/freeform <see cref="Items"/> plus
/// <see cref="Bytes"/>, the total on-disk size of the record files backing them (the header metric).
/// Both are produced from one enumeration so the view does not re-walk the vault for the count.
/// </summary>
public record VaultMemorySnapshot(IReadOnlyList<VaultMemoryItem> Items, long Bytes);

public interface IMemoryService
{
    Task<MemoryObject> CreateObjectAsync(string type, string label, string jsonData);
    Task<MemoryObject> ImportObjectAsync(MemoryObject memory);
    Task<MemoryObject?> GetObjectAsync(Guid id);
    Task UpdateObjectAsync(Guid id, string jsonMergePatch);
    Task UpdateObjectDataAsync(Guid id, string label, string jsonData);
    Task AppendToListAsync(Guid id, string jsonEntry);
    Task DeleteObjectAsync(Guid id);
    Task<IReadOnlyList<MemoryObject>> GetObjectsByTypeAsync(string type);
    Task<IReadOnlyList<MemoryObject>> GetAllObjectsAsync();
    Task<IReadOnlyList<MemoryObject>> SearchAsync(string query);
    Task<IReadOnlyList<MemoryObject>> FullTextSearchAsync(string query);
    Task<IReadOnlyList<MemoryObject>> VectorSearchAsync(float[] queryEmbedding, int topK = 5, float threshold = 0.3f);
    Task<IReadOnlyList<MemoryObject>> HybridSearchAsync(string query, float[]? queryEmbedding = null, int topK = 10);
    Task<IReadOnlyList<RecallHit>> RecallAsync(string query, int topK = 10);

    /// <summary>
    /// Write a memory into the on-disk vault (format spec v1). Maps <paramref name="type"/> to its
    /// document/path (§7), resolves the target section via <see cref="ISectionUpsertService"/>, and
    /// either edits the matched section (deterministic bullet merge), creates a new section/file, or
    /// returns <see cref="UpsertBand.Ambiguous"/> without writing. Embeddings are NOT generated here —
    /// the vault watcher/indexer owns reindex on file change.
    /// <para>When <paramref name="createOnAmbiguous"/> is <c>false</c> (the default, interactive
    /// behavior) an <see cref="UpsertBand.Ambiguous"/> resolution performs NO write and returns the
    /// candidate slugs for the caller to disambiguate. When it is <c>true</c> the Ambiguous band is
    /// resolved deterministically as a CREATE (a new section is appended for structured types, or the
    /// file is created for freeform types) so a write always lands — the returned outcome's
    /// <see cref="RememberOutcome.Band"/> is then <see cref="UpsertBand.Create"/>, never
    /// <see cref="UpsertBand.Ambiguous"/>. The Edit and Create bands are unaffected by this flag.</para>
    /// </summary>
    Task<RememberOutcome> RememberAsync(string type, string subject, string content, bool createOnAmbiguous = false);

    /// <summary>
    /// Resolution-only twin of <see cref="RememberAsync"/>: classifies where the memory WOULD land
    /// (mapping <paramref name="type"/> to its document/path and running the same section resolution)
    /// WITHOUT writing anything. Returns the band plus the <c>path#heading</c> (or bare path) reference
    /// for the <see cref="UpsertBand.Edit"/>/<see cref="UpsertBand.Create"/> bands, or
    /// <see cref="UpsertBand.Ambiguous"/> with candidate slugs and an empty reference. The confirmation-card
    /// UX uses this to preview the action; the committing write is the subsequent <see cref="RememberAsync"/>.
    /// </summary>
    Task<RememberOutcome> ResolveRememberAsync(string type, string subject, string content);

    /// <summary>
    /// Remove a memory from the vault. A <c>path#heading</c> <paramref name="reference"/> splices out
    /// that one section (heading line + body); a bare path deletes the whole file.
    /// </summary>
    Task ForgetAsync(string reference);

    /// <summary>
    /// Enumerate the on-disk vault as view items (one per <c>##</c> section of a structured document and
    /// one per freeform/preamble file) together with the total record-file byte size — both from a single
    /// enumeration. Scoped to genuine record files (see
    /// <see cref="Pia.Infrastructure.Vault.VaultPaths.IsRecordFile"/>) — housekeeping/scaffolding and the
    /// <c>sources/</c> RAW layer are excluded. Vault-only; the legacy table is not touched.
    /// </summary>
    Task<VaultMemorySnapshot> ListMemoriesAsync();

    /// <summary>
    /// Absolute path of the vault's <c>memory/</c> folder — where the record files shown in the
    /// memory views live (the vault root also holds <c>sources/</c> and housekeeping docs). Exposed
    /// here (not via <c>IVaultStore</c>) so ViewModels can surface it — e.g. "open memory folder" —
    /// without depending on Infrastructure. Tracks folder relocation live.
    /// </summary>
    string MemoryFolderRoot { get; }

    /// <summary>
    /// Replace a vault memory's body with <paramref name="newBody"/> (the manual editor's save). A
    /// <c>path#heading</c> <paramref name="reference"/> splices that section's body — frontmatter and
    /// sibling sections are preserved byte-for-byte (§3.1), so list-valued frontmatter keys survive; a
    /// bare path replaces the freeform file's preamble body. Whole-body replace (no bullet merge), and
    /// <c>updated</c> is bumped. Embeddings reindex via the watcher, not here.
    /// </summary>
    Task UpdateSectionAsync(string reference, string newBody);
    Task UpdateEmbeddingAsync(Guid id, byte[] embedding);
    Task TouchAccessTimeAsync(Guid id);
    Task<int> GetObjectCountAsync();
    Task<long> GetStorageSizeAsync();
    Task<IReadOnlyList<MemoryObject>> GetStaleObjectsAsync(TimeSpan staleThreshold);
    Task<string> ExportAllAsync();
    Task<IReadOnlyList<MemorySummary>> GetMemorySummariesAsync(string? typeFilter = null);
}
