using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure.Vault;
using Pia.Logging;
using Pia.Models.Vault;

namespace Pia.Services.Wiki;

/// <summary>
/// Maintains <c>memory/index.md</c> — the per-page catalog of spec §8. Renamed from the plan's
/// <c>VaultIndexFile</c> to the <c>Service</c> suffix so it satisfies <c>NamingConventionTests</c>
/// without an allowlist change, and is a concrete singleton (no interface) so it does not trip
/// <c>DiRegistrationTests</c>.
///
/// <para>Every edit re-reads the existing catalog, applies the upsert/remove, then REWRITES the whole
/// file deterministically (sorted) so the result is stable under re-runs (§8). The <c>id</c> in
/// frontmatter is reused if the file already exists, else a fresh lowercase-canonical GUID; <c>created</c>
/// is preserved if present; <c>updated</c> is bumped to now (§2.5).</para>
/// </summary>
public sealed class VaultIndexService
{
    private const string IndexPath = "memory/index.md";
    private const string TimestampFormat = "yyyy-MM-ddTHH:mm:ssZ";

    // §8 canonical type order with the spec's display-name headings. NOT MemoryObjectTypes.GetDisplayName,
    // which renders "Notes & Knowledge"/"Contacts" and lacks a `topic` row — §8's example uses the short
    // display names "Notes"/"Topics" and is the byte-for-byte authority. Public so the Memory view groups
    // by the same authoritative order/display names rather than re-deriving them.
    public static readonly (string Type, string Display)[] CanonicalGroups =
    [
        ("personal_profile", "Personal Profile"),
        ("contact_list", "Contacts"),
        ("preference", "Preferences"),
        ("note", "Notes"),
        ("project", "Projects"),
        ("topic", "Topics"),
    ];

    // The bucket for topics whose frontmatter `category` is missing or unrecognized. Public so the
    // Memory view uses the same fallback key when it elevates category to a top-level group.
    public const string DefaultCategory = "other";

    // §8 canonical category order + display headings for sub-grouping the `## Topics` group. Mirrors
    // the ingest extractor's category vocabulary; any page whose `category` is missing/unrecognized
    // falls under "Other". Public so the Memory view groups topics by the same authoritative
    // order/display names rather than re-deriving them.
    public static readonly (string Category, string Display)[] TopicCategories =
    [
        ("person", "People"),
        ("organization", "Organizations"),
        ("product", "Products"),
        ("concept", "Concepts"),
        ("regulation", "Regulations"),
        ("technology", "Technology"),
        (DefaultCategory, "Other"),
    ];

    private static readonly HashSet<string> KnownCategories =
        new(TopicCategories.Select(c => c.Category), StringComparer.Ordinal);

    /// <summary>
    /// Normalizes a raw frontmatter <c>category</c> value to one of the known <see cref="TopicCategories"/>
    /// keys, falling back to <see cref="DefaultCategory"/> when missing or unrecognized. Shared with the
    /// Memory view so its top-level topic grouping mirrors the index sub-grouping.
    /// </summary>
    public static string NormalizeTopicCategory(string? category)
    {
        if (!string.IsNullOrWhiteSpace(category))
        {
            var normalized = category.Trim().ToLowerInvariant();
            if (KnownCategories.Contains(normalized))
            {
                return normalized;
            }
        }

        return DefaultCategory;
    }

    private readonly IVaultStore _store;
    private readonly ILogger<VaultIndexService> _logger;

    public VaultIndexService(IVaultStore store, ILogger<VaultIndexService> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>Set (or replace) the catalog summary for <paramref name="path"/> and rewrite §8.</summary>
    public async Task UpsertEntryAsync(string path, string summary)
    {
        var target = LinkTarget(path);
        if (target is null)
        {
            // Housekeeping files (index/log/AGENTS) and anything off the canonical type map are never
            // cataloged — silently ignore (the catalog is a derived artifact, not an error surface).
            _logger.SensitiveDebug("Skipping non-cataloged path in index upsert {Path}", path);
            return;
        }

        var entries = await ReadEntriesAsync();
        entries[target] = OneLine(summary);
        await RewriteAsync(entries);
        _logger.SensitiveDebug("Upserted index entry {Target}", target);
    }

    /// <summary>Drop the catalog entry for <paramref name="path"/> (if any) and rewrite §8.</summary>
    public async Task RemoveEntryAsync(string path)
    {
        var target = LinkTarget(path);
        if (target is null)
        {
            return;
        }

        var entries = await ReadEntriesAsync();
        if (entries.Remove(target))
        {
            await RewriteAsync(entries);
            _logger.SensitiveDebug("Removed index entry {Target}", target);
        }
    }

    // ---- catalog state (link target -> one-line summary), parsed from the existing index ----

    private async Task<SortedDictionary<string, string>> ReadEntriesAsync()
    {
        var entries = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var doc = await _store.ReadAsync(IndexPath);
        if (doc is null)
        {
            return entries;
        }

        // Each section body holds the group's entry lines. Re-derive target+summary from each line so
        // the catalog round-trips regardless of which group heading it currently sits under.
        var lines = SplitLines(doc.Preamble);
        foreach (var section in doc.Sections)
        {
            CollectEntries(SplitLines(section.Body), entries);
        }

        CollectEntries(lines, entries);
        return entries;
    }

    private static void CollectEntries(IEnumerable<string> lines, IDictionary<string, string> into)
    {
        foreach (var line in lines)
        {
            if (TryParseEntry(line, out var target, out var summary))
            {
                into[target] = summary;
            }
        }
    }

    // Parse "- [[<target>]] — <summary>" (em dash surrounded by single spaces).
    private static bool TryParseEntry(string line, out string target, out string summary)
    {
        target = string.Empty;
        summary = string.Empty;

        const string open = "- [[";
        const string close = "]] — ";
        if (!line.StartsWith(open, StringComparison.Ordinal))
        {
            return false;
        }

        var closeIdx = line.IndexOf(close, open.Length, StringComparison.Ordinal);
        if (closeIdx < 0)
        {
            return false;
        }

        target = line[open.Length..closeIdx];
        summary = line[(closeIdx + close.Length)..];
        return target.Length > 0;
    }

    // ---- deterministic §8 rewrite ----

    private async Task RewriteAsync(SortedDictionary<string, string> entries)
    {
        var existing = await _store.ReadAsync(IndexPath);
        var sb = new StringBuilder();
        sb.Append(BuildFrontmatter(existing));
        sb.Append("# Index\n");

        foreach (var (type, display) in CanonicalGroups)
        {
            // Entries belonging to this group, already ascending-ordinal because `entries` is sorted.
            List<KeyValuePair<string, string>>? group = null;
            foreach (var entry in entries)
            {
                if (TypeForTarget(entry.Key) == type)
                {
                    (group ??= []).Add(entry);
                }
            }

            if (group is null)
            {
                continue;
            }

            sb.Append('\n');
            sb.Append("## ").Append(display).Append('\n');

            if (type == "topic")
            {
                await AppendTopicSubGroupsAsync(sb, group);
            }
            else
            {
                foreach (var (target, summary) in group)
                {
                    AppendEntry(sb, target, summary);
                }
            }
        }

        await _store.WriteAtomicAsync(IndexPath, sb.ToString());
    }

    // §8: within the path-derived `## Topics` group, sub-group entries by each page's frontmatter
    // `category` under canonically-ordered `### ...` headings, reading the page at rewrite time. Pages
    // with a missing/unknown category land under `### Other`. Reading N pages per rewrite is acceptable
    // (ingest is serial/background; topic count is small).
    private async Task AppendTopicSubGroupsAsync(StringBuilder sb, List<KeyValuePair<string, string>> group)
    {
        var byCategory = new Dictionary<string, List<KeyValuePair<string, string>>>(StringComparer.Ordinal);
        foreach (var entry in group)
        {
            var category = await CategoryForTargetAsync(entry.Key);
            (byCategory.TryGetValue(category, out var bucket) ? bucket : byCategory[category] = []).Add(entry);
        }

        foreach (var (category, display) in TopicCategories)
        {
            if (!byCategory.TryGetValue(category, out var bucket))
            {
                continue;
            }

            sb.Append("### ").Append(display).Append('\n');
            foreach (var (target, summary) in bucket)
            {
                AppendEntry(sb, target, summary);
            }
        }
    }

    private static void AppendEntry(StringBuilder sb, string target, string summary) =>
        sb.Append("- [[").Append(target).Append("]] — ").Append(summary).Append('\n');

    // Reads the topic page's frontmatter `category`, normalized to a known key; missing/unknown → "other".
    private async Task<string> CategoryForTargetAsync(string target)
    {
        var doc = await _store.ReadAsync("memory/" + target + ".md");
        var category = doc is not null && doc.Frontmatter.TryGetValue("category", out var raw) ? raw : null;
        return NormalizeTopicCategory(category);
    }

    // Frontmatter keys Pia owns on index.md; everything else is a user/Obsidian addition we must
    // preserve verbatim on rewrite (spec §2.3).
    private static readonly HashSet<string> OwnedKeys = new(StringComparer.Ordinal)
    {
        "pia", "id", "type", "title", "created", "updated", "schemaVersion",
    };

    private static string BuildFrontmatter(VaultDocument? existing)
    {
        var id = existing is not null && existing.Frontmatter.TryGetValue("id", out var existingId)
                 && !string.IsNullOrWhiteSpace(existingId)
            ? existingId
            : Guid.NewGuid().ToString("D").ToLowerInvariant();

        var now = DateTime.UtcNow.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        var created = existing is not null && existing.Frontmatter.TryGetValue("created", out var existingCreated)
                      && !string.IsNullOrWhiteSpace(existingCreated)
            ? existingCreated
            : now;

        var sb = new StringBuilder();
        sb.Append("---\n")
          .Append("pia: managed\n")
          .Append("id: ").Append(id).Append('\n')
          .Append("type: note\n")
          .Append("title: Index\n")
          .Append("created: ").Append(created).Append('\n')
          .Append("updated: ").Append(now).Append('\n')
          .Append("schemaVersion: 1\n");

        // §2.3: carry through unknown (user-added) frontmatter keys on rewrite. Only single-line
        // scalar values round-trip — the parser flattens YAML lists/maps to a non-reversible string,
        // so complex unknown keys cannot be preserved here (a known parser limitation, see plan notes).
        if (existing is not null)
        {
            foreach (var (key, value) in existing.Frontmatter)
            {
                if (!OwnedKeys.Contains(key) && !value.Contains('\n'))
                {
                    sb.Append(key).Append(": ").Append(value).Append('\n');
                }
            }
        }

        sb.Append("---\n");
        return sb.ToString();
    }

    // ---- path -> link target / type derivation (§7 storage map) ----

    /// <summary>
    /// Vault-relative link target (path without the <c>.md</c> extension, '/'-separated) for a cataloged
    /// page, or <c>null</c> for housekeeping files (index/log/AGENTS) and uncataloged paths. The target
    /// is the §5 wikilink form sans extension; the page type is derived from the same path (§7):
    /// <c>memory/profile.md</c>→personal_profile, <c>contacts.md</c>→contact_list,
    /// <c>preferences.md</c>→preference, <c>notes/*</c>→note, <c>projects/*</c>→project,
    /// <c>topics/*</c>→topic.
    /// </summary>
    private static string? LinkTarget(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith("memory/", StringComparison.Ordinal))
        {
            normalized = normalized["memory/".Length..];
        }

        if (!normalized.EndsWith(".md", StringComparison.Ordinal))
        {
            return null;
        }

        var target = normalized[..^3];

        // Housekeeping files are never catalog entries (§8 catalogs pages, not the catalog/journal/schema).
        if (target is "index" or "log" or "AGENTS")
        {
            return null;
        }

        return TypeForTarget(target) is null ? null : target;
    }

    /// <summary>Canonical §7 type for a link target (path-without-ext), or <c>null</c> if uncataloged.</summary>
    private static string? TypeForTarget(string target) => target switch
    {
        "profile" => "personal_profile",
        "contacts" => "contact_list",
        "preferences" => "preference",
        _ when target.StartsWith("notes/", StringComparison.Ordinal) => "note",
        _ when target.StartsWith("projects/", StringComparison.Ordinal) => "project",
        _ when target.StartsWith("topics/", StringComparison.Ordinal) => "topic",
        _ => null,
    };

    private static string OneLine(string summary) =>
        summary.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');

    private static IEnumerable<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
}
