namespace Pia.Models.Vault;

/// <summary>
/// A single <c>## Heading</c> section within a vault document.
/// <paramref name="BodyStart"/> and <paramref name="BodyEnd"/> are char (UTF-16) indices into the
/// owning document's <see cref="VaultDocument.RawText"/>, such that
/// <c>RawText[BodyStart..BodyEnd]</c> is the section body (spec §3.1 splice contract).
/// </summary>
public sealed record VaultSection(string Heading, string Slug, string Body, int BodyStart, int BodyEnd);

/// <summary>
/// Parsed view of a Pia-managed vault markdown file (format spec v1). <see cref="RawText"/> is the
/// exact, unmodified input bytes-as-text used for byte-range splice edits (spec §3.1).
/// </summary>
public sealed record VaultDocument(
    IReadOnlyDictionary<string, string> Frontmatter, string Preamble,
    IReadOnlyList<VaultSection> Sections, string RawText)
{
    public Guid Id => Guid.Parse(Frontmatter["id"]);
    public string Type => Frontmatter["type"];
}
