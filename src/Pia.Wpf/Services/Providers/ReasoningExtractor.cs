using System.ClientModel.Primitives;
using System.Text.Json.Nodes;

namespace Pia.Services.Providers;

/// <summary>
/// Recovers a provider's non-standard scalar <c>reasoning</c> field from an
/// OpenAI-SDK response object. The OpenAI SDK preserves unknown JSON fields and
/// re-emits them on round-trip, but Microsoft.Extensions.AI only maps
/// <c>reasoning_content</c> (DeepSeek / vLLM / Ollama) to
/// <see cref="Microsoft.Extensions.AI.TextReasoningContent"/> — OpenRouter's
/// <c>reasoning</c> field is dropped. This pulls it back out of the raw
/// representation so it can be surfaced as thinking content.
/// <para>We deliberately read only <c>reasoning</c> (never <c>reasoning_content</c>)
/// so we don't double-capture what the adapter already maps to a typed content part.</para>
/// </summary>
internal static class ReasoningExtractor
{
    /// <summary>
    /// Returns the <c>choices[0].delta.reasoning</c> (streaming) or
    /// <c>choices[0].message.reasoning</c> (non-streaming) string, or
    /// <c>null</c> when absent / not a string / the object can't be serialized.
    /// </summary>
    public static string? FromRawRepresentation(object? rawRepresentation)
    {
        if (rawRepresentation is null)
            return null;

        BinaryData data;
        try { data = ModelReaderWriter.Write(rawRepresentation); }
        catch { return null; }

        return FromJson(data.ToString());
    }

    /// <summary>Parses the reasoning field out of an OpenAI-compatible chat JSON payload.</summary>
    public static string? FromJson(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return null;

        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch { return null; }

        if (root?["choices"]?.AsArray() is not { Count: > 0 } choices)
            return null;

        // Streaming chunks carry `delta`; complete responses carry `message`.
        var container = choices[0]?["delta"] ?? choices[0]?["message"];
        return ReadString(container?["reasoning"]);
    }

    private static string? ReadString(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s)
            ? s
            : null;
}
