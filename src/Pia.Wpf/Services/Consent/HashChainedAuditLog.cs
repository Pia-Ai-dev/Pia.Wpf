using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Pia.Services.Consent;

/// <summary>
/// Append-only JSONL audit log with a SHA-256 hash chain and per-event Ed25519/ECDSA-P256
/// signatures. Each appended event references the prior event's hash so any tampering breaks
/// chain verification. Drop-newest overflow policy is shared with the Phase-1 writer.
/// </summary>
public sealed class HashChainedAuditLog : IConsentAuditLog
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private readonly string _path;
    private readonly AuditChainSigner _signer;
    private readonly ILogger<HashChainedAuditLog> _logger;
    private readonly Channel<AuditEvent> _queue;
    private readonly Task _drainLoop;
    private readonly CancellationTokenSource _cts = new();

    private string? _previousHash;

    public HashChainedAuditLog(string path, AuditChainSigner signer, ILogger<HashChainedAuditLog> logger)
    {
        _path = path;
        _signer = signer;
        _logger = logger;
        _previousHash = SeedFromExistingFile(path);
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

    private static string? SeedFromExistingFile(string path)
    {
        if (!File.Exists(path)) return null;
        string? lastLine = null;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        string? line;
        while ((line = reader.ReadLine()) is not null)
            if (!string.IsNullOrWhiteSpace(line)) lastLine = line;
        if (lastLine is null) return null;
        try
        {
            var prev = JsonSerializer.Deserialize<AuditEvent>(lastLine, JsonOpts);
            return prev is null ? null : AuditChainSigner.HashEventWithoutSignature(prev);
        }
        catch { return null; }
    }

    private async Task DrainAsync()
    {
        try
        {
            await using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            await foreach (var raw in _queue.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                try
                {
                    var withChain = raw with { PreviousEventHash = _previousHash };
                    var signature = _signer.Sign(withChain);
                    var signed = withChain with { Signature = signature };
                    var json = JsonSerializer.Serialize(signed, JsonOpts);
                    await writer.WriteLineAsync(json).ConfigureAwait(false);
                    await writer.FlushAsync().ConfigureAwait(false);
                    _previousHash = AuditChainSigner.HashEventWithoutSignature(signed);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to write consent audit event {EventId}", raw.EventId);
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Consent audit log drain loop failed");
        }
    }

    /// <summary>
    /// Verifies a JSONL audit log against the supplied public key. Returns
    /// (true, -1) if the chain is intact, otherwise (false, lineIndex) for the first broken line.
    /// </summary>
    public static (bool ok, int firstBrokenLine) Verify(string path, string publicKeyBase64)
    {
        if (!File.Exists(path)) return (false, 0);
        string? expectedPrev = null;
        var idx = 0;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) { idx++; continue; }
            AuditEvent? evt;
            try { evt = JsonSerializer.Deserialize<AuditEvent>(line, JsonOpts); }
            catch { return (false, idx); }
            if (evt is null) return (false, idx);

            // Phase-1 lines have no Signature/PreviousEventHash and are treated as the chain root.
            if (evt.Signature is null) { expectedPrev = null; idx++; continue; }
            if (evt.PreviousEventHash != expectedPrev) return (false, idx);
            if (!AuditChainSigner.Verify(evt, publicKeyBase64)) return (false, idx);
            expectedPrev = AuditChainSigner.HashEventWithoutSignature(evt);
            idx++;
        }
        return (true, -1);
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        try { await _drainLoop.ConfigureAwait(false); } catch { /* swallow */ }
        _cts.Dispose();
    }
}
