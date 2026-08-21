using System.Security.Cryptography;
using System.Text;

namespace Pia.Services;

/// <summary>
/// Derives a stable GUID from arbitrary text. Imports use it for foreign ids that are not GUIDs, so
/// re-importing the same file lands on the same rows instead of duplicating them.
/// </summary>
public static class DeterministicGuid
{
    public static Guid FromString(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(hash.AsSpan(0, 16));
    }
}
