using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services.Consent;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Consent;

/// <summary>
/// Measures the real on-disk behaviour of <see cref="JsonlConsentAuditLog"/> against a temp file: one
/// JSON line per appended event, no transcript content ever present, and that <c>DisposeAsync</c>
/// actually flushes whatever was queued rather than dropping it on shutdown.
/// </summary>
public sealed class JsonlConsentAuditLogTests : IDisposable
{
    private readonly string _tmpDir = Path.Combine(Path.GetTempPath(), "PiaTests_" + Guid.NewGuid().ToString("N"));

    public JsonlConsentAuditLogTests() => Directory.CreateDirectory(_tmpDir);

    public void Dispose()
    {
        TempPath.Remove(_tmpDir);
    }

    [Fact]
    public async Task Append_WritesOneLinePerEvent()
    {
        var path = Path.Combine(_tmpDir, "audit.jsonl");
        await using (var sut = new JsonlConsentAuditLog(path, NullLogger<JsonlConsentAuditLog>.Instance))
        {
            sut.Append(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, ConsentAuditEventTypes.SpeakerDetected, "Speaker 1", null));
            sut.Append(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, ConsentAuditEventTypes.ConsentGranted, "Speaker 1", null));
        }

        var lines = await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(2, lines.Length);
        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            Assert.True(doc.RootElement.TryGetProperty("eventType", out _) ||
                        doc.RootElement.TryGetProperty("EventType", out _));
        }
    }

    [Fact]
    public async Task Append_NeverIncludesTranscriptContent()
    {
        var path = Path.Combine(_tmpDir, "audit.jsonl");
        await using (var sut = new JsonlConsentAuditLog(path, NullLogger<JsonlConsentAuditLog>.Instance))
        {
            sut.Append(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, ConsentAuditEventTypes.DroppedUnconsented, "Speaker 1",
                new Dictionary<string, object?> { ["reason"] = "not_granted" }));
        }

        var content = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("transcript", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("consentSentence", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NoAppend_CreatesNoFileAtAll()
    {
        // The assistant view is constructed at application startup and transitively resolves this
        // singleton, so opening Pia and never touching direct transcription used to leave one zero-byte
        // session_*.jsonl behind per launch — and hold its handle for the whole process lifetime — with no
        // cleanup path anywhere. The file must not exist until there is something to record.
        var path = Path.Combine(_tmpDir, "nested", "audit.jsonl");
        var sut = new JsonlConsentAuditLog(path, NullLogger<JsonlConsentAuditLog>.Instance);

        // Give the drain task a chance to have opened the file if it were going to.
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)!));

        await sut.DisposeAsync();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task FirstAppend_CreatesTheDirectoryAndTheFile()
    {
        // The lazy open owns directory creation too — the DI factory no longer pre-creates it.
        var path = Path.Combine(_tmpDir, "nested", "audit.jsonl");
        await using (var sut = new JsonlConsentAuditLog(path, NullLogger<JsonlConsentAuditLog>.Instance))
        {
            sut.Append(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, ConsentAuditEventTypes.SessionStarted, null, null));
        }

        Assert.True(File.Exists(path));
        Assert.Single(await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DisposeAsync_FlushesQueuedEvents()
    {
        var path = Path.Combine(_tmpDir, "audit.jsonl");
        var sut = new JsonlConsentAuditLog(path, NullLogger<JsonlConsentAuditLog>.Instance);

        sut.Append(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, ConsentAuditEventTypes.SessionStarted, null, null));
        sut.Append(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, ConsentAuditEventTypes.SessionStopped, null, null));

        await sut.DisposeAsync();

        var lines = await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(2, lines.Length);
    }
}
