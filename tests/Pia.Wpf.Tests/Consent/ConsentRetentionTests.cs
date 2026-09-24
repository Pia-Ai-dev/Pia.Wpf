using System.IO;
using Pia.Services.Consent;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Consent;

/// <summary>
/// Temp directories only. The sweep resolves no path of its own, so nothing here can reach the real profile —
/// which matters more than usual, because <c>ConsentEvidenceDirectory</c> ignores the profile override.
/// </summary>
public sealed class ConsentRetentionTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pia-consent-retention-{Guid.NewGuid():N}");
    private readonly string _evidence;
    private readonly string _audit;

    public ConsentRetentionTests()
    {
        _evidence = Path.Combine(_root, "ConsentEvidence");
        _audit = Path.Combine(_root, "ConsentAudit");
        Directory.CreateDirectory(_evidence);
        Directory.CreateDirectory(_audit);
    }

    public void Dispose() => TempPath.Remove(_root);

    private ConsentRetentionOutcome Sweep(int retainedDays = ConsentRetention.DefaultRetainedDays) =>
        ConsentRetention.Sweep(_evidence, _audit, retainedDays, Now);

    private string Session(string sessionId, double ageInDays, params string[] fileNames)
    {
        var directory = Path.Combine(_evidence, sessionId);
        Directory.CreateDirectory(directory);
        var stamp = Now.AddDays(-ageInDays);

        foreach (var fileName in fileNames)
        {
            var path = Path.Combine(directory, fileName);
            File.WriteAllText(path, "protected");
            File.SetLastWriteTimeUtc(path, stamp);
        }

        Directory.SetLastWriteTimeUtc(directory, stamp);
        return directory;
    }

    private string AuditFile(string name, double ageInDays)
    {
        var path = Path.Combine(_audit, name);
        File.WriteAllText(path, "{\"eventId\":1}\n");
        File.SetLastWriteTimeUtc(path, Now.AddDays(-ageInDays));
        return path;
    }

    [Fact]
    public void ASessionOutsideTheWindowGoes_DirectoryIncluded()
    {
        var stale = Session("aaa", 15, "Speaker 1.json", "Speaker 1.revoked.json");

        var outcome = Sweep();

        Assert.False(Directory.Exists(stale));
        Assert.Equal(1, outcome.EvidenceSessionsDeleted);
        Assert.Equal(0, outcome.EvidenceSessionsKept);
        Assert.Equal(0, outcome.Skipped);
    }

    [Fact]
    public void ASessionInsideTheWindowStays()
    {
        var fresh = Session("bbb", 13, "Speaker 1.json");

        var outcome = Sweep();

        Assert.True(Directory.Exists(fresh));
        Assert.Equal(1, outcome.EvidenceSessionsKept);
        Assert.Equal(0, outcome.EvidenceSessionsDeleted);
    }

    [Fact]
    public void ASessionAgesOnItsNewestFile_NotItsOldest()
    {
        var directory = Session("ccc", 20, "Speaker 1.json");
        var revocation = Path.Combine(directory, "Speaker 1.revoked.json");
        File.WriteAllText(revocation, "protected");
        File.SetLastWriteTimeUtc(revocation, Now.AddDays(-2));
        // Last, and stale: it is the revocation date alone that has to hold the session, not the folder stamp
        // the write just refreshed.
        Directory.SetLastWriteTimeUtc(directory, Now.AddDays(-20));

        var outcome = Sweep();

        Assert.True(Directory.Exists(directory));
        Assert.Equal(1, outcome.EvidenceSessionsKept);
    }

    [Fact]
    public void AnEmptySessionDirectoryStillAgesOut()
    {
        var stale = Session("ddd", 15);

        var outcome = Sweep();

        Assert.False(Directory.Exists(stale));
        Assert.Equal(1, outcome.EvidenceSessionsDeleted);
    }

    [Fact]
    public void OldAuditSessionFilesGo_AndTheAssignmentTrailStays()
    {
        var stale = AuditFile("session_9f3a1c.jsonl", 15);
        var fresh = AuditFile("session_beef42.jsonl", 1);
        var assignments = AuditFile("assignments.jsonl", 400);

        var outcome = Sweep();

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(assignments));
        Assert.Equal(1, outcome.AuditFilesDeleted);
        Assert.Equal(1, outcome.AuditFilesKept);
    }

    [Fact]
    public void MissingDirectoriesAreNotAnError()
    {
        var outcome = ConsentRetention.Sweep(
            Path.Combine(_root, "gone"), Path.Combine(_root, "also-gone"),
            ConsentRetention.DefaultRetainedDays, Now);

        Assert.Equal(0, outcome.EvidenceSessionsDeleted);
        Assert.Equal(0, outcome.AuditFilesDeleted);
        Assert.Equal(0, outcome.Skipped);
    }

    [Fact]
    public void TheCutoffIsTheWindowBeforeNow()
    {
        var outcome = Sweep();

        Assert.Equal(Now.AddDays(-14), outcome.Cutoff);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ANonPositiveWindowThrows(int retainedDays) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Sweep(retainedDays));

    [Fact]
    public void BothStoresSweepInOnePass()
    {
        Session("aaa", 15, "Speaker 1.json");
        Session("bbb", 1, "Speaker 1.json");
        AuditFile("session_aaa.jsonl", 15);
        AuditFile("session_bbb.jsonl", 1);

        var outcome = Sweep();

        Assert.Equal(1, outcome.EvidenceSessionsDeleted);
        Assert.Equal(1, outcome.EvidenceSessionsKept);
        Assert.Equal(1, outcome.AuditFilesDeleted);
        Assert.Equal(1, outcome.AuditFilesKept);
    }
}
