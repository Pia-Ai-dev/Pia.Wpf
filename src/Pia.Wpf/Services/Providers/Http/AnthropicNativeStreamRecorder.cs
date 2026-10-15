using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;

namespace Pia.Services.Providers.Http;

/// <summary>Hands every byte to the SDK unchanged while feeding a copy to the accumulator; reports once, at end of stream.</summary>
internal sealed class AnthropicNativeStreamRecorder : Stream
{
    // A buffered body larger than this is not worth replaying; the SDK still reads all of it.
    private const int MaxBufferedChars = 4 * 1024 * 1024;

    private readonly Stream _inner;
    private readonly bool _isEventStream;
    private readonly Action<AnthropicNativeContentBuilder> _onComplete;
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly AnthropicNativeContentBuilder _events = new();
    private readonly StringBuilder _pending = new();
    private bool _reported;
    private bool _overflowed;

    public AnthropicNativeStreamRecorder(Stream inner, bool isEventStream, Action<AnthropicNativeContentBuilder> onComplete)
    {
        _inner = inner;
        _isEventStream = isEventStream;
        _onComplete = onComplete;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var read = _inner.Read(buffer);
        Observe(buffer[..read]);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Observe(buffer.Span[..read]);
        return read;
    }

    private void Observe(ReadOnlySpan<byte> bytes)
    {
        if (_reported)
            return;

        if (bytes.Length == 0)
        {
            Report();
            return;
        }

        if (_overflowed)
            return;

        var chars = new char[_decoder.GetCharCount(bytes, flush: false)];
        _decoder.GetChars(bytes, chars, flush: false);
        _pending.Append(chars);

        if (!_isEventStream)
        {
            if (_pending.Length > MaxBufferedChars)
            {
                _overflowed = true;
                _pending.Clear();
            }

            return;
        }

        var start = 0;
        for (var i = 0; i < _pending.Length; i++)
        {
            if (_pending[i] != '\n')
                continue;

            _events.FeedLine(_pending.ToString(start, i - start).TrimEnd('\r'));
            start = i + 1;
            // The reader may stop at message_stop without draining to end of stream.
            if (_events.IsComplete)
            {
                Report();
                return;
            }
        }

        _pending.Remove(0, start);
    }

    private void Report()
    {
        _reported = true;
        if (_overflowed)
            return;

        var result = _isEventStream ? _events : AnthropicNativeContentBuilder.FromBuffered(_pending.ToString());
        _pending.Clear();
        if (result.IsComplete)
            _onComplete(result);
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Wraps a response body in <see cref="AnthropicNativeStreamRecorder"/> without buffering it; headers are copied over.</summary>
internal sealed class AnthropicNativeResponseRecorder : HttpContent
{
    private readonly HttpContent _original;
    private readonly bool _isEventStream;
    private readonly Action<AnthropicNativeContentBuilder> _onComplete;

    public AnthropicNativeResponseRecorder(HttpContent original, bool isEventStream, Action<AnthropicNativeContentBuilder> onComplete)
    {
        _original = original;
        _isEventStream = isEventStream;
        _onComplete = onComplete;
        foreach (var header in original.Headers)
            Headers.TryAddWithoutValidation(header.Key, header.Value);
    }

    protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
        new AnthropicNativeStreamRecorder(
            await _original.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), _isEventStream, _onComplete);

    protected override Task<Stream> CreateContentReadStreamAsync() => CreateContentReadStreamAsync(CancellationToken.None);

    protected override Stream CreateContentReadStream(CancellationToken cancellationToken) =>
        new AnthropicNativeStreamRecorder(_original.ReadAsStream(cancellationToken), _isEventStream, _onComplete);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        await using var source = await CreateContentReadStreamAsync(cancellationToken).ConfigureAwait(false);
        await source.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _original.Dispose();
        base.Dispose(disposing);
    }
}
