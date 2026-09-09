using System.Security.Cryptography;
using System.Text;

namespace Pia.Services.Screen;

/// <summary>Keyed, because a window title is low-entropy enough that an unkeyed digest is dictionary-reversed
/// in seconds. The key lives in memory only, so hashes correlate within one launch and not across.</summary>
internal static class ScreenCaptureTitleHash
{
    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

    /// <summary>Empty for a null or blank title, so a monitor line carries no hash at all.</summary>
    public static string Compute(byte[] key, string? title)
    {
        if (string.IsNullOrEmpty(title))
            return string.Empty;

        var digest = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(title));
        return Convert.ToHexStringLower(digest.AsSpan(0, 8));
    }
}
