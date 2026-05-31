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
    private readonly ILogger _logger;

    public PiaCloudChatClient(HttpClient httpClient, string serverUrl, string? accessToken, ILogger logger, string? mode = null)
    {
        _httpClient = httpClient;
        _chatUrl = $"{serverUrl.TrimEnd('/')}/api/ai/chat";
        _mode = mode;
        _logger = logger;

        if (!string.IsNullOrEmpty(accessToken))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", accessToken);
        }

        if (!string.IsNullOrEmpty(mode))
        {
            _httpClient.DefaultRequestHeaders.Add("X-Pia-Mode", mode);
        }
    }

    public ChatClientMetadata Metadata => new("PiaCloud");

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var requestBody = BuildRequestBody(chatMessages, options, stream: false);
        _logger.LogDebug("PiaCloudChatClient: POST {Url} (non-streaming)", SafeUrl.Format(_chatUrl));

        using var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(_chatUrl, content, cancellationToken);

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

        using var request = new HttpRequestMessage(HttpMethod.Post, _chatUrl)
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
        };

        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            await HandleErrorResponse(response, errorBody);
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        // Track tool call accumulation across deltas
        var toolCallBuilders = new Dictionary<int, (string? Id, string? Name, StringBuilder Args)>();

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

            // OpenAI-compatible streams emit a final chunk with usage and empty choices when
            // stream_options.include_usage=true. Surface it as a UsageContent update so the
            // aggregated ChatResponse.Usage is populated downstream.
            if (TryParseUsage(json?["usage"]) is { } streamUsage)
            {
                yield return new ChatResponseUpdate
                {
                    Role = ChatRole.Assistant,
                    Contents = [new UsageContent(streamUsage)]
                };
            }

            var choices = json?["choices"]?.AsArray();
            if (choices is null || choices.Count == 0) continue;
            var choice = choices[0];
            if (choice is null) continue;

            var delta = choice["delta"];
            if (delta is null) continue;

            var finishReason = choice["finish_reason"]?.GetValue<string>();

            // Text content
            var textContent = delta["content"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(textContent))
            {
                yield return new ChatResponseUpdate
                {
                    Role = ChatRole.Assistant,
                    Contents = [new TextContent(textContent)]
                };
            }

            // Tool call deltas
            var toolCalls = delta["tool_calls"]?.AsArray();
            if (toolCalls is not null)
            {
                foreach (var tc in toolCalls)
                {
                    if (tc is null) continue;
                    var index = tc["index"]?.GetValue<int>() ?? 0;

                    if (!toolCallBuilders.TryGetValue(index, out var builder))
                    {
                        builder = (null, null, new StringBuilder());
                        toolCallBuilders[index] = builder;
                    }

                    var id = tc["id"]?.GetValue<string>();
                    if (id is not null) builder.Id = id;

                    var funcNode = tc["function"];
                    var name = funcNode?["name"]?.GetValue<string>();
                    if (name is not null) builder.Name = name;

                    var args = funcNode?["arguments"]?.GetValue<string>();
                    if (args is not null) builder.Args.Append(args);

                    toolCallBuilders[index] = builder;
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
    }

    private static IEnumerable<ChatResponseUpdate> EmitAccumulatedToolCalls(
        Dictionary<int, (string? Id, string? Name, StringBuilder Args)> builders)
    {
        foreach (var (_, (id, name, args)) in builders)
        {
            if (name is null) continue;

            IDictionary<string, object?>? arguments = null;
            var argsStr = args.ToString();
            if (!string.IsNullOrEmpty(argsStr))
            {
                try
                {
                    arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                        argsStr, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch
                {
                    arguments = new Dictionary<string, object?> { ["raw"] = argsStr };
                }
            }

            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = [new FunctionCallContent(id ?? "", name, arguments)]
            };
        }
    }

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

        var message = json["message"]?.AsObject()
            ?? throw new InvalidOperationException("Response missing 'message' field");

        var model = json["model"]?.GetValue<string>() ?? "pia-cloud";
        var finishReason = json["finishReason"]?.GetValue<string>();

        var chatMessage = ParseMessageObject(message);

        ChatFinishReason? chatFinishReason = finishReason switch
        {
            "stop" => ChatFinishReason.Stop,
            "tool_calls" => ChatFinishReason.ToolCalls,
            "length" => ChatFinishReason.Length,
            _ => null
        };

        return new ChatResponse([chatMessage])
        {
            ModelId = model,
            FinishReason = chatFinishReason,
            Usage = TryParseUsage(json["usage"])
        };
    }

    private static ChatMessage ParseMessageObject(JsonObject message)
    {
        var contents = new List<AIContent>();

        var textContent = message["content"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(textContent))
        {
            contents.Add(new TextContent(textContent));
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

                IDictionary<string, object?>? arguments = null;
                if (!string.IsNullOrEmpty(argsStr))
                {
                    try
                    {
                        arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                            argsStr, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    }
                    catch
                    {
                        arguments = new Dictionary<string, object?> { ["raw"] = argsStr };
                    }
                }

                contents.Add(new FunctionCallContent(id, name, arguments));
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
