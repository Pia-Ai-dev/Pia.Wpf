using System.Net.Http;
using System.Text.Json;

namespace Pia.Services.E2EE;

internal static class E2EEErrorBody
{
    public static async Task<string?> ReadCodeAsync(HttpResponseMessage response, CancellationToken ct = default) =>
        ReadCode(await response.Content.ReadAsStringAsync(ct));

    public static string? ReadCode(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                ? error.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
