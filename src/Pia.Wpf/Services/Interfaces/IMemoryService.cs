using Pia.Models;
using Pia.Models.Vault;

namespace Pia.Services.Interfaces;

public record MemorySummary(Guid Id, string Type, string Label);

public record RecallHit(string FilePath, string Heading, string Snippet, float Score)
{
    /// <summary>
    /// The navigation tier of this hit, derived from its path: <c>"topic"</c> for a synthesized topic
    /// page under <c>memory/topics/</c> (expandable to its full body + cited sources via
    /// <c>read_topic</c>), else <c>"record"</c>. Binary on purpose — the recall pool holds notes and
    /// projects too (only <c>sources/</c> is excluded), so this is topic-vs-record, NOT the §7 record
    /// type. Get-only so <see cref="System.Text.Json"/> emits it to the model with no construction-site
    /// churn.
    /// </summary>
    public string Tier =>
        FilePath.StartsWith("memory/topics/", StringComparison.OrdinalIgnoreCase) ? "topic" : "record";
}

/// <summary>One topic/record in the <c>browse_index</c> orientation map: a display <see cref="Title"/>,
/// a one-line <see cref="Summary"/> so the map can be triaged without opening every page, and the
/// vault-relative <see cref="Ref"/> handle (e.g. <c>memory/topics/foo.md</c>) that chains straight
/// into <c>read_topic</c>.</summary>
public record BrowseEntry(string Title, string Ref, string Summary);

/// <summary>A category group in the <c>browse_index</c> map (a §8 canonical type, or a topic category
/// such as <c>person</c>), with its display heading and its entries.</summary>
public record BrowseCategory(string Category, string Display, IReadOnlyList<BrowseEntry> Entries);

/// <summary>The vault's category → topic/record map, built programmatically (never by reading
/// <c>index.md</c>) — the orient rung of the navigation loop.</summary>
public record BrowseIndexResult(IReadOnlyList<BrowseCategory> Categories);

/// <summary>
/// The read rung: a topic page's full body plus its cited <see cref="Sources"/> refs and resolvable
/// outbound <see cref="Wikilinks"/> (both are <c>read_source</c>/<c>read_topic</c> handles). <see cref="Sources"/>
/// is surfaced even when empty so a topic with stale/missing provenance fails visibly. On a rejected ref
/// (outside the vault, or not recall-visible) or a miss, <see cref="Found"/> is <c>false</c> and
/// <see cref="Error"/> explains why.
/// </summary>
public record TopicRead(
    bool Found, string Ref, string Title, string Body,
    IReadOnlyList<string> Sources, IReadOnlyList<string> Wikilinks, string? Error);

/// <summary>
/// The drill rung: a raw primary source's text, reached only via a topic's cited ref (traversal-only).
/// <see cref="Truncated"/> is set when the source exceeded the line window / output cap. On a rejected
/// ref (outside <c>sources/</c>, escapes containment, non-text, or missing) <see cref="Found"/> is
/// <c>false</c> and <see cref="Error"/> explains why.
/// </summary>
public record SourceRead(bool Found, string Ref, string Text, bool Truncated, string? Error);

/// <summary>
/// Resolution-only preview for <see cref="IMemoryService.UpdateSourceAsync"/>: validates the same
/// guard chain as <see cref="IMemoryService.ReadSourceAsync"/> and, when the ref resolves to an
/// existing source, reads its current text and last-write time as the diff/TOCTOU baseline. No write
/// happens here. On a rejected/missing ref <see cref="CanWrite"/> is <c>false</c> and <see cref="Error"/>
/// explains why.
/// </summary>
public record SourceUpdatePreview(bool CanWrite, string Ref, string OldContent, DateTime? Mtime, string? Error);

/// <summary>Outcome of <see cref="IMemoryService.UpdateSourceAsync"/> or <see cref="IMemoryService.CreateSourceAsync"/>.</summary>
public record SourceWrite(bool Success, string Ref, string? Error);

/// <summary>
/// Resolution-only preview for <see cref="IMemoryService.CreateSourceAsync"/>: validates the same
/// scope guard as <see cref="IMemoryService.ReadSourceAsync"/>, but requires the ref NOT already exist
/// — the mirror of <see cref="IMemoryService.ResolveUpdateSourceAsync"/>'s existing-only rule. No write
/// happens here. On a rejected/colliding ref <see cref="CanWrite"/> is <c>false</c> and
/// <see cref="Error"/> explains why.
/// </summary>
public record SourceCreatePreview(bool CanWrite, string Ref, string? Error);

/// <summary>
/// The <c>recall</c> tool's result shape: the ranked <see cref="Hits"/> plus a standing <see cref="Note"/>
/// telling the model topic hits are summaries expandable via <c>read_topic</c>/<c>read_source</c>. This
/// wrapper lives only at the tool boundary — <see cref="IMemoryService.RecallAsync"/> still returns the
/// bare hit list, which the Vault view consumes directly. (A result-DTO record, co-located with
/// <see cref="RecallHit"/> under Services.Interfaces — the sanctioned home for return-shape carriers.)
/// </summary>
public record RecallResult(IReadOnlyList<RecallHit> Hits, string Note);

/// <summary>
/// Outcome of <see cref="IMemoryService.RememberAsync"/> (vault write path). <see cref="Reference"/>
/// is a <c>path#heading</c> address (e.g. <c>memory/contacts.md#John Smith</c>) for the
/// <see cref="UpsertBand.Edit"/>/<see cref="UpsertBand.Create"/> bands, or a bare path for freeform
/// types. For <see cref="UpsertBand.Ambiguous"/> no write happened: <see cref="Reference"/> is empty
/// and <see cref="Candidates"/> (matching section slugs, score-descending) is non-empty.
/// </summary>
public record RememberOutcome(UpsertBand Band, string Reference, IReadOnlyList<string> Candidates);

/// <summary>
/// A single-pass projection of the vault for the Vault view: the section/freeform <see cref="Items"/> plus
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
    /// Orient rung of the vault navigation loop: the category → topic/record map, grouped by §8
    /// <see cref="Pia.Services.Wiki.VaultIndexService.CanonicalGroups"/> (topics sub-grouped by
    /// <see cref="Pia.Services.Wiki.VaultIndexService.TopicCategories"/>), each entry carrying a
    /// <c>read_topic</c> handle. Built from <see cref="ListMemoriesAsync"/> — never by reading
    /// <c>index.md</c> (which is recall-denylisted).
    /// </summary>
    Task<BrowseIndexResult> BrowseIndexAsync();

    /// <summary>
    /// Read rung: the full body of the recall-visible page at <paramref name="reference"/> (the same
    /// <see cref="RecallHit.FilePath"/> handle recall emits) plus its cited <c>sources:</c> refs and
    /// resolvable outbound wikilinks. Guarded by BOTH containment (stays inside the vault) AND
    /// <see cref="Pia.Infrastructure.Vault.VaultPaths.IsRecallIndexable"/> (recall-visible: excludes
    /// <c>sources/</c>, <c>.archive/</c>, and housekeeping). A rejected/missing ref returns
    /// <see cref="TopicRead.Found"/> = <c>false</c>.
    /// </summary>
    Task<TopicRead> ReadTopicAsync(string reference);

    /// <summary>
    /// Drill rung: read a raw primary source under <c>sources/</c> (traversal-only — the ref comes from a
    /// topic's <c>sources:</c> provenance). Guarded by containment + a <c>sources/</c>-scope assertion.
    /// Bounded by a 1&#160;MB input ceiling and an <paramref name="offset"/>/<paramref name="limit"/> line
    /// window (default 500, max 2000) for chunking large logs. A rejected/missing/non-text ref returns
    /// <see cref="SourceRead.Found"/> = <c>false</c>.
    /// </summary>
    Task<SourceRead> ReadSourceAsync(string reference, int? offset = null, int? limit = null);

    /// <summary>
    /// Resolution-only twin of <see cref="UpdateSourceAsync"/>: runs <see cref="ReadSourceAsync"/>'s
    /// guard chain (containment, <c>sources/</c>-scope, sensitive-path, text-extension, size ceiling,
    /// must already exist) and, on success, returns the current content and last-write time as the
    /// baseline for a diff preview and the TOCTOU check in <see cref="UpdateSourceAsync"/>. No write.
    /// </summary>
    Task<SourceUpdatePreview> ResolveUpdateSourceAsync(string reference);

    /// <summary>
    /// Correct an existing raw source under <c>sources/</c> in place — the one sanctioned exception to
    /// the RAW layer otherwise being read-only to Pia. Re-validates the same guard chain as
    /// <see cref="ResolveUpdateSourceAsync"/> (the vault root may have changed between preview and
    /// approval), then, if <paramref name="expectedMtime"/> is supplied and no longer matches the file
    /// on disk, refuses (the approved diff no longer matches current content) rather than clobbering an
    /// out-of-band change. Preserves the source's original EOL style and BOM (<see cref="Pia.Infrastructure.AtomicTextWriter"/>) since
    /// this is a user-authored file, not a Pia-managed page. Does not re-ingest — the caller re-ingests
    /// via <c>IIngestScheduler</c> after a successful write.
    /// </summary>
    Task<SourceWrite> UpdateSourceAsync(string reference, string content, DateTime? expectedMtime);

    /// <summary>
    /// Resolution-only twin of <see cref="CreateSourceAsync"/>: runs the same scope guard as
    /// <see cref="ReadSourceAsync"/> (containment, <c>sources/</c>-scope, sensitive-path, text-extension)
    /// but requires the ref NOT already exist — the mirror of <see cref="ResolveUpdateSourceAsync"/>'s
    /// existing-only rule. No write.
    /// </summary>
    Task<SourceCreatePreview> ResolveCreateSourceAsync(string reference);

    /// <summary>
    /// Stage a brand-new raw source under <c>sources/</c> — unlike the general file tools, this
    /// resolves against the vault root directly, so it works regardless of the active chat's working
    /// directory scope. Re-validates the same guard chain as <see cref="ResolveCreateSourceAsync"/>
    /// (the vault root may have changed between preview and approval) and refuses if a file now exists
    /// where none did when the create was previewed (nothing to compare an mtime against — existence
    /// alone is the collision signal). Creates the parent directory if a nested ref needs one. Does not
    /// re-ingest — the caller re-ingests via <c>IIngestScheduler</c> after a successful write.
    /// </summary>
    Task<SourceWrite> CreateSourceAsync(string reference, string content);

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
    /// Absolute path of the vault root — the user-facing "memory vault" folder holding both
    /// <c>memory/</c> (the records shown in the Vault view) and <c>sources/</c> (the RAW layer,
    /// read-only to Pia except for a corrective <see cref="UpdateSourceAsync"/>). Exposed here (not via
    /// <c>IVaultStore</c>) so ViewModels can surface it — e.g. "open memory vault" — without depending
    /// on Infrastructure. Tracks folder relocation live.
    /// </summary>
    string VaultRoot { get; }

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
