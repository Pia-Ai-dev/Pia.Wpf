using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace Pia.Services.Providers.Http;

/// <summary>
/// Rewrites outgoing chat-completions requests to use Mistral's agents
/// endpoint, enabling agent-managed tools such as web search.
/// Changes /v1/chat/completions → /v1/agents/completions and replaces
/// the "model" field with "agent_id".
/// </summary>
public sealed class MistralAgentsHandler : DelegatingHandler
{
    private readonly string _agentId;

    public MistralAgentsHandler(string agentId)
    {
        _agentId = agentId;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.RequestUri is not null)
        {
            var builder = new UriBuilder(request.RequestUri);
            // Handle both /v1/chat/completions (standard) and /chat/completions
            // (OpenAI SDK strips the /v1 prefix when the endpoint already includes it).
            if (builder.Path.EndsWith("chat/completions", StringComparison.OrdinalIgnoreCase))
                builder.Path = "/v1/agents/completions";
            request.RequestUri = builder.Uri;
        }

        if (request.Content is not null)
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            var rewritten = Rewrite(body, _agentId);
            if (rewritten is not null)
                request.Content = new StringContent(rewritten, Encoding.UTF8, "application/json");
        }

        return await base.SendAsync(request, cancellationToken);
    }

    public static string? Rewrite(string requestBody, string agentId)
    {
        if (string.IsNullOrEmpty(requestBody))
            return null;

        JsonNode? root;
        try { root = JsonNode.Parse(requestBody); }
        catch { return null; }

        if (root is not JsonObject obj)
            return null;

        obj.Remove("model");
        obj["agent_id"] = agentId;

        // The agents completions endpoint only accepts UserMessage, AssistantMessage,
        // and ToolMessage. SystemMessage is not in the schema and causes a 400.
        // The agent already carries its own system prompt from Mistral Studio.
        if (obj["messages"] is JsonArray messages)
        {
            for (var i = messages.Count - 1; i >= 0; i--)
            {
                if (messages[i] is JsonObject msg &&
                    msg["role"]?.GetValue<string>() == "system")
                    messages.RemoveAt(i);
            }
        }

        return obj.ToJsonString();
    }

}
