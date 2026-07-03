namespace Pia.Services.Interfaces;

/// <summary>
/// Outcome of a section-aware 3-way merge (memory-vault format spec §10).
/// <paramref name="Text"/> is the fully reassembled file content; <paramref name="ConflictedSlugs"/>
/// lists the section slugs the merge could not auto-resolve (spec §10.1 rule 4 — edit-vs-delete or
/// concurrent edits). An empty list means a clean auto-merge. Conflicted bodies in
/// <paramref name="Text"/> carry the git-style conflict markers from spec §10.3.
/// </summary>
public sealed record MergeResult(string Text, IReadOnlyList<string> ConflictedSlugs);
