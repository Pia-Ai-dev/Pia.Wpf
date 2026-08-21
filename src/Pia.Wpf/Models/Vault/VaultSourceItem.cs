namespace Pia.Models.Vault;

/// <summary>
/// One file of the vault's <c>sources/</c> RAW layer (read-only except for a corrective
/// <c>update_source</c>) as the Vault view consumes it.
/// <see cref="RelativePath"/> is vault-root-relative with forward slashes (<c>sources/q2-report.txt</c>)
/// — the same spelling the ingest tool takes as <c>source_ref</c> and records as provenance.
/// <see cref="TopicPageCount"/> is the number of <c>memory/topics/</c> pages whose <c>sources:</c>
/// frontmatter records this file (0 = staged but not yet compiled). <see cref="IsText"/> mirrors
/// ingest's text-extension test: non-text sources cannot be ingested until binary handling lands.
/// </summary>
public sealed record VaultSourceItem(
    string RelativePath, string Name, long Bytes, DateTime Modified, bool IsText, int TopicPageCount)
{
    /// <summary>True when at least one topic page records this source as provenance.</summary>
    public bool IsIngested => TopicPageCount > 0;
}
