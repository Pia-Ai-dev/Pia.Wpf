using System.Buffers.Text;
using System.Text.Json;

namespace Pia.Services;

internal static class AccessTokenClaims
{
    /// <summary>Null when the token is malformed or carries no <c>e2ee</c> claim; the signature is not checked.</summary>
    public static bool? ReadE2EE(string? accessToken)
    {
        var parts = accessToken?.Split('.');
        if (parts is not { Length: 3 })
            return null;

        try
        {
            using var payload = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));
            if (payload.RootElement.ValueKind != JsonValueKind.Object
                || !payload.RootElement.TryGetProperty("e2ee", out var claim)
                || claim.ValueKind != JsonValueKind.String)
                return null;

            return claim.GetString() switch
            {
                "1" => true,
                "0" => false,
                _ => null,
            };
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }
}
