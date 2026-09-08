using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Pia.Paths;
using Pia.Services.Interfaces;

namespace Pia.Services.Screen;

/// <summary>Append-only JSONL trail of every frame handed on: metadata only, the window title as a keyed hash.
/// A full queue drops the incoming entry with a warning, because dropping audit lines must be visible.</summary>
public sealed class ScreenCaptureAuditLog : IScreenCaptureAuditLog, IAsyncDisposable
{
    internal const int DefaultCapacity = 256;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private readonly string _path;
    private readonly ILogger<ScreenCaptureAuditLog> _logger;
    private readonly Channel<ScreenCaptureAuditEntry> _queue;
    private readonly Task _drainLoop;
    private readonly Func<string, Stream> _openStream;
    private readonly byte[] _titleKey;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _cts = new();

    public ScreenCaptureAuditLog(string path, ILogger<ScreenCaptureAuditLog> logger)
        : this(path, logger, DefaultCapacity)
    {
    }

    internal ScreenCaptureAuditLog(
        string path,
        ILogger<ScreenCaptureAuditLog> logger,
        int capacity,
        Func<string, Stream>? openStream = null,
        byte[]? titleKey = null,
        TimeProvider? clock = null)
    {
        _path = path;
        _logger = logger;
        _openStream = openStream ?? DefaultOpenStream;
        _titleKey = titleKey ?? ScreenCaptureTitleHash.NewKey();
        _clock = clock ?? TimeProvider.System;
        // The itemDropped callback, not TryWrite's result: every Drop* mode makes TryWrite return true, so a
        // caller-side check would never fire and the drop would be silent.
        _queue = Channel.CreateBounded<ScreenCaptureAuditEntry>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = false,
            },
            OnEntryDropped);
        _drainLoop = Task.Run(DrainAsync);
    }

    /// <summary>One file per launch, sortable by name; neither it nor the directory exists until the first
    /// capture is recorded.</summary>
    public static ScreenCaptureAuditLog CreateForSession(ILogger<ScreenCaptureAuditLog> logger)
    {
        var path = Path.Combine(
            PiaPaths.ScreenCaptureAuditDirectory,
            $"captures_{DateTime.UtcNow:yyyyMMdd-HHmmss}.jsonl");
        return new ScreenCaptureAuditLog(path, logger);
    }

    public void Record(ScreenCaptureAuditEvent evt)
    {
        var entry = new ScreenCaptureAuditEntry(
            _clock.GetUtcNow(),
            evt.Surface,
            evt.TaskId,
            evt.Granter,
            evt.TargetKind,
            evt.ProcessName,
            evt.Width,
            evt.Height,
            ScreenCaptureTitleHash.Compute(_titleKey, evt.WindowTitle));

        _queue.Writer.TryWrite(entry);
    }

    private void OnEntryDropped(ScreenCaptureAuditEntry entry) =>
        _logger.LogWarning("Screen capture audit log overflow — entry for {TargetKind} dropped", entry.TargetKind);

    private async Task DrainAsync()
    {
        Stream? stream = null;
        StreamWriter? writer = null;
        try
        {
            await foreach (var entry in _queue.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                try
                {
                    if (writer is null)
                    {
                        stream = _openStream(_path);
                        writer = new StreamWriter(
                            stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    }

                    await writer.WriteLineAsync(JsonSerializer.Serialize(entry, JsonOpts)).ConfigureAwait(false);
                    await writer.FlushAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to write a screen capture audit entry");
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Screen capture audit log drain loop failed");
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

    private static Stream DefaultOpenStream(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
    }
}
