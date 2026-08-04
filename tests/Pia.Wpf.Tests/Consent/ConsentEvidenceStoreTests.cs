using System.IO;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Services.Consent;
using Xunit;

namespace Pia.Tests.Consent;

/// <summary>
/// Measures the real on-disk behaviour of <see cref="ConsentEvidenceStore"/>: it writes one file per
/// speaker under the session directory, it throws (and writes nothing) instead of silently persisting
/// an empty evidence file when DPAPI protection fails, the raw plaintext consent sentence never lands
/// on disk unprotected, and a revocation never rewrites the grant file it sits beside.
/// <para>
/// <see cref="DpapiHelper"/> uses Windows DPAPI and would throw off Windows, so every test substitutes
/// it rather than using the real implementation.
/// </para>
/// </summary>
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

    // A reversible fake standing in for real DPAPI: proves the store round-trips through Encrypt
    // rather than writing raw JSON, without depending on the real Windows-only implementation.
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

        // Confirm the fake round-trips (proves the assertion above is meaningful, not vacuous because
        // Encrypt was never actually invoked).
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
