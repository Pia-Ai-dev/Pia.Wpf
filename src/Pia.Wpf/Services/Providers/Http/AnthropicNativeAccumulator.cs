using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pia.Services.Providers.Http;

/// <summary>Rebuilds a Messages API response's content array from its SSE events, or reads it from a buffered body.</summary>
internal sealed class AnthropicNativeAccumulator
{
    private readonly SortedDictionary<int, JsonObject> _blocks = new();
    private readonly Dictionary<int, StringBuilder> _partialInput = new();
    private bool _failed;

    /// <summary>True once <c>message_stop</c> arrived with every block intact; a cut-off stream never is.</summary>
    public bool IsComplete { get; private set; }

    public IReadOnlyList<JsonObject> Blocks => [.. _blocks.Values];

    public static AnthropicNativeAccumulator FromBuffered(string body)
    {
        var accumulator = new AnthropicNativeAccumulator();
        try
        {
            if (JsonNode.Parse(body) is JsonObject { } root && root["content"] is JsonArray content)
            {
                for (var i = 0; i < content.Count; i++)
                {
                    if (content[i] is JsonObject block)
                        accumulator._blocks[i] = (JsonObject)block.DeepClone();
                }

                accumulator.IsComplete = true;
            }
        }
        catch (JsonException)
        {
        }

        return accumulator;
    }

    /// <summary>One SSE line; only <c>data:</c> lines carry anything.</summary>
    public void FeedLine(string line)
    {
        if (_failed || IsComplete || !line.StartsWith("data:", StringComparison.Ordinal))
            return;

        JsonObject? evt;
        try
        {
            evt = JsonNode.Parse(line.AsSpan(5).Trim().ToString()) as JsonObject;
        }
        catch (JsonException)
        {
            _failed = true;
            return;
        }

        if (evt is null)
            return;

        switch (Str(evt["type"]))
        {
            case "content_block_start" when Index(evt) is { } start && evt["content_block"] is JsonObject block:
                _blocks[start] = (JsonObject)block.DeepClone();
                if (Str(block["type"]) is "tool_use" or "server_tool_use")
                    _partialInput[start] = new StringBuilder();
                break;
            case "content_block_delta" when Index(evt) is { } at && _blocks.TryGetValue(at, out var target)
                                            && evt["delta"] is JsonObject delta:
                ApplyDelta(at, target, delta);
                break;
            case "content_block_stop" when Index(evt) is { } stop && _blocks.TryGetValue(stop, out var done):
                FinishInput(stop, done);
                break;
            case "message_stop":
                IsComplete = !_failed && _partialInput.Count == 0;
                break;
            case "error":
                _failed = true;
                break;
        }
    }

    private void ApplyDelta(int index, JsonObject block, JsonObject delta)
    {
        switch (Str(delta["type"]))
        {
            case "text_delta":
                block["text"] = Str(block["text"]) + Str(delta["text"]);
                break;
            case "citations_delta" when delta["citation"] is JsonObject citation:
                if (block["citations"] is not JsonArray citations)
                    block["citations"] = citations = [];
                citations.Add(citation.DeepClone());
                break;
            case "thinking_delta":
                block["thinking"] = Str(block["thinking"]) + Str(delta["thinking"]);
                break;
            case "signature_delta":
                block["signature"] = Str(delta["signature"]);
                break;
            case "input_json_delta" when _partialInput.TryGetValue(index, out var input):
                input.Append(Str(delta["partial_json"]));
                break;
        }
    }

    private void FinishInput(int index, JsonObject block)
    {
        if (!_partialInput.Remove(index, out var input))
            return;

        if (input.Length == 0)
            return;

        try
        {
            block["input"] = JsonNode.Parse(input.ToString());
        }
        catch (JsonException)
        {
            _failed = true;
        }
    }

    private static int? Index(JsonObject evt) =>
        evt["index"] is JsonValue value && value.TryGetValue<int>(out var index) ? index : null;

    private static string? Str(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;
}
