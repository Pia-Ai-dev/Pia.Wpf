using System.Globalization;
using Pia.Models;
using Pia.Models.Vault;

namespace Pia.Infrastructure.Vault;

/// <summary>
/// The canonical Pia-managed frontmatter block for freshly created vault records. Extracted from
/// MemoryService so IngestService can create topic pages with an identical header without taking a
/// dependency on the whole memory pipeline.
/// </summary>
public static class VaultFrontmatter
{
    public const string TimestampFormat = "yyyy-MM-ddTHH:mm:ssZ";

    public static string Build(string type, string title) => Build(type, title, null);

    /// <summary>
    /// Build a fresh managed frontmatter block. When <paramref name="category"/> is non-null it is
    /// emitted as a <c>category:</c> line immediately after <c>title:</c> (used by topic pages for
    /// index sub-grouping); the 2-arg overload delegates here with <c>null</c> and stays byte-identical.
    /// </summary>
    public static string Build(string type, string title, string? category)
    {
        var id = Guid.NewGuid().ToString("D").ToLowerInvariant();
        var now = DateTime.UtcNow.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        var categoryLine = category is null
            ? string.Empty
            : $"category: {VaultYaml.EncodeScalar(category)}\n";
        return "---\n" +
               "pia: managed\n" +
               $"id: {id}\n" +
               $"type: {type}\n" +
               $"title: {VaultYaml.EncodeScalar(title)}\n" +
               categoryLine +
               $"created: {now}\n" +
               $"updated: {now}\n" +
               AiContentMarking.YamlLines() +
               "schemaVersion: 1\n" +
               "---\n";
    }

    /// <summary>
    /// Rebuild a topic page's frontmatter while preserving its identity. Always writes
    /// <c>type: topic</c> and the given <paramref name="category"/>; reuses the existing document's
    /// <c>id</c> and <c>created</c> when present (else mints fresh, mirroring
    /// <c>VaultIndexService.BuildFrontmatter</c>) and stamps a fresh <c>updated</c>. This is the only
    /// frontmatter builder re-synthesis calls, so a topic page's <c>id</c>/<c>created</c> stay stable
    /// across re-ingests (sync keys on <c>id</c>).
    /// </summary>
    public static string BuildPreserving(VaultDocument? existing, string title, string category)
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

        return "---\n" +
               "pia: managed\n" +
               $"id: {id}\n" +
               "type: topic\n" +
               $"title: {VaultYaml.EncodeScalar(title)}\n" +
               $"category: {VaultYaml.EncodeScalar(category)}\n" +
               $"created: {created}\n" +
               $"updated: {now}\n" +
               AiContentMarking.YamlLines() +
               "schemaVersion: 1\n" +
               "---\n";
    }
}
