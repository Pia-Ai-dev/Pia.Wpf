using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Pia.Models;

namespace Pia.Services.Providers.Http;

/// <summary>
/// Rewrites outgoing JSON request bodies for vLLM's OpenAI-compatible
/// endpoint: vLLM ignores `reasoning_effort`. Thinking is toggled via
/// `chat_template_kwargs.enable_thinking` (Qwen3 / DeepSeek-R1 family
/// chat templates).
/// </summary>
internal sealed class VLlmThinkingHandler : DelegatingHandler
{
    private readonly bool _enableThinking;

    public VLlmThinkingHandler(ReasoningEffort effort)
    {
        _enableThinking = effort != ReasoningEffort.None;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Content is not null && IsJsonRequest(request))
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            var rewritten = Rewrite(body, _enableThinking);
            if (rewritten is not null)
            {
                request.Content = new StringContent(rewritten, Encoding.UTF8, "application/json");
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }

    internal static string? Rewrite(string requestBody, bool enableThinking)
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

        // vLLM does not understand reasoning_effort; strip it unconditionally.
        obj.Remove("reasoning_effort");

        var kwargs = obj["chat_template_kwargs"] as JsonObject ?? new JsonObject();
        kwargs["enable_thinking"] = enableThinking;
        obj["chat_template_kwargs"] = kwargs;

        return obj.ToJsonString();
    }

    private static bool IsJsonRequest(HttpRequestMessage request)
    {
        var mediaType = request.Content?.Headers.ContentType?.MediaType;
        return mediaType is "application/json";
    }
}
