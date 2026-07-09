namespace Pia.Models.Vault;

/// <summary>
/// A single user-facing memory as the Memory view consumes it: one <c>## section</c> of a structured
/// document (<c>profile</c>/<c>contacts</c>/<c>preferences</c>), or the whole body of a freeform
/// (<c>note</c>/<c>project</c>/<c>topic</c>) file. Keyed by <see cref="Reference"/> (a
/// <c>path#heading</c> address, or a bare path for freeform files), which is the verb argument for
/// <c>UpdateSectionAsync</c>/<c>ForgetAsync</c>.
///
/// <para><see cref="Updated"/> is the document-level frontmatter timestamp (NOT per-section — a
/// <c>contacts.md</c> with 50 people shares one <c>updated</c>), so it is meaningful for group sort but
/// not per-item recency.</para>
/// </summary>
/// <param name="Category">
/// The frontmatter <c>category</c> for a topic page (e.g. <c>person</c>/<c>organization</c>), used to
/// group topics in the Memory view; <c>null</c> for non-topic records that carry no category.
/// </param>
public sealed record VaultMemoryItem(
    string Reference, string FilePath, string Type, string Title, string Body, DateTime? Updated,
    string? Category = null)
{
    // Mirrors the writer's sentinel in IngestService (a Models-layer type must not depend on a service,
    // so it is duplicated rather than shared — the round-trip test keeps both spellings honest).
    private const string ManagedMarker = "<!-- pia:managed -->";

    /// <summary>The enum kind for the type chip (maps the §7 <c>type</c> string to <see cref="MemoryType"/>).</summary>
    public MemoryType TypeKind => MemoryObjectTypes.ToKind(Type);

    /// <summary>
    /// The body as rendered in the inspector: the internal <c>&lt;!-- pia:managed --&gt;</c> sentinel line
    /// (which opens every synthesized topic page) is dropped so it never shows as literal text, mirroring
    /// what <see cref="Preview"/> already does for the one-line list snippet. The stored <see cref="Body"/>
    /// is untouched — edit and copy still see the raw markdown, sentinel included.
    /// </summary>
    public string DisplayBody
    {
        get
        {
            if (!Body.Contains(ManagedMarker, StringComparison.Ordinal))
            {
                return Body;
            }

            var kept = Body
                .Split('\n')
                .Where(line => line.Trim() != ManagedMarker);
            return string.Join('\n', kept);
        }
    }

    /// <summary>
    /// First real content line of the body, trimmed and truncated — the one-line row preview. Leading
    /// frontmatter fences, HTML comments (including the <c>&lt;!-- pia:managed --&gt;</c> sentinel that
    /// opens every synthesized topic page) and blank lines are skipped so the snippet shows real prose.
    /// </summary>
    public string Preview
    {
        get
        {
            foreach (var line in Body.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0
                    || trimmed == "---"
                    || trimmed.StartsWith("<!--", StringComparison.Ordinal))
                {
                    continue;
                }

                return trimmed.Length > 120 ? trimmed[..120] + "…" : trimmed;
            }

            return string.Empty;
        }
    }
}
