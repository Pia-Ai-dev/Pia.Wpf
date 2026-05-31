using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace Pia.Services.Providers.Http;

/// <summary>
/// Injects {"type":"web_search_preview"} into the tools array of outgoing
/// OpenAI Responses API requests, enabling the model's built-in web search.
/// </summary>
internal sealed class OpenAiWebSearchHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Content is not null && IsJsonRequest(request))
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            var rewritten = Rewrite(body);
            if (rewritten is not null)
                request.Content = new StringContent(rewritten, Encoding.UTF8, "application/json");
        }

        return await base.SendAsync(request, cancellationToken);
    }

    internal static string? Rewrite(string requestBody)
    {
        if (string.IsNullOrEmpty(requestBody))
            return null;

        JsonNode? root;
        try { root = JsonNode.Parse(requestBody); }
        catch { return null; }

        if (root is not JsonObject obj)
            return null;

        var tools = obj["tools"] as JsonArray ?? new JsonArray();
        tools.Add(new JsonObject { ["type"] = "web_search_preview" });
        obj["tools"] = tools;

        return obj.ToJsonString();
    }

    private static bool IsJsonRequest(HttpRequestMessage request)
        => request.Content?.Headers.ContentType?.MediaType is "application/json";
}
