using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace Pia.Services.Providers.Http;

/// <summary>
/// Strips "thinking" content parts from Mistral JSON responses before the
/// OpenAI SDK deserializes them. When reasoning_effort is active Mistral
/// returns thinking tokens as {"type":"thinking","thinking":"..."} entries
/// inside the content array, which the OpenAI SDK does not recognise and
/// throws ArgumentOutOfRangeException on.
/// </summary>
internal sealed class MistralThinkingResponseHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);

        if (response.Content.Headers.ContentType?.MediaType != "application/json")
            return response;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var rewritten = StripThinkingParts(body);
        if (rewritten is not null)
            response.Content = new StringContent(rewritten, Encoding.UTF8, "application/json");

        return response;
    }

    internal static string? StripThinkingParts(string body)
    {
        if (string.IsNullOrEmpty(body))
            return null;

        JsonNode? root;
        try { root = JsonNode.Parse(body); }
        catch { return null; }

        if (root is not JsonObject obj || obj["choices"] is not JsonArray choices)
            return null;

        var changed = false;
        foreach (var choice in choices)
        {
            if (choice is not JsonObject choiceObj)
                continue;

            if (choiceObj["message"] is JsonObject message)
                changed |= FilterContent(message);

            if (choiceObj["delta"] is JsonObject delta)
                changed |= FilterContent(delta);
        }

        return changed ? obj.ToJsonString() : null;
    }

    private static bool FilterContent(JsonObject container)
    {
        if (container["content"] is not JsonArray content)
            return false;

        var toRemove = new List<int>();
        for (var i = 0; i < content.Count; i++)
        {
            if (content[i] is JsonObject part &&
                part["type"]?.GetValue<string>() == "thinking")
            {
                toRemove.Add(i);
            }
        }

        if (toRemove.Count == 0)
            return false;

        for (var i = toRemove.Count - 1; i >= 0; i--)
            content.RemoveAt(toRemove[i]);

        return true;
    }
}
