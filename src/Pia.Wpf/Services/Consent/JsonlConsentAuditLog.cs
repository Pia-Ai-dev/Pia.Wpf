using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Pia.Paths;

namespace Pia.Services.Consent;

/// <summary>
/// Append-only JSONL audit log writer. Events are queued on a bounded channel and drained by a single
/// background task. On overflow, drops the newest event with a logged warning — dropping audit lines
/// must be visible, not silent. <see cref="DisposeAsync"/> completes the writer and awaits the drain.
///
/// <para>Events carry metadata only (<see cref="AuditEvent"/>) — never transcript text, a consent
/// sentence, or an extracted name.</para>
/// </summary>
public sealed class JsonlConsentAuditLog : IConsentAuditLog
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private readonly string _path;
    private readonly ILogger<JsonlConsentAuditLog> _logger;
    private readonly Channel<AuditEvent> _queue;
    private readonly Task _drainLoop;
    private readonly CancellationTokenSource _cts = new();

    public JsonlConsentAuditLog(string path, ILogger<JsonlConsentAuditLog> logger)
    {
        _path = path;
        _logger = logger;
        _queue = Channel.CreateBounded<AuditEvent>(new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.DropNewest,
            SingleReader = true,
            SingleWriter = false,
        });
        _drainLoop = Task.Run(DrainAsync);
    }

    /// <summary>
    /// Builds a log rooted at <c>%LOCALAPPDATA%\Pia\ConsentAudit</c> with a fresh
    /// <c>session_{guid:N}.jsonl</c> file name per call. Neither the directory nor the file is created
    /// here — both are created on the first <see cref="Append"/>, so resolving this service without ever
    /// recording an audit event leaves nothing behind on disk.
    /// </summary>
    public static JsonlConsentAuditLog CreateForSession(ILogger<JsonlConsentAuditLog> logger)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var path = Path.Combine(PiaPaths.ConsentAuditDirectory, $"session_{sessionId}.jsonl");
        return new JsonlConsentAuditLog(path, logger);
    }

    public void Append(AuditEvent evt)
    {
        if (!_queue.Writer.TryWrite(evt))
            _logger.LogWarning("Consent audit log overflow — event {EventId} dropped", evt.EventId);
    }

    /// <summary>
    /// Drains the queue, opening the file LAZILY on the first event. Opening it eagerly created a
    /// zero-byte <c>session_*.jsonl</c> — and held its handle for the whole process lifetime — on every
    /// application launch, because the assistant view is constructed at startup and transitively resolves
    /// this singleton even when direct transcription is never opened. Nothing ever cleaned those up.
    /// </summary>
    private async Task DrainAsync()
    {
        FileStream? stream = null;
        StreamWriter? writer = null;
        try
        {
            await foreach (var evt in _queue.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                try
                {
                    if (writer is null)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                        stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
                        writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    }

                    var json = JsonSerializer.Serialize(evt, JsonOpts);
                    await writer.WriteLineAsync(json).ConfigureAwait(false);
                    await writer.FlushAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to write consent audit event {EventId}", evt.EventId);
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Consent audit log drain loop failed");
        }
        finally
        {
            if (writer is not null) await writer.DisposeAsync().ConfigureAwait(false);
            if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        try { await _drainLoop.ConfigureAwait(false); } catch { /* swallow */ }
        _cts.Dispose();
    }
}
