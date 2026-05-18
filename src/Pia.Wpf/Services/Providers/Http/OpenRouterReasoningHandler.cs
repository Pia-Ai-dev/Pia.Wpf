using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Pia.Models;

namespace Pia.Services.Providers.Http;

/// <summary>
/// Rewrites outgoing JSON request bodies for OpenRouter: removes the flat
/// `reasoning_effort` field (which OpenRouter rejects when paired with a
/// nested form) and replaces it with `reasoning: { effort: "..." }` per the
/// OpenRouter API contract.
/// </summary>
internal sealed class OpenRouterReasoningHandler : DelegatingHandler
{
    private readonly ReasoningEffort _effort;

    public OpenRouterReasoningHandler(ReasoningEffort effort)
    {
        _effort = effort;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Content is not null && IsJsonRequest(request))
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            var rewritten = Rewrite(body, _effort);
            if (rewritten is not null)
            {
                request.Content = new StringContent(rewritten, Encoding.UTF8, "application/json");
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }

    internal static string? Rewrite(string requestBody, ReasoningEffort effort)
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
        {
            obj.Remove("reasoning");
            return obj.ToJsonString();
        }

        obj["reasoning"] = new JsonObject
        {
            ["effort"] = MapEffort(effort),
        };

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
