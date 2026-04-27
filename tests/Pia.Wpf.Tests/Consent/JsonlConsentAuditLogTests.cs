using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Services.Consent;
using Xunit;

namespace Pia.Wpf.Tests.Consent;

public sealed class JsonlConsentAuditLogTests : IDisposable
{
    private readonly string _tmpDir = Path.Combine(Path.GetTempPath(), "pia-consent-tests-" + Guid.NewGuid().ToString("N"));

    public JsonlConsentAuditLogTests() { Directory.CreateDirectory(_tmpDir); }
    public void Dispose() { try { Directory.Delete(_tmpDir, true); } catch { } }

    [Fact]
    public async Task Append_WritesOneLinePerEvent()
    {
        var path = Path.Combine(_tmpDir, "audit.jsonl");
        await using (var sut = new JsonlConsentAuditLog(path, NullLogger<JsonlConsentAuditLog>.Instance))
        {
            sut.Append(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, "SPEAKER_JOINED", "Speaker 1", null));
            sut.Append(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, "CONSENT_GRANTED", "Speaker 1", null));
        }

        var lines = await File.ReadAllLinesAsync(path);
        Assert.Equal(2, lines.Length);
        foreach (var l in lines) JsonDocument.Parse(l).Dispose();
    }

    [Fact]
    public async Task Append_NeverIncludesTranscriptContent()
    {
        var path = Path.Combine(_tmpDir, "audit.jsonl");
        await using (var sut = new JsonlConsentAuditLog(path, NullLogger<JsonlConsentAuditLog>.Instance))
        {
            sut.Append(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, "DROPPED_TRANSCRIPT_NO_CONSENT", "Speaker 1",
                new Dictionary<string, object?> { ["reason"] = "post_stt_filter" }));
        }

        var content = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("transcript_text", content);
    }
}
