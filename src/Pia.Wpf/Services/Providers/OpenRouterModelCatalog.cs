using System.Text.Json;
using Pia.Models;

namespace Pia.Services.Providers;

/// <summary>Reads OpenRouter's public <c>/api/v1/models</c> payload.</summary>
public static class OpenRouterModelCatalog
{
    /// <summary>
    /// What the default route serves for <paramref name="modelName"/>, or <see langword="null"/> when the
    /// payload does not list it.
    /// <para>
    /// Prefers <c>top_provider.context_length</c> over the advertised <c>context_length</c>: the advertised
    /// figure is what the model claims, the routed one is what the request will actually be measured
    /// against, and where they differ the advertised one is the larger.
    /// </para>
    /// </summary>
    public static int? TryReadContextLength(string json, string? modelName)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(modelName))
            return null;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return null;

            // Indexed first so the exact id wins wherever it sits in the payload — scanning per candidate
            // would otherwise let an earlier base-id entry beat a later exact one.
            var byId = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var model in data.EnumerateArray())
            {
                if (model.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                    && id.GetString() is { Length: > 0 } raw)
                {
                    byId[OpenRouterContextWindows.Normalize(raw)] = model;
                }
            }

            foreach (var key in OpenRouterContextWindows.LookupKeys(modelName))
                if (byId.TryGetValue(key, out var model) && ReadWindow(model) is { } window)
                    return window;

            return null;
        }
    }

    private static int? ReadWindow(JsonElement model)
    {
        if (model.TryGetProperty("top_provider", out var top) && top.ValueKind == JsonValueKind.Object
            && PositiveInt(top, "context_length") is { } routed)
        {
            return routed;
        }

        return PositiveInt(model, "context_length");
    }

    /// <summary>The ValueKind guard is load-bearing: <c>TryGetInt32</c> THROWS on a JSON null rather than
    /// returning false, and a null <c>context_length</c> is a shape the payload can carry.</summary>
    private static int? PositiveInt(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
        && number > 0
            ? number
            : null;
}
