using System.IO;
using Microsoft.Extensions.Logging;

namespace Pia.Services.Consent;

/// <summary>
/// Deletes consent evidence and audit trails outside a window of DAYS, taking both roots as parameters so a
/// test never points it at the real profile.
/// </summary>
public static class ConsentRetention
{
    public const int DefaultRetainedDays = 14;

    // By name, not by extension: assignments.jsonl shares this folder and has a lifecycle of its own.
    private const string AuditSearchPattern = "session_*.jsonl";

    public static ConsentRetentionOutcome Sweep(string evidenceDirectory, string auditDirectory, int retainedDays) =>
        Sweep(evidenceDirectory, auditDirectory, retainedDays, DateTime.UtcNow);

    public static ConsentRetentionOutcome Sweep(
        string evidenceDirectory, string auditDirectory, int retainedDays, DateTime nowUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(retainedDays);

        var cutoff = nowUtc - TimeSpan.FromDays(retainedDays);
        var evidence = SweepEvidence(evidenceDirectory, cutoff);
        var audit = SweepAudit(auditDirectory, cutoff);

        return new ConsentRetentionOutcome(
            evidence.Kept, evidence.Deleted, audit.Kept, audit.Deleted, evidence.Skipped + audit.Skipped, cutoff);
    }

    /// <summary>Counts only: a session id and a speaker label are data in their own right.</summary>
    public static void LogOutcome(ILogger logger, ConsentRetentionOutcome outcome) =>
        logger.LogInformation(
            "Consent retention: evidence sessions kept {SessionsKept}, deleted {SessionsDeleted}; "
            + "audit files kept {AuditKept}, deleted {AuditDeleted}; skipped {Skipped}, cutoff {Cutoff:yyyy-MM-dd}",
            outcome.EvidenceSessionsKept, outcome.EvidenceSessionsDeleted,
            outcome.AuditFilesKept, outcome.AuditFilesDeleted, outcome.Skipped, outcome.Cutoff);

    private static (int Kept, int Deleted, int Skipped) SweepEvidence(string directory, DateTime cutoff)
    {
        if (!TryEnumerate(directory, Directory.GetDirectories, out var sessionDirectories))
            return (0, 0, 0);

        int kept = 0, deleted = 0, skipped = 0;
        foreach (var sessionDirectory in sessionDirectories)
        {
            DateTime lastWrite;
            try
            {
                lastWrite = NewestWriteIn(sessionDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++;
                continue;
            }

            if (lastWrite >= cutoff)
            {
                kept++;
                continue;
            }

            try
            {
                // The directory too, not only the files in it: the session id is itself a data point, so an
                // emptied folder would leave it on disk.
                Directory.Delete(sessionDirectory, recursive: true);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++;
            }
        }

        return (kept, deleted, skipped);
    }

    /// <summary>
    /// Newest write in the session directory, the directory's own stamp included — a grant whose write failed
    /// after <c>CreateDirectory</c> leaves an empty folder that still has to age out.
    /// </summary>
    private static DateTime NewestWriteIn(string sessionDirectory)
    {
        var newest = Directory.GetLastWriteTimeUtc(sessionDirectory);
        foreach (var file in Directory.EnumerateFiles(sessionDirectory, "*", SearchOption.AllDirectories))
        {
            var stamp = File.GetLastWriteTimeUtc(file);
            if (stamp > newest)
                newest = stamp;
        }
        return newest;
    }

    private static (int Kept, int Deleted, int Skipped) SweepAudit(string directory, DateTime cutoff)
    {
        if (!TryEnumerate(directory, d => Directory.GetFiles(d, AuditSearchPattern), out var paths))
            return (0, 0, 0);

        int kept = 0, deleted = 0, skipped = 0;
        foreach (var path in paths)
        {
            if (File.GetLastWriteTimeUtc(path) >= cutoff)
            {
                kept++;
                continue;
            }

            try
            {
                File.Delete(path);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++;
            }
        }

        return (kept, deleted, skipped);
    }

    private static bool TryEnumerate(string directory, Func<string, string[]> enumerate, out string[] entries)
    {
        entries = [];
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return false;

        try
        {
            entries = enumerate(directory);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>Skipped means only "could not delete" — the live session's own audit file is still open, and if it
/// ever ages out while the app runs it lands here until the next start.</summary>
public sealed record ConsentRetentionOutcome(
    int EvidenceSessionsKept,
    int EvidenceSessionsDeleted,
    int AuditFilesKept,
    int AuditFilesDeleted,
    int Skipped,
    DateTime Cutoff);
