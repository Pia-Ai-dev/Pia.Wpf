namespace Pia.Helpers;

/// <summary>
/// The private URI scheme used to turn Obsidian-style <c>[[target]]</c> wikilinks in vault content into
/// clickable in-app navigation rather than external browser opens. <see cref="WikiLinkConverter"/> rewrites
/// the wikilink into a markdown link whose destination is <c>pia-memory:&lt;target&gt;</c>; the shared
/// <c>MarkdownMessageControl</c> recognizes the scheme in its navigation handler and raises an in-app event
/// (instead of <c>Process.Start</c>), which the Memory view resolves back to a <c>VaultMemoryItem</c>.
///
/// <para>The scheme is inert everywhere the converter is not applied: no other content produces this scheme,
/// so assistant chat and other markdown surfaces are unaffected.</para>
/// </summary>
public static class WikiLinkScheme
{
    /// <summary>The scheme name (hyphenated schemes are valid and parse as absolute opaque URIs).</summary>
    public const string Scheme = "pia-memory";

    /// <summary><see cref="Scheme"/> followed by the ':' separator — the prefix of a generated link.</summary>
    public const string Prefix = Scheme + ":";

    /// <summary>
    /// If <paramref name="uri"/> is a wikilink navigation URI, extracts its vault-relative link target
    /// (path without the <c>.md</c> extension, e.g. <c>topics/foo</c>) and returns <c>true</c>; otherwise
    /// returns <c>false</c>. The target is URI-unescaped so encoded characters round-trip.
    /// </summary>
    public static bool TryGetTarget(Uri uri, out string target)
    {
        target = string.Empty;
        if (uri is null || !string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // AbsoluteUri round-trips an opaque scheme:path URI exactly (e.g. "pia-memory:topics/foo").
        var raw = uri.AbsoluteUri;
        var body = raw.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ? raw[Prefix.Length..] : raw;
        target = Uri.UnescapeDataString(body).Trim();
        return target.Length > 0;
    }
}
