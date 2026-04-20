using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Pia.Shared.Licensing;

namespace Pia.Infrastructure;

/// <summary>
/// Inspects an <see cref="HttpResponseMessage"/> for the Community Edition license-error
/// JSON shapes (<c>no_license</c>, <c>feature_not_licensed</c>, <c>user_limit_reached</c>).
/// Buffers the response body so downstream readers still see the original payload.
/// </summary>
public static class LicenseErrorParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<LicenseErrorResponse?> TryParseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode != HttpStatusCode.Forbidden) return null;
        if (response.Content is null) return null;

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }
        catch
        {
            return null;
        }

        if (bytes.Length == 0) return null;

        LicenseErrorResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<LicenseErrorResponse>(bytes, JsonOptions);
        }
        catch (JsonException)
        {
            RestoreContent(response, bytes, mediaType);
            return null;
        }

        RestoreContent(response, bytes, mediaType);

        if (parsed is null || !LicenseErrorKeys.IsKnown(parsed.Error))
        {
            return null;
        }

        return parsed;
    }

    private static void RestoreContent(HttpResponseMessage response, byte[] bytes, string? mediaType)
    {
        var headers = response.Content.Headers;
        var replacement = new ByteArrayContent(bytes);
        foreach (var header in headers)
        {
            replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        response.Content = replacement;
    }
}
