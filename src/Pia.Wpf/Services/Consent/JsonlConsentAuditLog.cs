using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Pia.Services.Consent;

/// <summary>
/// Append-only JSONL audit log writer. Events are queued on an unbounded channel and drained
/// by a single background task. On overflow, drops the newest event with a logged warning —
/// dropping audit lines must be visible. <see cref="DisposeAsync"/> completes the writer and
/// awaits the drain.
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

    public void Append(AuditEvent evt)
    {
        if (!_queue.Writer.TryWrite(evt))
            _logger.LogWarning("Consent audit log overflow — event {EventId} dropped", evt.EventId);
    }

    private async Task DrainAsync()
    {
        try
        {
            await using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            await foreach (var evt in _queue.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                try
                {
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
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        try { await _drainLoop.ConfigureAwait(false); } catch { /* swallow */ }
        _cts.Dispose();
    }
}
