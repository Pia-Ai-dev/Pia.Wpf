using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Services.Consent;
using Pia.Tests.TestInfrastructure;
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
        TempPath.Remove(_tmpDir);
    }

    private static DpapiHelper SubstituteDpapi() =>
        Substitute.For<DpapiHelper>(NullLogger<DpapiHelper>.Instance);

    // Reversible so a test can decode the file back and see the store really went through Encrypt.
    private static void MakeReversible(DpapiHelper dpapi)
    {
        dpapi.Encrypt(Arg.Any<string>())
            .Returns(ci => Convert.ToBase64String(Encoding.UTF8.GetBytes(ci.Arg<string>())));
    }

    private static ConsentSessionMarker Session(string sessionId) => new(
        sessionId,
        new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.FromHours(2)),
        ConsentSessionMarker.DirectKind,
        ConsentNotice.Version,
        ConsentNotice.Purposes,
        "de");

    private static ConsentEvidence MakeEvidence(string label, string sentence) => new(
        SpeakerLabel: label,
        ExtractedName: "Alice",
        ConsentSentence: sentence,
        Language: "en",
        Confidence: 0.95f,
        GrantedAt: DateTimeOffset.UtcNow,
        SttModelId: "whisper-base",
        NoticeVersion: ConsentNotice.Version,
        NoticePurposes: ConsentNotice.Purposes,
        NoticeLanguage: "en");

    [Fact]
    public async Task SaveGrantAsync_WritesOneFilePerSpeaker_UnderTheSessionDirectory()
    {
        var dpapi = SubstituteDpapi();
        MakeReversible(dpapi);
        var sut = new ConsentEvidenceStore(_tmpDir, dpapi, NullLogger<ConsentEvidenceStore>.Instance);
        var sessionId = "session-1";

        await sut.SaveGrantAsync(Session(sessionId), MakeEvidence("Speaker 1", "yes, Pia may record"), TestContext.Current.CancellationToken);
        await sut.SaveGrantAsync(Session(sessionId), MakeEvidence("Speaker 2", "yes, Pia may record"), TestContext.Current.CancellationToken);

        var sessionDir = Path.Combine(_tmpDir, sessionId);
        Assert.True(Directory.Exists(sessionDir));
        var files = Directory.GetFiles(sessionDir, "*.json").Select(Path.GetFileName).Order(StringComparer.Ordinal);
        Assert.Equal(["Speaker 1.json", "Speaker 2.json", ConsentEvidenceStore.SessionMarkerFileName], files);
    }

    [Fact]
    public async Task SaveGrantAsync_WritesTheSessionMarkerWithTheFirstGrant_AndNeverRewritesIt()
    {
        var dpapi = SubstituteDpapi();
        MakeReversible(dpapi);
        var sut = new ConsentEvidenceStore(_tmpDir, dpapi, NullLogger<ConsentEvidenceStore>.Instance);
        var session = Session("session-5");
        var markerPath = Path.Combine(_tmpDir, session.SessionId, ConsentEvidenceStore.SessionMarkerFileName);

        await sut.SaveGrantAsync(session, MakeEvidence("Speaker 1", "yes, Pia may record"), TestContext.Current.CancellationToken);
        var first = await File.ReadAllBytesAsync(markerPath, TestContext.Current.CancellationToken);
        await sut.SaveGrantAsync(
            session with { StartedAt = session.StartedAt.AddHours(1) },
            MakeEvidence("Speaker 2", "yes, Pia may record"),
            TestContext.Current.CancellationToken);

        Assert.Equal(first, await File.ReadAllBytesAsync(markerPath, TestContext.Current.CancellationToken));

        using var marker = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(Encoding.UTF8.GetString(first))));
        Assert.Equal("pia-consent-session/v1", marker.RootElement.GetProperty("Schema").GetString());
        var written = marker.RootElement.GetProperty("Session");
        Assert.Equal(session.SessionId, written.GetProperty("SessionId").GetString());
        Assert.Equal(session.StartedAt, written.GetProperty("StartedAt").GetDateTimeOffset());
        Assert.Equal(ConsentSessionMarker.DirectKind, written.GetProperty("Kind").GetString());
        Assert.Equal(ConsentNotice.Version, written.GetProperty("NoticeVersion").GetInt32());
        Assert.Equal(ConsentNotice.Purposes, written.GetProperty("NoticePurposes").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal("de", written.GetProperty("NoticeLanguage").GetString());
    }

    [Fact]
    public async Task SaveGrantAsync_WritesAV2Envelope_ThatCitesTheNotice()
    {
        var dpapi = SubstituteDpapi();
        MakeReversible(dpapi);
        var sut = new ConsentEvidenceStore(_tmpDir, dpapi, NullLogger<ConsentEvidenceStore>.Instance);
        var session = Session("session-6");

        await sut.SaveGrantAsync(session, MakeEvidence("Speaker 1", "yes, Pia may record"), TestContext.Current.CancellationToken);

        var raw = await File.ReadAllTextAsync(
            Path.Combine(_tmpDir, session.SessionId, "Speaker 1.json"), TestContext.Current.CancellationToken);
        using var grant = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(raw)));
        Assert.Equal("pia-consent-evidence/v2", grant.RootElement.GetProperty("Schema").GetString());
        var evidence = grant.RootElement.GetProperty("Evidence");
        Assert.Equal(ConsentNotice.Version, evidence.GetProperty("NoticeVersion").GetInt32());
        Assert.Equal(ConsentNotice.Purposes, evidence.GetProperty("NoticePurposes").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal("en", evidence.GetProperty("NoticeLanguage").GetString());
    }

    [Fact]
    public async Task SaveGrantAsync_WhenEncryptReturnsEmpty_Throws_AndWritesNoFile()
    {
        var dpapi = SubstituteDpapi();
        dpapi.Encrypt(Arg.Any<string>()).Returns(string.Empty);
        var sut = new ConsentEvidenceStore(_tmpDir, dpapi, NullLogger<ConsentEvidenceStore>.Instance);
        var sessionId = "session-2";

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.SaveGrantAsync(Session(sessionId), MakeEvidence("Speaker 1", "yes, Pia may record"), TestContext.Current.CancellationToken));

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

        await sut.SaveGrantAsync(Session(sessionId), MakeEvidence("Speaker 1", $"yes, {Canary}, Pia may record"), TestContext.Current.CancellationToken);

        var sessionDir = Path.Combine(_tmpDir, sessionId);
        var file = Path.Combine(sessionDir, "Speaker 1.json");
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

        await sut.SaveGrantAsync(Session(sessionId), MakeEvidence("Speaker 1", "yes, Pia may record"), TestContext.Current.CancellationToken);
        var sessionDir = Path.Combine(_tmpDir, sessionId);
        var grantFile = Path.Combine(sessionDir, "Speaker 1.json");
        var beforeBytes = await File.ReadAllBytesAsync(grantFile, TestContext.Current.CancellationToken);

        await sut.SaveRevocationAsync(sessionId, "Speaker 1", DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        var afterBytes = await File.ReadAllBytesAsync(grantFile, TestContext.Current.CancellationToken);
        Assert.Equal(beforeBytes, afterBytes);

        var revocationFile = Path.Combine(sessionDir, "Speaker 1.revoked.json");
        Assert.True(File.Exists(revocationFile), "non-vacuity: a separate revocation file must have been written");
    }
}
