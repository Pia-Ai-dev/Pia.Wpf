using System.Security.Cryptography;
using System.Text;

namespace Pia.Logging;

// Format a URL for logging.
//
// DEBUG builds return the original URL (truncated at 500 chars) so developers
// can see exactly what was hit. RELEASE builds reduce the URL to
// "{scheme}://host-NNN" where NNN is a stable 3-digit code derived from
// SHA256(lowercase host) — support can correlate "all host-847 requests fail"
// across user-submitted log files without learning the actual host.
public static class SafeUrl
{
    private const int MaxUrlLength = 500;
    private const string Empty = "<no-url>";

    public static string Format(Uri? uri)
    {
        if (uri is null) return Empty;

#if DEBUG
        return Truncate(uri.ToString());
#else
        var scheme = uri.IsAbsoluteUri ? uri.Scheme : "unknown";
        var host = uri.IsAbsoluteUri ? uri.Host : null;
        return $"{scheme}://host-{HashHost(host)}";
#endif
    }

    public static string Format(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return Empty;

#if DEBUG
        return Truncate(url);
#else
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return Format(uri);
        // Not parseable as absolute URI — strip everything; we can't safely
        // identify scheme/host, so just emit the anonymous code over the
        // whole string.
        return $"opaque://host-{HashHost(url)}";
#endif
    }

#if DEBUG
    private static string Truncate(string url)
        => url.Length > MaxUrlLength ? string.Concat(url.AsSpan(0, MaxUrlLength), "...") : url;
#else
    private static string HashHost(string? host)
    {
        var input = (host ?? string.Empty).ToLowerInvariant();
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(input), hash);
        // First 8 bytes as ulong, mod 1000, formatted as 3 digits.
        var n = BitConverter.ToUInt64(hash[..8]) % 1000;
        return n.ToString("D3");
    }
#endif
}
