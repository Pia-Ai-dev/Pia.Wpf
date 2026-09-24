using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace Pia.Services.Providers.Http;

// Adds what the Microsoft.Extensions.AI adapter cannot: the web search tool, the thinking config, and
// cache_control breakpoints. A breakpoint goes on the LAST message block, which exists only after the
// adapter has built the array; MessageCreateParams also declares Model/Messages/MaxTokens required, so it
// cannot be the partial overlay RawRepresentationFactory expects.
internal sealed class AnthropicRequestHandler : DelegatingHandler
{
    // Basic search: called directly, available on every model. The newer variants run inside code execution.
    private const string WebSearchToolType = "web_search_20250305";

    private const int MaxWebSearchUses = 5;

    private readonly bool _enableWebSearch;
    private readonly bool _enablePromptCache;
    private readonly string? _effort;
    private readonly bool _disableThinking;

    public AnthropicRequestHandler(
        bool enableWebSearch, bool enablePromptCache, string? effort, bool disableThinking)
    {
        _enableWebSearch = enableWebSearch;
        _enablePromptCache = enablePromptCache;
        _effort = effort;
        _disableThinking = disableThinking;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is not null && IsJsonRequest(request))
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            var rewritten = Rewrite(body, _enableWebSearch, _enablePromptCache, _effort, _disableThinking);
            if (rewritten is not null)
                request.Content = new StringContent(rewritten, Encoding.UTF8, "application/json");
        }

        return await base.SendAsync(request, cancellationToken);
    }

    internal static string? Rewrite(
        string requestBody, bool enableWebSearch, bool enablePromptCache,
        string? effort = null, bool disableThinking = false)
    {
        if (string.IsNullOrEmpty(requestBody)) return null;
        if (!enableWebSearch && !enablePromptCache && effort is null && !disableThinking) return null;

        JsonNode? root;
        try { root = JsonNode.Parse(requestBody); }
        catch (System.Text.Json.JsonException) { return null; }
        if (root is not JsonObject obj) return null;

        if (enableWebSearch)
        {
            var tools = obj["tools"] as JsonArray ?? [];
            tools.Add(new JsonObject
            {
                ["type"] = WebSearchToolType,
                ["name"] = "web_search",
                ["max_uses"] = MaxWebSearchUses,
            });
            obj["tools"] = tools;
        }

        // From the provider row alone, never the turn: changing it between requests invalidates the cache.
        // Effort and thinking are separate fields — an effort alone never turns thinking off.
        if (disableThinking)
        {
            obj["thinking"] = new JsonObject { ["type"] = "disabled" };
        }
        else if (effort is not null)
        {
            obj["thinking"] = new JsonObject { ["type"] = "adaptive", ["display"] = "summarized" };
            var outputConfig = obj["output_config"] as JsonObject ?? [];
            outputConfig["effort"] = effort;
            obj["output_config"] = outputConfig;
        }

        if (enablePromptCache)
            ApplyCacheBreakpoints(obj);

        return obj.ToJsonString();
    }

    // The prefix renders tools → system → messages, so a system marker already covers the tools behind it;
    // only a system-less request needs the tool marker. Plain ephemeral is the five-minute TTL.
    internal static void ApplyCacheBreakpoints(JsonObject body)
    {
        if (body["system"] is JsonArray { Count: > 0 } system)
        {
            if (system[^1] is JsonObject lastSystem)
                lastSystem["cache_control"] = Ephemeral();
        }
        else if (body["tools"] is JsonArray { Count: > 0 } tools)
        {
            if (tools[^1] is JsonObject lastTool)
                lastTool["cache_control"] = Ephemeral();
        }

        if (body["messages"] is JsonArray { Count: > 0 } messages
            && messages[^1] is JsonObject lastMessage
            && lastMessage["content"] is JsonArray { Count: > 0 } content
            && content[^1] is JsonObject lastBlock)
        {
            lastBlock["cache_control"] = Ephemeral();
        }
    }

    private static JsonObject Ephemeral() => new() { ["type"] = "ephemeral" };

    private static bool IsJsonRequest(HttpRequestMessage request)
        => request.Content?.Headers.ContentType?.MediaType is "application/json";
}
