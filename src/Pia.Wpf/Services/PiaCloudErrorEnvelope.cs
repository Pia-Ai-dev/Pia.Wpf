using System.Text.Json.Nodes;

namespace Pia.Services;

/// <summary>Reads the error chunk the server relays inside an open 200 stream.</summary>
internal static class PiaCloudErrorEnvelope
{
    public const string DefaultTitle = "Upstream Error";

    /// <summary>
    /// Three wire shapes reach this, and the third nests the whole upstream envelope as a STRING — read as
    /// a plain title that put a raw JSON blob in the chat bubble, so it is unwrapped one level here.
    /// </summary>
    public static (string Title, string? Message) Describe(JsonNode? error, JsonNode? chunk)
    {
        if (error is JsonObject direct)
            return (TitleOf(direct), ReadString(direct["message"]));

        var text = ReadString(error);

        if (text is not null && Nested(text) is { } nested)
            return nested;

        return (text ?? DefaultTitle, ReadString(chunk?["message"]));
    }

    private static (string Title, string? Message)? Nested(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '{')
            return null;

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        if (parsed is not JsonObject outer)
            return null;

        if (outer["error"] is JsonObject inner)
            return (TitleOf(inner), ReadString(inner["message"]) ?? ReadString(outer["message"]));

        // A bare {"message":…} envelope, with no nested error object to name it.
        return ReadString(outer["message"]) is { } message ? (DefaultTitle, message) : null;
    }

    private static string TitleOf(JsonObject error) =>
        ReadString(error["type"]) ?? ReadString(error["code"]) ?? DefaultTitle;

    private static string? ReadString(JsonNode? node)
    {
        if (node is not JsonValue value)
            return null;

        if (value.TryGetValue<string>(out var s))
            return string.IsNullOrEmpty(s) ? null : s;

        // A numeric code is a usable title; anything else is not worth guessing at.
        return value.TryGetValue<int>(out var i) ? i.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
    }
}
