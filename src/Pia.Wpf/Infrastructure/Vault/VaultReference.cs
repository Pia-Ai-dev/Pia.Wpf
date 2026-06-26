namespace Pia.Infrastructure.Vault;

/// <summary>
/// Parses a vault address of the form <c>path#heading</c> (e.g. <c>memory/contacts.md#John Smith</c>).
/// The part before the first <c>#</c> is the vault-relative file path; the part after is the section
/// heading TEXT, which is re-slugified (spec §6) to match section identity on read/write. A reference
/// with no <c>#</c> addresses a whole file.
/// </summary>
public static class VaultReference
{
    /// <summary>
    /// Split <paramref name="reference"/> into its file path and (optional) section slug. When a
    /// <c>#</c> is present, <c>Slug</c> is <see cref="VaultSlug.Slugify"/> of the heading text; when
    /// absent, <c>Slug</c> is <c>null</c> (the reference addresses the whole file).
    /// </summary>
    public static (string Path, string? Slug) Parse(string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        var hashIdx = reference.IndexOf('#');
        if (hashIdx < 0)
        {
            return (reference, null);
        }

        var path = reference[..hashIdx];
        var heading = reference[(hashIdx + 1)..];
        return (path, VaultSlug.Slugify(heading));
    }
}
