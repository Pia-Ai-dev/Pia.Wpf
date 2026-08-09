using System.IO;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Services.Consent;
using Xunit;

namespace Pia.Tests.Consent;

/// <summary><see cref="DpapiHelper"/> would throw off Windows, so every test here substitutes it.</summary>
public sealed class ConsentEvidenceStoreTests : IDisposable
{
    private const string Canary = "CANARY-9f3a1c";

    private readonly string _tmpDir = Path.Combine(Path.GetTempPath(), "PiaTests_" + Guid.NewGuid().ToString("N"));

    public ConsentEvidenceStoreTests() => Directory.CreateDirectory(_tmpDir);

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static DpapiHelper SubstituteDpapi() =>
        Substitute.For<DpapiHelper>(NullLogger<DpapiHelper>.Instance);

    // Reversible so a test can decode the file back and see the store really went through Encrypt.
    private static void MakeReversible(DpapiHelper dpapi)
    {
        dpapi.Encrypt(Arg.Any<string>())
            .Returns(ci => Convert.ToBase64String(Encoding.UTF8.GetBytes(ci.Arg<string>())));
    }

    private static ConsentEvidence MakeEvidence(string label, string sentence) => new(
        SpeakerLabel: label,
        ExtractedName: "Alice",
        ConsentSentence: sentence,
        Language: "en",
        Confidence: 0.95f,
        GrantedAt: DateTimeOffset.UtcNow,
        SttModelId: "whisper-base");

    [Fact]
    public async Task SaveGrantAsync_WritesOneFilePerSpeaker_UnderTheSessionDirectory()
    {
        var dpapi = SubstituteDpapi();
        MakeReversible(dpapi);
        var sut = new ConsentEvidenceStore(_tmpDir, dpapi, NullLogger<ConsentEvidenceStore>.Instance);
        var sessionId = "session-1";

        await sut.SaveGrantAsync(sessionId, MakeEvidence("Speaker 1", "yes, Pia may record"), TestContext.Current.CancellationToken);
        await sut.SaveGrantAsync(sessionId, MakeEvidence("Speaker 2", "yes, Pia may record"), TestContext.Current.CancellationToken);

        var sessionDir = Path.Combine(_tmpDir, sessionId);
        Assert.True(Directory.Exists(sessionDir));
        var files = Directory.GetFiles(sessionDir, "*.json");
        Assert.Equal(2, files.Length);
    }

    [Fact]
    public async Task SaveGrantAsync_WhenEncryptReturnsEmpty_Throws_AndWritesNoFile()
    {
        var dpapi = SubstituteDpapi();
        dpapi.Encrypt(Arg.Any<string>()).Returns(string.Empty);
        var sut = new ConsentEvidenceStore(_tmpDir, dpapi, NullLogger<ConsentEvidenceStore>.Instance);
        var sessionId = "session-2";

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.SaveGrantAsync(sessionId, MakeEvidence("Speaker 1", "yes, Pia may record"), TestContext.Current.CancellationToken));

        var sessionDir = Path.Combine(_tmpDir, sessionId);
        var files = Directory.Exists(sessionDir) ? Directory.GetFiles(sessionDir, "*.json") : [];
        Assert.Empty(files);
    }

    [Fact]
    public async Task SaveGrantAsync_PlaintextSentenceNeverAppearsOnDisk()
    {
        var dpapi = SubstituteDpapi();
        MakeReversible(dpapi);
        var sut = new ConsentEvidenceStore(_tmpDir, dpapi, NullLogger<ConsentEvidenceStore>.Instance);
        var sessionId = "session-3";

        await sut.SaveGrantAsync(sessionId, MakeEvidence("Speaker 1", $"yes, {Canary}, Pia may record"), TestContext.Current.CancellationToken);

        var sessionDir = Path.Combine(_tmpDir, sessionId);
        var file = Assert.Single(Directory.GetFiles(sessionDir, "*.json"));
        var raw = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(Canary, raw, StringComparison.Ordinal);

        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(raw));
        Assert.Contains(Canary, decoded, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveRevocationAsync_LeavesTheGrantFileByteIdentical()
    {
        var dpapi = SubstituteDpapi();
        MakeReversible(dpapi);
        var sut = new ConsentEvidenceStore(_tmpDir, dpapi, NullLogger<ConsentEvidenceStore>.Instance);
        var sessionId = "session-4";

        await sut.SaveGrantAsync(sessionId, MakeEvidence("Speaker 1", "yes, Pia may record"), TestContext.Current.CancellationToken);
        var sessionDir = Path.Combine(_tmpDir, sessionId);
        var grantFile = Assert.Single(Directory.GetFiles(sessionDir, "*.json"), f => !f.EndsWith(".revoked.json", StringComparison.Ordinal));
        var beforeBytes = await File.ReadAllBytesAsync(grantFile, TestContext.Current.CancellationToken);

        await sut.SaveRevocationAsync(sessionId, "Speaker 1", DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        var afterBytes = await File.ReadAllBytesAsync(grantFile, TestContext.Current.CancellationToken);
        Assert.Equal(beforeBytes, afterBytes);

        var revocationFile = Path.Combine(sessionDir, "Speaker 1.revoked.json");
        Assert.True(File.Exists(revocationFile), "non-vacuity: a separate revocation file must have been written");
    }
}
