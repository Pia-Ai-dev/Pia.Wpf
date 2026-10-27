using System.Security.Cryptography;
using System.Text;

namespace Pia.Shared.E2EE;

/// <summary>The bytes a device signs to authorise a request, so the client and the server sign and check the same thing.</summary>
public static class DeviceRequestSignature
{
    public const string DeviceIdHeader = "X-Pia-Device-Id";
    public const string ChallengeIdHeader = "X-Pia-Challenge-Id";
    public const string SignatureHeader = "X-Pia-Device-Signature";

    /// <summary><paramref name="path"/> is the route path from <c>/api/</c> on, as on the wire: no deployment
    /// prefix, no query, no host.</summary>
    public static byte[] Payload(
        string method, string path, Guid userId, string deviceId, string challengeId, ReadOnlySpan<byte> body) =>
        Encoding.UTF8.GetBytes(
            $"pia-req-v1|{method.ToUpperInvariant()}|{path}|{userId:D}|{deviceId}|{challengeId}|{Convert.ToBase64String(SHA256.HashData(body))}");
}
