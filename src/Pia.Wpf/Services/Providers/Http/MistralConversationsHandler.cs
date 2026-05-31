using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace Pia.Services.Providers.Http;

/// <summary>
/// Routes outgoing OpenAI chat-completions traffic to Mistral's
/// <c>/v1/conversations</c> endpoint so a configured Mistral Agent (with its
/// own web-search / RAG tools) can be invoked through the OpenAI SDK.
///
/// Request: rewrites <c>POST .../chat/completions</c> bodies of shape
/// <c>{ model, messages, stream, ... }</c> to <c>{ agent_id, inputs, stream:false, store:false }</c>.
/// System messages are dropped (the agent carries its own instructions).
///
/// Response: maps the conversations response (<c>{ conversation_id, outputs:[...], usage }</c>)
/// back into a synthetic <c>chat.completion</c> shape the OpenAI SDK can parse,
/// concatenating the <c>text</c> chunks inside <c>message.output</c> entries.
///
/// Streaming is forced off — the conversations API streams a different
/// SSE event shape (<c>message.output.delta</c>) that the OpenAI SDK can't
/// consume. Callers requesting <c>stream:true</c> get a buffered response.
/// </summary>
public sealed class MistralConversationsHandler : DelegatingHandler
{
    private readonly string _agentId;

    public MistralConversationsHandler(string agentId)
    {
        _agentId = agentId;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var isChatCompletions = false;
        var clientRequestedStreaming = false;

        if (request.RequestUri is not null)
        {
            var builder = new UriBuilder(request.RequestUri);
            if (builder.Path.EndsWith("chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                builder.Path = "/v1/conversations";
                isChatCompletions = true;
            }
            request.RequestUri = builder.Uri;
        }

        if (isChatCompletions && request.Content is not null)
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            clientRequestedStreaming = WasStreamingRequested(body);
            var rewritten = RewriteRequest(body, _agentId);
            if (rewritten is not null)
                request.Content = new StringContent(rewritten, Encoding.UTF8, "application/json");
        }

        var response = await base.SendAsync(request, cancellationToken);

        if (isChatCompletions &&
            response.Content.Headers.ContentType?.MediaType == "application/json")
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var transformed = TransformResponse(responseBody);
            if (transformed is not null)
            {
                if (clientRequestedStreaming)
                {
                    // SDK is in streaming mode and expects SSE — wrap the buffered
                    // chat.completion as a single chat.completion.chunk event.
                    var sse = BuildSseFromChatCompletion(transformed);
                    response.Content = new StringContent(sse, Encoding.UTF8, "text/event-stream");
                }
                else
                {
                    response.Content = new StringContent(transformed, Encoding.UTF8, "application/json");
                }
            }
        }

        return response;
    }

    private static bool WasStreamingRequested(string body)
    {
        if (string.IsNullOrEmpty(body)) return false;
        try
        {
            return JsonNode.Parse(body) is JsonObject obj &&
                   obj["stream"]?.GetValue<bool>() == true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Wraps a single chat.completion JSON object as one chat.completion.chunk
    /// SSE event followed by a terminator. The OpenAI SDK consumes this as a
    /// degenerate one-event stream; effectively streaming-for-the-caller without
    /// real token streaming (the conversations API doesn't expose chat-completion
    /// style deltas).
    /// </summary>
    internal static string BuildSseFromChatCompletion(string chatCompletionJson)
    {
        if (JsonNode.Parse(chatCompletionJson) is not JsonObject completion)
            return "data: [DONE]\n\n";

        var id = completion["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString();
        var model = completion["model"]?.GetValue<string>() ?? "mistral";
        var created = completion["created"]?.GetValue<long>() ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var choices = completion["choices"] as JsonArray;
        var firstChoice = choices?[0] as JsonObject;
        var content = firstChoice?["message"]?["content"]?.GetValue<string>() ?? string.Empty;

        var chunk = new JsonObject
        {
            ["id"] = id,
            ["object"] = "chat.completion.chunk",
            ["created"] = created,
            ["model"] = model,
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = content,
                    },
                    ["finish_reason"] = "stop",
                },
            },
        };

        if (completion["usage"] is JsonObject usage)
            chunk["usage"] = usage.DeepClone();

        return $"data: {chunk.ToJsonString()}\n\ndata: [DONE]\n\n";
    }

    /// <summary>
    /// Converts an OpenAI chat-completions request body into a Mistral
    /// conversations request body. Returns null if the body is empty, not
    /// JSON, or not a JSON object.
    /// </summary>
    public static string? RewriteRequest(string requestBody, string agentId)
    {
        if (string.IsNullOrEmpty(requestBody))
            return null;

        JsonNode? root;
        try { root = JsonNode.Parse(requestBody); }
        catch { return null; }

        if (root is not JsonObject obj)
            return null;

        var inputs = BuildInputs(obj["messages"]);

        var newObj = new JsonObject
        {
            ["agent_id"] = agentId,
            // Force non-streaming: the conversations SSE shape (`message.output.delta`)
            // doesn't match what the OpenAI SDK expects on `chat/completions`.
            ["stream"] = false,
            ["store"] = false,
            ["inputs"] = inputs,
        };

        return newObj.ToJsonString();
    }

    /// <summary>
    /// Converts the message array from a chat-completions request into the
    /// <c>inputs</c> field for /v1/conversations. System messages are dropped
    /// (the agent owns its own instructions). When only a single user message
    /// remains we collapse to the simpler string form documented in the
    /// Mistral curl sample; otherwise we emit a <c>[{role,content}, ...]</c>
    /// array.
    /// </summary>
    private static JsonNode BuildInputs(JsonNode? messagesNode)
    {
        var entries = new JsonArray();
        if (messagesNode is JsonArray messages)
        {
            foreach (var msg in messages)
            {
                if (msg is not JsonObject msgObj) continue;
                var role = msgObj["role"]?.GetValue<string>();
                if (string.IsNullOrEmpty(role) || role == "system") continue;

                var content = msgObj["content"];
                if (content is null) continue;

                var entry = new JsonObject { ["role"] = role };
                entry["content"] = content.DeepClone();
                entries.Add(entry);
            }
        }

        if (entries.Count == 1 &&
            entries[0] is JsonObject only &&
            only["role"]?.GetValue<string>() == "user" &&
            only["content"] is JsonValue userContent &&
            userContent.TryGetValue<string>(out var userText))
        {
            return JsonValue.Create(userText)!;
        }

        return entries;
    }

    /// <summary>
    /// Converts a /v1/conversations response into a synthetic chat.completion
    /// response so the OpenAI SDK can parse it. Returns null if the body is
    /// not a recognisable conversations response (in which case the caller
    /// leaves the body untouched).
    /// </summary>
    public static string? TransformResponse(string responseBody)
    {
        if (string.IsNullOrEmpty(responseBody))
            return null;

        JsonNode? root;
        try { root = JsonNode.Parse(responseBody); }
        catch { return null; }

        if (root is not JsonObject obj)
            return null;
        if (obj["outputs"] is not JsonArray outputs)
            return null;

        var sb = new StringBuilder();
        string? model = null;
        foreach (var output in outputs)
        {
            if (output is not JsonObject outputObj) continue;
            if (outputObj["type"]?.GetValue<string>() != "message.output") continue;

            model ??= outputObj["model"]?.GetValue<string>();
            AppendContent(sb, outputObj["content"]);
        }

        var chatCompletion = new JsonObject
        {
            ["id"] = obj["conversation_id"]?.DeepClone() ?? JsonValue.Create(Guid.NewGuid().ToString())!,
            ["object"] = "chat.completion",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = model ?? "mistral",
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["message"] = new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = sb.ToString(),
                    },
                    ["finish_reason"] = "stop",
                },
            },
        };

        if (obj["usage"] is JsonObject usage)
            chatCompletion["usage"] = usage.DeepClone();

        return chatCompletion.ToJsonString();
    }

    private static void AppendContent(StringBuilder sb, JsonNode? content)
    {
        switch (content)
        {
            case null:
                return;
            case JsonValue value when value.TryGetValue<string>(out var s):
                sb.Append(s);
                return;
            case JsonArray chunks:
                foreach (var chunk in chunks)
                {
                    if (chunk is not JsonObject chunkObj) continue;
                    if (chunkObj["type"]?.GetValue<string>() != "text") continue;
                    if (chunkObj["text"] is JsonValue chunkText &&
                        chunkText.TryGetValue<string>(out var text))
                    {
                        sb.Append(text);
                    }
                }
                return;
        }
    }
}
