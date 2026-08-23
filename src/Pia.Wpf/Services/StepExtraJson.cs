using System.Text.Json.Nodes;
using Pia.Models;

namespace Pia.Services;

/// <summary>The <c>artifactRef</c> member of a step's <c>ExtraJson</c>: one spelling, one merge, one read.</summary>
internal static class StepExtraJson
{
    private const string ArtifactRefKey = "artifactRef";

    internal const int MaxArtifactChars = StepOutcomeStore.MaxArtifactChars;

    // A document that will not parse is REPLACED rather than preserved: its only other reader already treats
    // an unreadable marker as sequential, so keeping those bytes buys nothing and costs the evidence.
    internal static string WithArtifactRef(string? extraJson, string artifactRef)
    {
        var root = TryReadObject(extraJson) ?? new JsonObject();
        root[ArtifactRefKey] = artifactRef;
        return root.ToJsonString();
    }

    internal static string? ArtifactRefOf(AgentStep step)
    {
        if (TryReadObject(step.ExtraJson) is not { } root
            || !root.TryGetPropertyValue(ArtifactRefKey, out var node)
            || node is not JsonValue value
            || !value.TryGetValue<string>(out var raw))
        {
            return null;
        }

        return StepOutcomeStore.Clamp(raw, MaxArtifactChars);
    }

    private static JsonObject? TryReadObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try { return JsonNode.Parse(json) as JsonObject; }
        catch (Exception) { return null; }
    }
}
