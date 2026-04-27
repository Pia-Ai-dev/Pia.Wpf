using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure;
using Pia.Services.Consent;
using Xunit;

namespace Pia.Wpf.Tests.Consent;

public sealed class HashChainedAuditLogTests : IDisposable
{
    private readonly string _tmpDir = Path.Combine(Path.GetTempPath(), "pia-chain-tests-" + Guid.NewGuid().ToString("N"));

    public HashChainedAuditLogTests() { Directory.CreateDirectory(_tmpDir); }
    public void Dispose() { try { Directory.Delete(_tmpDir, true); } catch { } }

    private (string logPath, AuditChainSigner signer) SetUp()
    {
        var manifestPath = Path.Combine(_tmpDir, "manifest.json");
        var dpapi = new DpapiHelper(NullLogger<DpapiHelper>.Instance);
        var signer = AuditChainSigner.LoadOrCreate(manifestPath, dpapi);
        return (Path.Combine(_tmpDir, "audit.jsonl"), signer);
    }

    [Fact]
    public async Task AppendedEvents_FormHashChain()
    {
        var (path, signer) = SetUp();
        await using (var sut = new HashChainedAuditLog(path, signer, NullLogger<HashChainedAuditLog>.Instance))
        {
            sut.Append(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, "A", "S1", null));
            sut.Append(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, "B", "S1", null));
            sut.Append(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, "C", "S1", null));
        }

        var lines = await File.ReadAllLinesAsync(path);
        Assert.Equal(3, lines.Length);

        var e0 = JsonSerializer.Deserialize<AuditEvent>(lines[0])!;
        var e1 = JsonSerializer.Deserialize<AuditEvent>(lines[1])!;
        var e2 = JsonSerializer.Deserialize<AuditEvent>(lines[2])!;

        Assert.Null(e0.PreviousEventHash);
        Assert.Equal(AuditChainSigner.HashEventWithoutSignature(e0), e1.PreviousEventHash);
        Assert.Equal(AuditChainSigner.HashEventWithoutSignature(e1), e2.PreviousEventHash);
    }

    [Fact]
    public async Task SignedEvents_VerifyAgainstSessionPublicKey()
    {
        var (path, signer) = SetUp();
        await using (var sut = new HashChainedAuditLog(path, signer, NullLogger<HashChainedAuditLog>.Instance))
        {
            sut.Append(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, "A", "S1", null));
            sut.Append(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, "B", "S1", null));
        }

        var (ok, idx) = HashChainedAuditLog.Verify(path, signer.PublicKeyBase64);
        Assert.True(ok);
        Assert.Equal(-1, idx);
    }

    [Fact]
    public async Task TamperedLine_BreaksChainVerification()
    {
        var (path, signer) = SetUp();
        await using (var sut = new HashChainedAuditLog(path, signer, NullLogger<HashChainedAuditLog>.Instance))
        {
            sut.Append(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, "A", "S1", null));
            sut.Append(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, "B", "S1", null));
        }

        // Tamper with the second line: change EventType.
        var lines = await File.ReadAllLinesAsync(path);
        var tampered = JsonSerializer.Deserialize<AuditEvent>(lines[1])! with { EventType = "MALICIOUS" };
        lines[1] = JsonSerializer.Serialize(tampered);
        await File.WriteAllLinesAsync(path, lines);

        var (ok, idx) = HashChainedAuditLog.Verify(path, signer.PublicKeyBase64);
        Assert.False(ok);
        Assert.Equal(1, idx);
    }

    [Fact]
    public async Task NewLog_SeedsFromExistingLastLine()
    {
        var (path, signer) = SetUp();
        // First session
        await using (var sut = new HashChainedAuditLog(path, signer, NullLogger<HashChainedAuditLog>.Instance))
        {
            sut.Append(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, "A", "S1", null));
        }
        // Second session continues the chain
        await using (var sut = new HashChainedAuditLog(path, signer, NullLogger<HashChainedAuditLog>.Instance))
        {
            sut.Append(new AuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, "B", "S1", null));
        }

        var (ok, idx) = HashChainedAuditLog.Verify(path, signer.PublicKeyBase64);
        Assert.True(ok);
        Assert.Equal(-1, idx);
    }

    [Fact]
    public void Signer_RoundTrips_PrivateKey_AcrossLoad()
    {
        var manifestPath = Path.Combine(_tmpDir, "manifest.json");
        var dpapi = new DpapiHelper(NullLogger<DpapiHelper>.Instance);
        var first = AuditChainSigner.LoadOrCreate(manifestPath, dpapi);
        var second = AuditChainSigner.LoadOrCreate(manifestPath, dpapi);
        Assert.Equal(first.PublicKeyBase64, second.PublicKeyBase64);
    }
}
