using System.Globalization;

namespace Pia.Infrastructure.Vault;

/// <summary>
/// The canonical Pia-managed frontmatter block for freshly created vault records. Extracted from
/// MemoryService so IngestService can create topic pages with an identical header without taking a
/// dependency on the whole memory pipeline.
/// </summary>
public static class VaultFrontmatter
{
    public const string TimestampFormat = "yyyy-MM-ddTHH:mm:ssZ";

    public static string Build(string type, string title)
    {
        var id = Guid.NewGuid().ToString("D").ToLowerInvariant();
        var now = DateTime.UtcNow.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        return "---\n" +
               "pia: managed\n" +
               $"id: {id}\n" +
               $"type: {type}\n" +
               $"title: {title}\n" +
               $"created: {now}\n" +
               $"updated: {now}\n" +
               "schemaVersion: 1\n" +
               "---\n";
    }
}
