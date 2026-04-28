using System.IO;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure;
using Pia.Services.Consent;
using Pia.Services.Consent.Snippet;
using Xunit;

namespace Pia.Wpf.Tests.Consent.Snippet;

public sealed class ConsentSnippetRecorderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FakeAuditLog _audit = new();
    private readonly SessionEncryption _enc = SessionEncryption.CreateSession();

    public ConsentSnippetRecorderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "PiaSnippetTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private sealed class FakeAuditLog : IConsentAuditLog
    {
        public readonly List<AuditEvent> Events = new();
        public void Append(AuditEvent ev) { lock (Events) Events.Add(ev); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private ConsentSnippetRecorder Make() => new(
        _enc, _audit, TimeProvider.System,
        NullLogger<ConsentSnippetRecorder>.Instance);

    private static byte[] FakeWav(string content) => Encoding.UTF8.GetBytes(content);

    [Fact]
    public void Persist_FlagOff_DoesNothing()
    {
        var sut = Make();
        var path = sut.Persist(SecurityProfile.Standard, _tempDir, "S1", FakeWav("audio"));
        Assert.Null(path);
        Assert.Empty(_audit.Events);
        Assert.False(Directory.Exists(Path.Combine(_tempDir, "consent")));
    }

    [Fact]
    public void Persist_FlagOn_WritesEncryptedFile_AndAudits()
    {
        var sut = Make();
        var path = sut.Persist(SecurityProfile.Strict, _tempDir, "Speaker 1", FakeWav("audio-bytes"));
        Assert.NotNull(path);
        Assert.True(File.Exists(path));

        var bytes = File.ReadAllBytes(path!);
        // Encrypted file must NOT contain the plaintext token.
        Assert.DoesNotContain(Encoding.UTF8.GetBytes("audio-bytes"), bytes);
        Assert.Contains(_audit.Events, e => e.EventType == "SNIPPET_PERSISTED");
    }

    [Fact]
    public void Persist_EncryptedFile_DecryptsBackToOriginal()
    {
        var sut = Make();
        var original = FakeWav("hello-audio");
        var path = sut.Persist(SecurityProfile.Strict, _tempDir, "S1", original);
        Assert.NotNull(path);

        var ct = File.ReadAllBytes(path!);
        var pt = _enc.Decrypt(ct);
        Assert.Equal(original, pt);
    }

    [Fact]
    public void Delete_RemovesFile_ReturnsTrue()
    {
        var sut = Make();
        var path = sut.Persist(SecurityProfile.Strict, _tempDir, "S1", FakeWav("a"));
        Assert.True(File.Exists(path));

        var deleted = sut.Delete(_tempDir, "S1");
        Assert.True(deleted);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Delete_MissingFile_ReturnsFalse()
    {
        var sut = Make();
        Assert.False(sut.Delete(_tempDir, "Nope"));
    }

    [Fact]
    public void Persist_EmptyAudio_DoesNothing()
    {
        var sut = Make();
        var path = sut.Persist(SecurityProfile.Strict, _tempDir, "S1", ReadOnlySpan<byte>.Empty);
        Assert.Null(path);
    }
}
