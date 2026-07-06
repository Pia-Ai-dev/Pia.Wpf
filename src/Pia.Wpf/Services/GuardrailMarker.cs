namespace Pia.Services;

using System.Text.Json.Nodes;

/// <summary>
/// Reads the neutral guardrail routing marker the server attaches to a chat response — an envelope field
/// on buffered responses, or a choiceless SSE <c>data:</c> chunk on streams: <c>{"guardrail":{"protected":true}}</c>.
/// Returns only whether the answer was routed to the protected model; no outcome, model, or verdict detail
/// is carried (by design). Tolerant: false on null / missing / non-bool.
/// </summary>
public static class GuardrailMarker
{
    public static bool IsProtected(JsonNode? envelopeOrChunk) =>
        envelopeOrChunk?["guardrail"]?["protected"] is JsonValue v
        && v.TryGetValue<bool>(out var isProtected)
        && isProtected;
}
