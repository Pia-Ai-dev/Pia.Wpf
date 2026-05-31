using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Pia.Models;

namespace Pia.Services.Providers.Http;

/// <summary>
/// Rewrites outgoing JSON request bodies for OpenRouter: removes the flat
/// `reasoning_effort` field (which OpenRouter rejects when paired with a
/// nested form) and replaces it with `reasoning: { effort: "..." }` per the
/// OpenRouter API contract. Optionally injects `plugins: [{"id":"web"}]` to
/// enable native web search.
/// </summary>
internal sealed class OpenRouterReasoningHandler : DelegatingHandler
{
    private readonly ReasoningEffort _effort;
    private readonly bool _enableWebSearch;

    public OpenRouterReasoningHandler(ReasoningEffort effort, bool enableWebSearch = false)
    {
        _effort = effort;
        _enableWebSearch = enableWebSearch;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Content is not null && IsJsonRequest(request))
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            var rewritten = Rewrite(body, _effort, _enableWebSearch);
            if (rewritten is not null)
            {
                request.Content = new StringContent(rewritten, Encoding.UTF8, "application/json");
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }

    internal static string? Rewrite(string requestBody, ReasoningEffort effort, bool enableWebSearch = false)
    {
        if (string.IsNullOrEmpty(requestBody))
            return null;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(requestBody);
        }
        catch
        {
            return null;
        }

        if (root is not JsonObject obj)
            return null;

        obj.Remove("reasoning_effort");

        if (effort == ReasoningEffort.None)
            obj.Remove("reasoning");
        else
            obj["reasoning"] = new JsonObject { ["effort"] = MapEffort(effort) };

        if (enableWebSearch)
            obj["plugins"] = new JsonArray { new JsonObject { ["id"] = "web" } };

        return obj.ToJsonString();
    }

    private static string MapEffort(ReasoningEffort effort) => effort switch
    {
        ReasoningEffort.Minimal => "minimal",
        ReasoningEffort.Low => "low",
        ReasoningEffort.Medium => "medium",
        ReasoningEffort.High => "high",
        ReasoningEffort.XHigh => "high",
        _ => "low",
    };

    private static bool IsJsonRequest(HttpRequestMessage request)
    {
        var mediaType = request.Content?.Headers.ContentType?.MediaType;
        return mediaType is "application/json";
    }
}
