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
public sealed record VaultMemoryItem(
    string Reference, string FilePath, string Type, string Title, string Body, DateTime? Updated)
{
    /// <summary>The enum kind for the type chip (maps the §7 <c>type</c> string to <see cref="MemoryType"/>).</summary>
    public MemoryType TypeKind => MemoryObjectTypes.ToKind(Type);

    /// <summary>First non-empty line of the body, trimmed and truncated — the one-line row preview.</summary>
    public string Preview
    {
        get
        {
            foreach (var line in Body.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                return trimmed.Length > 120 ? trimmed[..120] + "…" : trimmed;
            }

            return string.Empty;
        }
    }
}
