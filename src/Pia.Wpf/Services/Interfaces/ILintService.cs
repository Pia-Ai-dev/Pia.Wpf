namespace Pia.Services.Interfaces;

/// <summary>
/// The kind of coherence issue a lint pass found (Task 7.2). FLAG kinds are reported for a human/agent
/// to resolve; AUTO-FIX kinds (<see cref="MissingXref"/>, <see cref="Duplicate"/>) are repaired in place
/// by the lint run and reported with <see cref="LintFinding.AutoFixed"/> set.
/// </summary>
public enum LintKind
{
    /// <summary>The same entity field key has different values across pages (FLAG).</summary>
    Contradiction,

    /// <summary>A page's <c>sources:</c> ref points at a source file that no longer exists (FLAG).</summary>
    Stale,

    /// <summary>A topic page that no other page body links to (FLAG).</summary>
    Orphan,

    /// <summary>A page mentions an entity that has its own page but is not linked (AUTO-FIX: link inserted).</summary>
    MissingXref,

    /// <summary>Two topic pages whose embeddings are near-identical (AUTO-FIX: one archived/merged).</summary>
    Duplicate,

    /// <summary>A <c>[[wikilink]]</c> whose target page does not exist (FLAG).</summary>
    GapPage,
}

/// <summary>
/// One coherence issue found by a lint pass. <see cref="Detail"/> is a human-readable, vault-relative
/// description (treated as sensitive). <see cref="AutoFixed"/> is <c>true</c> only when the lint run
/// repaired the issue itself (MissingXref link insertion, Duplicate archive/merge).
/// </summary>
public record LintFinding(LintKind Kind, string Detail, bool AutoFixed);

/// <summary>The result of a single lint pass — every finding it produced, in discovery order.</summary>
public record LintReport(IReadOnlyList<LintFinding> Findings);

/// <summary>
/// The coherence lint pass (Task 7.2): a whole-vault sweep over <c>memory/</c> pages that detects
/// contradictions, stale source refs, orphans, missing cross-references, near-duplicate pages, and gap
/// pages — flagging FLAG kinds and repairing AUTO-FIX kinds in place. Every finding is journaled to
/// <c>memory/log.md</c>. Runs on demand via <see cref="RunAsync"/>; a scheduled / run-after-N-ingests
/// trigger is deferred.
/// </summary>
public interface ILintService
{
    Task<LintReport> RunAsync(DateOnly date, CancellationToken ct = default);
}
