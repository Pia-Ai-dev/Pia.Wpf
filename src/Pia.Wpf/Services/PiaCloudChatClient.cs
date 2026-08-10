using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Logging;

namespace Pia.Services;

/// <summary>
/// Custom IChatClient that talks to the Pia Cloud /api/ai/chat endpoint,
/// supporting tool calling and streaming.
/// </summary>
public sealed class PiaCloudChatClient : IChatClient
{
    private readonly HttpClient _httpClient;
    private readonly string _chatUrl;
    private readonly string? _mode;
    private readonly Guid? _managedPersonaId;
    private readonly string? _modelType;
    private readonly ILogger _logger;
    private readonly Func<bool, string?, Task<string?>> _tokenProvider;

    /// <param name="tokenProvider">
    /// Resolves a bearer token; the bool argument requests a forced refresh (used on 401 retry),
    /// and the string argument carries the token that failed so duplicate refreshes can be avoided.
    /// </param>
    /// <param name="managedPersonaId">
    /// The persona driving this turn, sent as <c>X-Pia-Persona</c> so the server can UNION the persona's
    /// bound KBs/connectors into the request's plugin scope. Sent for ANY selected persona, managed or
    /// not: the server maps an id it does not recognise to null (deliberately no existence oracle), so no
    /// "is this managed?" check belongs on the chat path.
    /// </param>
    /// <param name="modelType">
    /// The persona's model-routing hint, sent as <c>metadata.pia_persona_type</c> so the server can route
    /// Assistant-mode chat to the group's catalog provider for that type. Null ⇒ the key is omitted
    /// entirely (no empty <c>metadata</c> object on the wire).
    /// </param>
    public PiaCloudChatClient(HttpClient httpClient, string serverUrl, Func<bool, string?, Task<string?>> tokenProvider, ILogger logger, string? mode = null, Guid? managedPersonaId = null, string? modelType = null)
    {
        _httpClient = httpClient;
        _chatUrl = $"{serverUrl.TrimEnd('/')}/api/ai/chat";
        _mode = mode;
        _managedPersonaId = managedPersonaId;
        _modelType = modelType;
        _logger = logger;
        _tokenProvider = tokenProvider;
    }

    private async Task<HttpResponseMessage> SendWithAuthRetryAsync(
        string requestBody, HttpCompletionOption completionOption, CancellationToken cancellationToken)
    {
        async Task<(HttpResponseMessage Response, string? Token)> Attempt(bool forceRefresh, string? staleAccessToken = null)
        {
            var token = await _tokenProvider(forceRefresh, staleAccessToken);
            var request = new HttpRequestMessage(HttpMethod.Post, _chatUrl)
            {
                Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrEmpty(token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (!string.IsNullOrEmpty(_mode))
                request.Headers.Add("X-Pia-Mode", _mode);
            // Omitted entirely when no persona was resolved — an empty header value is not the contract.
            if (_managedPersonaId is Guid personaId)
                request.Headers.Add("X-Pia-Persona", personaId.ToString());
            return (await _httpClient.SendAsync(request, completionOption, cancellationToken), token);
        }

        var (response, token) = await Attempt(forceRefresh: false);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            _logger.LogInformation("PiaCloudChatClient: unauthorized; refreshing token and retrying once");
            response.Dispose();
            (response, _) = await Attempt(forceRefresh: true, token);
        }
        return response;
    }

    public ChatClientMetadata Metadata => new("PiaCloud");

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var requestBody = BuildRequestBody(chatMessages, options, stream: false);
        _logger.LogDebug("PiaCloudChatClient: POST {Url} (non-streaming)", SafeUrl.Format(_chatUrl));

        using var response = await SendWithAuthRetryAsync(
            requestBody, HttpCompletionOption.ResponseContentRead, cancellationToken);

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        await HandleErrorResponse(response, responseJson);

        return ParseChatResponse(responseJson);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var requestBody = BuildRequestBody(chatMessages, options, stream: true);
        _logger.LogDebug("PiaCloudChatClient: POST {Url} (streaming)", SafeUrl.Format(_chatUrl));

        using var response = await SendWithAuthRetryAsync(
            requestBody, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            await HandleErrorResponse(response, errorBody);
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        // Track tool call accumulation across deltas
        var toolCallBuilders = new List<ToolCallBuilder>();

        // OpenAI emits usage on a single final, choiceless chunk — but some OpenAI-compatible
        // backends (observed: GLM reasoning models) report it on EVERY chunk instead. Buffering to
        // the latest and yielding once after the loop keeps both correct; yielding per-occurrence
        // would let Microsoft.Extensions.AI's ToChatResponse() sum the same usage N times over.
        UsageDetails? pendingUsage = null;

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (string.IsNullOrEmpty(line)) continue;

            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            var data = line[6..];
            if (data == "[DONE]") break;

            JsonNode? json;
            try { json = JsonNode.Parse(data); }
            catch { continue; }

            if (TryParseUsage(json?["usage"]) is { } streamUsage)
                pendingUsage = streamUsage;

            // Neutral guardrail marker rides its own choiceless chunk (server emits it first).
            if (GuardrailMarker.IsProtected(json))
            {
                yield return new ChatResponseUpdate
                {
                    Role = ChatRole.Assistant,
                    AdditionalProperties = new AdditionalPropertiesDictionary { [GuardrailMarker.AdditionalPropertyKey] = true }
                };
            }

            var choices = json?["choices"]?.AsArray();
            if (choices is null || choices.Count == 0) continue;
            var choice = choices[0];
            if (choice is null) continue;

            var delta = choice["delta"];
            if (delta is null) continue;

            var finishReason = choice["finish_reason"]?.GetValue<string>();

            // Text content. Read defensively: the canonical shape is a plain string, but
            // some providers (e.g. Mistral reasoning chunks) emit content as an array of
            // typed parts, which would throw on GetValue<string>().
            var textContent = ReadContent(delta["content"]);
            if (!string.IsNullOrEmpty(textContent))
            {
                yield return new ChatResponseUpdate
                {
                    Role = ChatRole.Assistant,
                    Contents = [new TextContent(textContent)]
                };
            }

            // Reasoning content (when the server forwards it). Surfaced as
            // TextReasoningContent so it funnels into ThinkingContent like every other provider.
            var reasoning = ReadString(delta["reasoning"]) ?? ReadString(delta["reasoning_content"]);
            if (!string.IsNullOrEmpty(reasoning))
            {
                yield return new ChatResponseUpdate
                {
                    Role = ChatRole.Assistant,
                    Contents = [new TextReasoningContent(reasoning)]
                };
            }

            // Tool call deltas
            var toolCalls = delta["tool_calls"]?.AsArray();
            if (toolCalls is not null)
            {
                foreach (var tc in toolCalls)
                {
                    if (tc is null) continue;

                    var id = tc["id"]?.GetValue<string>();
                    var index = tc["index"]?.GetValue<int>();
                    var funcNode = tc["function"];
                    var name = funcNode?["name"]?.GetValue<string>();

                    // Call-opening deltas only — enough to see whether the wire separates parallel calls
                    // (and so whether the proxy needs fixing too) without a line per argument fragment.
                    if (id is not null || name is not null)
                    {
                        _logger.SensitiveDebug("PiaCloudChatClient: tool_call delta id={Id} index={Index} name={Name}",
                            id, index, name);
                    }

                    var builder = ResolveToolCallBuilder(toolCallBuilders, id, index, name);
                    if (id is not null) builder.Id = id;
                    if (name is not null) builder.Name = name;

                    var args = funcNode?["arguments"]?.GetValue<string>();
                    if (args is not null) builder.Args.Append(args);
                }
            }

            // When finish_reason is "tool_calls", emit accumulated tool calls
            if (finishReason == "tool_calls")
            {
                foreach (var update in EmitAccumulatedToolCalls(toolCallBuilders))
                    yield return update;
                toolCallBuilders.Clear();
            }

            // Propagate any finish_reason so aggregated ChatResponse.FinishReason is populated
            // (lets callers detect truncation, etc.)
            if (finishReason is not null)
            {
                ChatFinishReason? mapped = finishReason switch
                {
                    "stop" => ChatFinishReason.Stop,
                    "tool_calls" => ChatFinishReason.ToolCalls,
                    "length" => ChatFinishReason.Length,
                    "content_filter" => ChatFinishReason.ContentFilter,
                    _ => null
                };
                if (mapped is not null)
                {
                    yield return new ChatResponseUpdate
                    {
                        Role = ChatRole.Assistant,
                        FinishReason = mapped
                    };
                }
            }
        }

        // Defensive flush: some servers omit finish_reason="tool_calls" on long generations.
        // Without this, accumulated tool calls would be silently dropped.
        if (toolCallBuilders.Count > 0)
        {
            _logger.LogWarning(
                "PiaCloudChatClient: stream ended without finish_reason=tool_calls; flushing {Count} accumulated tool call(s)",
                toolCallBuilders.Count);
            foreach (var update in EmitAccumulatedToolCalls(toolCallBuilders))
                yield return update;
        }

        if (pendingUsage is not null)
        {
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = [new UsageContent(pendingUsage)]
            };
        }
    }

    private sealed class ToolCallBuilder
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public int? WireIndex { get; init; }
        public StringBuilder Args { get; } = new();
    }

    /// <summary>
    /// A delta joins an existing call only on a matching id or index — keying on index alone folded every
    /// parallel call from a provider that omits <c>index</c> into one accumulator, concatenating their args.
    /// </summary>
    private static ToolCallBuilder ResolveToolCallBuilder(
        List<ToolCallBuilder> builders, string? id, int? index, string? name)
    {
        if (!string.IsNullOrEmpty(id))
        {
            var byId = builders.FirstOrDefault(b => b.Id == id);
            if (byId is not null) return byId;
            return Append(builders, id, index);
        }

        if (index is not null)
        {
            // Newest first: a stream that reuses index 0 for every call still routes each
            // continuation delta to the call it opened.
            var byIndex = builders.LastOrDefault(b => b.WireIndex == index);
            if (byIndex is not null) return byIndex;
            return Append(builders, id, index);
        }

        if (builders.Count > 0)
        {
            var last = builders[^1];
            // With neither id nor index, a second, different name is the only call boundary the
            // stream offers.
            if (name is null || last.Name is null || last.Name == name) return last;
        }

        return Append(builders, id, index);

        static ToolCallBuilder Append(List<ToolCallBuilder> builders, string? id, int? index)
        {
            // Splitting an id-less stream would otherwise give both calls the same empty CallId, which a
            // provider rejects once they are echoed back with their results.
            id ??= builders.Count > 0 ? $"pia_call_{builders.Count}" : null;
            var created = new ToolCallBuilder { Id = id, WireIndex = index };
            builders.Add(created);
            return created;
        }
    }

    private static IEnumerable<ChatResponseUpdate> EmitAccumulatedToolCalls(List<ToolCallBuilder> builders)
    {
        foreach (var builder in builders)
        {
            if (builder.Name is null) continue;

            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = [BuildFunctionCall(builder.Id ?? "", builder.Name, builder.Args.ToString())]
            };
        }
    }

    private static readonly JsonSerializerOptions ToolArgumentOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Unparseable arguments surface as <see cref="FunctionCallContent.Exception"/>, not a substitute
    /// dictionary, so the tool loop can report a malformed call instead of running one with no parameters.
    /// </summary>
    private static FunctionCallContent BuildFunctionCall(string callId, string name, string? argsJson) =>
        string.IsNullOrEmpty(argsJson)
            ? new FunctionCallContent(callId, name, null)
            : FunctionCallContent.CreateFromParsedArguments(argsJson, callId, name, ParseToolArguments);

    private static IDictionary<string, object?> ParseToolArguments(string argsJson) =>
        JsonSerializer.Deserialize<Dictionary<string, object?>>(argsJson, ToolArgumentOptions)
            ?? new Dictionary<string, object?>();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }

    private string BuildRequestBody(IEnumerable<ChatMessage> messages, ChatOptions? options, bool stream)
    {
        var body = new JsonObject
        {
            ["messages"] = SerializeMessages(messages),
            ["stream"] = stream
        };

        if (options?.Tools is { Count: > 0 })
        {
            body["tools"] = SerializeTools(options.Tools);
        }

        if (options?.Temperature is not null)
            body["temperature"] = options.Temperature.Value;

        if (options?.MaxOutputTokens is not null)
            body["max_tokens"] = options.MaxOutputTokens.Value;

        if (_modelType is not null)
            body["metadata"] = new JsonObject { ["pia_persona_type"] = _modelType };

        if (stream)
        {
            body["stream_options"] = new JsonObject { ["include_usage"] = true };
        }

        return body.ToJsonString();
    }

    private static UsageDetails? TryParseUsage(JsonNode? usageNode)
    {
        if (usageNode is null) return null;

        var input = ReadLong(usageNode["prompt_tokens"]) ?? ReadLong(usageNode["input_tokens"]);
        var output = ReadLong(usageNode["completion_tokens"]) ?? ReadLong(usageNode["output_tokens"]);
        var total = ReadLong(usageNode["total_tokens"]);

        if (input is null && output is null && total is null) return null;

        return new UsageDetails
        {
            InputTokenCount = input,
            OutputTokenCount = output,
            TotalTokenCount = total ?? ((input ?? 0) + (output ?? 0)),
        };
    }

    private static long? ReadLong(JsonNode? node)
    {
        if (node is null) return null;
        try { return node.GetValue<long>(); }
        catch { return null; }
    }

    private static string? ReadString(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s)
            ? s
            : null;

    /// <summary>
    /// Reads message/delta content tolerantly. The canonical shape is a plain string, but
    /// some providers emit an array of typed parts (e.g. <c>[{ "type": "text", "text": "…" }]</c>);
    /// in that case the text parts are concatenated. Any other shape is ignored (returns null)
    /// rather than throwing.
    /// </summary>
    private static string? ReadContent(JsonNode? node)
    {
        if (node is null) return null;

        if (ReadString(node) is { } scalar) return scalar;

        if (node is JsonArray array)
        {
            var sb = new StringBuilder();
            foreach (var part in array)
            {
                if (part is not JsonObject obj) continue;
                var type = ReadString(obj["type"]);
                if (type is null or "text" && ReadString(obj["text"]) is { } text)
                    sb.Append(text);
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }

        return null;
    }

    private static JsonArray SerializeMessages(IEnumerable<ChatMessage> messages)
    {
        var array = new JsonArray();

        foreach (var msg in messages)
        {
            var role = msg.Role == ChatRole.System ? "system"
                : msg.Role == ChatRole.User ? "user"
                : msg.Role == ChatRole.Assistant ? "assistant"
                : msg.Role == ChatRole.Tool ? "tool"
                : "user";

            // Tool result messages
            if (msg.Role == ChatRole.Tool)
            {
                foreach (var content in msg.Contents.OfType<FunctionResultContent>())
                {
                    var resultStr = content.Result switch
                    {
                        string s => s,
                        null => "",
                        _ => JsonSerializer.Serialize(content.Result)
                    };

                    array.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = content.CallId,
                        ["content"] = resultStr
                    });
                }
                continue;
            }

            var msgObj = new JsonObject { ["role"] = role };

            var textParts = new List<string>();
            var toolCalls = new JsonArray();
            var imageParts = new List<(string MediaType, ReadOnlyMemory<byte> Data)>();

            foreach (var content in msg.Contents)
            {
                switch (content)
                {
                    case TextContent tc:
                        textParts.Add(tc.Text);
                        break;
                    case FunctionCallContent fc:
                        var funcCall = new JsonObject
                        {
                            ["id"] = fc.CallId,
                            ["type"] = "function",
                            ["function"] = new JsonObject
                            {
                                ["name"] = fc.Name,
                                ["arguments"] = fc.Arguments is not null
                                    ? JsonSerializer.Serialize(fc.Arguments)
                                    : "{}"
                            }
                        };
                        toolCalls.Add(funcCall);
                        break;
                    case DataContent dc when dc.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase):
                        imageParts.Add((dc.MediaType, dc.Data));
                        break;
                }
            }

            if (imageParts.Count > 0)
            {
                var parts = new JsonArray();
                foreach (var text in textParts)
                {
                    if (!string.IsNullOrEmpty(text))
                        parts.Add(new JsonObject { ["type"] = "text", ["text"] = text });
                }
                foreach (var (mediaType, data) in imageParts)
                {
                    var url = $"data:{mediaType};base64,{Convert.ToBase64String(data.Span)}";
                    parts.Add(new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject { ["url"] = url }
                    });
                }
                msgObj["content"] = parts;
            }
            else if (textParts.Count > 0)
                msgObj["content"] = string.Join("", textParts);
            else if (toolCalls.Count == 0)
                msgObj["content"] = msg.Text ?? "";

            if (toolCalls.Count > 0)
            {
                msgObj["tool_calls"] = toolCalls;
                // OpenAI requires content to be null or present when tool_calls exist
                if (!msgObj.ContainsKey("content"))
                    msgObj["content"] = (JsonNode?)null;
            }

            array.Add(msgObj);
        }

        return array;
    }

    private static JsonArray SerializeTools(IList<AITool> tools)
    {
        var array = new JsonArray();

        foreach (var tool in tools)
        {
            if (tool is not AIFunction func) continue;

            var funcObj = new JsonObject
            {
                ["name"] = func.Name,
                ["description"] = func.Description
            };

            // Serialize the JSON schema for parameters
            if (func.JsonSchema.ValueKind != JsonValueKind.Undefined)
            {
                var schemaNode = JsonNode.Parse(func.JsonSchema.GetRawText());
                funcObj["parameters"] = schemaNode;
            }

            array.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = funcObj
            });
        }

        return array;
    }

    private ChatResponse ParseChatResponse(string responseJson)
    {
        var json = JsonNode.Parse(responseJson)
            ?? throw new InvalidOperationException("Invalid response from Pia Cloud");

        // The server's non-streaming chat returns the raw upstream OpenAI-compatible envelope
        // (choices[0].message / finish_reason — Pia.Server AiProxyEndpoints "Return raw upstream
        // JSON"). The flat { message, finishReason } shape stays as a fallback for older envelopes.
        var choice = (json["choices"] as JsonArray)?.FirstOrDefault() as JsonObject;
        var message = (choice?["message"] ?? json["message"])?.AsObject()
            ?? throw new InvalidOperationException("Response missing 'message' field");

        var model = ReadString(json["model"]) ?? "pia-cloud";
        var finishReason = ReadString(choice?["finish_reason"]) ?? ReadString(json["finishReason"]);

        var chatMessage = ParseMessageObject(message);

        ChatFinishReason? chatFinishReason = finishReason switch
        {
            "stop" => ChatFinishReason.Stop,
            "tool_calls" => ChatFinishReason.ToolCalls,
            "length" => ChatFinishReason.Length,
            _ => null
        };

        var response = new ChatResponse([chatMessage])
        {
            ModelId = model,
            FinishReason = chatFinishReason,
            Usage = TryParseUsage(json["usage"])
        };
        // Neutral guardrail marker: the answer was routed to the protected model. No detail beyond a flag.
        if (GuardrailMarker.IsProtected(json))
        {
            response.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            response.AdditionalProperties[GuardrailMarker.AdditionalPropertyKey] = true;
        }
        return response;
    }

    private static ChatMessage ParseMessageObject(JsonObject message)
    {
        var contents = new List<AIContent>();

        var textContent = ReadContent(message["content"]);
        if (!string.IsNullOrEmpty(textContent))
        {
            contents.Add(new TextContent(textContent));
        }

        var reasoning = ReadString(message["reasoning"]) ?? ReadString(message["reasoning_content"]);
        if (!string.IsNullOrEmpty(reasoning))
        {
            contents.Add(new TextReasoningContent(reasoning));
        }

        var toolCalls = message["tool_calls"]?.AsArray();
        if (toolCalls is not null)
        {
            foreach (var tc in toolCalls)
            {
                if (tc is null) continue;
                var id = tc["id"]?.GetValue<string>() ?? "";
                var funcNode = tc["function"];
                var name = funcNode?["name"]?.GetValue<string>() ?? "";
                var argsStr = funcNode?["arguments"]?.GetValue<string>();

                contents.Add(BuildFunctionCall(id, name, argsStr));
            }
        }

        return new ChatMessage(ChatRole.Assistant, contents);
    }

    private static async Task HandleErrorResponse(HttpResponseMessage response, string responseJson)
    {
        if (response.IsSuccessStatusCode) return;

        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            var friendlyMessage = "Token limit reached.";
            try
            {
                using var errDoc = JsonDocument.Parse(responseJson);
                var root = errDoc.RootElement;
                if (root.TryGetProperty("resetsAt", out var resetsAtProp))
                {
                    var resetsAt = resetsAtProp.GetDateTime();
                    var remaining = resetsAt - DateTime.UtcNow;
                    if (remaining.TotalMinutes > 60)
                        friendlyMessage = $"Token limit reached. Resets in {remaining.Hours}h {remaining.Minutes}m.";
                    else if (remaining.TotalMinutes > 1)
                        friendlyMessage = $"Token limit reached. Resets in {(int)remaining.TotalMinutes} minutes.";
                    else
                        friendlyMessage = "Token limit reached. Resets shortly.";
                }
            }
            catch { }
            throw new InvalidOperationException(friendlyMessage);
        }

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException("Authentication required. Please log in to Pia Cloud.");
        }

        throw new HttpRequestException(
            $"Pia Cloud chat failed ({(int)response.StatusCode}): {responseJson}",
            null,
            response.StatusCode);
    }
}
