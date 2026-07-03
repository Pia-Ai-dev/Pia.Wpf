using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pia.Services.Providers.Http;

/// <summary>
/// Rewrites Mistral responses so the model's reasoning becomes visible thinking content.
/// Mistral returns reasoning as <c>{"type":"thinking", ...}</c> entries inside the
/// <c>content</c> array, which (a) the OpenAI SDK can't deserialize (it throws on the
/// unknown part type) and (b) would otherwise be lost. We convert each thinking part into
/// a normal <c>{"type":"text","text":"&lt;think&gt;…&lt;/think&gt;"}</c> part, so the SDK
/// accepts it and <see cref="StreamThinkTagParser"/> can split it back out into
/// <c>ThinkingContent</c> downstream. Both the buffered JSON and streaming SSE paths are
/// handled.
/// </summary>
internal sealed class MistralThinkingResponseHandler : DelegatingHandler
{
    // Emit literal <think> tags rather than the default < escaping so the parser and
    // any debug logs see the markers verbatim. The result is still valid JSON.
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        var mediaType = response.Content.Headers.ContentType?.MediaType;

        if (mediaType == "application/json")
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var rewritten = RewriteThinkingParts(body);
            if (rewritten is not null)
                ReplaceContent(response, new StringContent(rewritten, Encoding.UTF8, "application/json"));
        }
        else if (mediaType == "text/event-stream")
        {
            // Wrap the live stream and transform each SSE line as it arrives, preserving
            // token-by-token streaming. A no-thinking stream passes through unchanged.
            var inner = await response.Content.ReadAsStreamAsync(cancellationToken);
            ReplaceContent(response, new StreamContent(new SseLineTransformStream(inner, RewriteStreamLine)));
        }

        return response;
    }

    /// <summary>Converts thinking parts in a complete (non-streaming) response body.
    /// Returns the rewritten JSON, or <c>null</c> when there was nothing to change or the
    /// body was not parseable.</summary>
    internal static string? RewriteThinkingParts(string? body)
    {
        if (string.IsNullOrEmpty(body))
            return null;

        JsonNode? root;
        try { root = JsonNode.Parse(body); }
        catch { return null; }

        if (root is not JsonObject obj)
            return null;

        return RewriteChoices(obj) ? obj.ToJsonString(SerializerOptions) : null;
    }

    /// <summary>Converts thinking parts in a single SSE line. Non-<c>data:</c> lines, the
    /// terminal <c>[DONE]</c> sentinel, unparseable payloads, and thinking-free chunks are
    /// returned verbatim.</summary>
    internal static string RewriteStreamLine(string line)
    {
        const string prefix = "data: ";
        if (!line.StartsWith(prefix, StringComparison.Ordinal))
            return line;

        var data = line[prefix.Length..];
        if (data == "[DONE]")
            return line;

        JsonNode? root;
        try { root = JsonNode.Parse(data); }
        catch { return line; }

        if (root is not JsonObject obj)
            return line;

        return RewriteChoices(obj) ? prefix + obj.ToJsonString(SerializerOptions) : line;
    }

    private static bool RewriteChoices(JsonObject root)
    {
        if (root["choices"] is not JsonArray choices)
            return false;

        var changed = false;
        foreach (var choice in choices)
        {
            if (choice is not JsonObject choiceObj)
                continue;

            // Complete responses carry `message`; streaming chunks carry `delta`.
            if (choiceObj["message"] is JsonObject message && message["content"] is JsonArray messageContent)
                changed |= ConvertThinkingParts(messageContent);

            if (choiceObj["delta"] is JsonObject delta && delta["content"] is JsonArray deltaContent)
                changed |= ConvertThinkingParts(deltaContent);
        }

        return changed;
    }

    private static bool ConvertThinkingParts(JsonArray content)
    {
        var changed = false;
        for (var i = 0; i < content.Count; i++)
        {
            if (content[i] is JsonObject part && part["type"]?.GetValue<string>() == "thinking")
            {
                var text = ExtractThinkingText(part["thinking"]);
                content[i] = new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = $"<think>{text}</think>",
                };
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>Mistral's <c>thinking</c> value is either a plain string or an array of
    /// <c>{"type":"text","text":…}</c> parts; this flattens both to a single string.</summary>
    private static string ExtractThinkingText(JsonNode? thinking)
    {
        switch (thinking)
        {
            case JsonValue value when value.TryGetValue<string>(out var s):
                return s;
            case JsonArray array:
                var sb = new StringBuilder();
                foreach (var part in array)
                    if (part?["text"] is JsonValue pv && pv.TryGetValue<string>(out var t))
                        sb.Append(t);
                return sb.ToString();
            default:
                return string.Empty;
        }
    }

    private static void ReplaceContent(HttpResponseMessage response, HttpContent newContent)
    {
        // Preserve the upstream Content-Type (e.g. text/event-stream) so downstream parsing
        // still engages. The previous content's stream is now owned by newContent, so we do
        // not dispose it here.
        if (response.Content.Headers.ContentType is { } contentType)
            newContent.Headers.ContentType = contentType;
        response.Content = newContent;
    }

    /// <summary>
    /// A read-only stream that transforms a text/event-stream line by line on the fly,
    /// re-emitting each line with a <c>\n</c> terminator so SSE event framing (the blank line
    /// between events) is preserved. Lines the transform leaves untouched pass through
    /// unchanged, so a thinking-free Mistral stream is effectively a passthrough.
    /// </summary>
    private sealed class SseLineTransformStream : Stream
    {
        private readonly StreamReader _reader;
        private readonly Func<string, string> _transform;
        private byte[] _pending = [];
        private int _pendingPos;
        private bool _completed;

        public SseLineTransformStream(Stream inner, Func<string, string> transform)
        {
            _reader = new StreamReader(inner, Encoding.UTF8);
            _transform = transform;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_pendingPos >= _pending.Length)
            {
                if (_completed)
                    return 0;

                var line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    _completed = true;
                    return 0;
                }

                _pending = Encoding.UTF8.GetBytes(_transform(line) + "\n");
                _pendingPos = 0;
            }

            var count = Math.Min(buffer.Length, _pending.Length - _pendingPos);
            _pending.AsMemory(_pendingPos, count).CopyTo(buffer);
            _pendingPos += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => await ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _reader.Dispose();
            base.Dispose(disposing);
        }
    }
}
