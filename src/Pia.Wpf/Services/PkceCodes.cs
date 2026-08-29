using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Pia.Services;

/// <summary>RFC 7636 S256 pair. The verifier never leaves this process except in the POST that redeems the login code.</summary>
public static class PkceCodes
{
    public static (string Verifier, string Challenge) Create()
    {
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        return (verifier, ComputeChallenge(verifier));
    }

    public static string ComputeChallenge(string verifier) =>
        Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
}
