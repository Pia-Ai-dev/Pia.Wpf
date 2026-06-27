using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Pia.Infrastructure.Vault;

public enum FolderMovePhase { Copying, Verifying, CleaningUp }

public record FolderMoveProgress(FolderMovePhase Phase, int PercentComplete, string? CurrentItem = null);

public enum DirectoryMoveOutcome { Success, CopyFailed, VerifyFailed }

public record DirectoryMoveResult(DirectoryMoveOutcome Outcome, string? Error = null);

/// <summary>
/// Copy → verify → delete a directory tree with rollback. The source is the source of truth until
/// verify passes: any failure before the delete step keeps the source intact and removes the partial
/// destination (only if WE created it). Used by both the user-initiated folder move and the startup
/// in-place vault migration.
/// </summary>
public static class SafeDirectoryMove
{
    // The copy/verify/delete is synchronous I/O; run it on the thread pool so a UI-thread caller
    // (the settings command awaiting a progress dialog) does not freeze. Progress<T> marshals the
    // report callback back to the captured (UI) context on its own.
    public static Task<DirectoryMoveResult> MoveAsync(
        string source, string destination,
        IProgress<FolderMoveProgress>? progress,
        CancellationToken ct,
        Func<bool>? verifyOverride = null)
        => Task.Run(() => MoveCore(source, destination, progress, ct, verifyOverride), ct);

    private static DirectoryMoveResult MoveCore(
        string source, string destination,
        IProgress<FolderMoveProgress>? progress,
        CancellationToken ct,
        Func<bool>? verifyOverride)
    {
        if (!Directory.Exists(source))
            return new DirectoryMoveResult(DirectoryMoveOutcome.Success); // nothing to move

        // Only a destination WE created may be wiped on rollback — never delete a pre-existing
        // user folder. (Validation also rejects a non-empty target, so this is defense in depth.)
        var destExistedBefore = Directory.Exists(destination);

        try
        {
            // 1) COPY
            var files = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
            var total = Math.Max(files.Length, 1);
            Directory.CreateDirectory(destination);
            for (int i = 0; i < files.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(source, files[i]);
                var target = Path.Combine(destination, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(files[i], target, overwrite: true);
                progress?.Report(new FolderMoveProgress(
                    FolderMovePhase.Copying, (int)((i + 1) * 100L / total), rel));
            }

            // 2) VERIFY
            progress?.Report(new FolderMoveProgress(FolderMovePhase.Verifying, 100));
            var ok = verifyOverride?.Invoke() ?? Verify(source, destination);
            if (!ok)
            {
                if (!destExistedBefore) TryDelete(destination);
                return new DirectoryMoveResult(DirectoryMoveOutcome.VerifyFailed,
                    "Verification of the copied folder failed.");
            }

            // 3) DELETE SOURCE
            progress?.Report(new FolderMoveProgress(FolderMovePhase.CleaningUp, 100));
            TryDelete(source); // delete-source failure is non-fatal: dest is authoritative
            return new DirectoryMoveResult(DirectoryMoveOutcome.Success);
        }
        catch (OperationCanceledException)
        {
            if (!destExistedBefore) TryDelete(destination);
            return new DirectoryMoveResult(DirectoryMoveOutcome.CopyFailed, "Cancelled.");
        }
        catch (Exception ex)
        {
            if (!destExistedBefore) TryDelete(destination);
            return new DirectoryMoveResult(DirectoryMoveOutcome.CopyFailed, ex.Message);
        }
    }

    private static bool Verify(string source, string destination)
    {
        var srcFiles = Directory.GetFiles(source, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(source, f), f => new FileInfo(f).Length,
                          StringComparer.OrdinalIgnoreCase);
        var dstFiles = Directory.GetFiles(destination, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(destination, f), f => f,
                          StringComparer.OrdinalIgnoreCase);

        if (srcFiles.Count != dstFiles.Count) return false;
        foreach (var (rel, size) in srcFiles)
        {
            if (!dstFiles.TryGetValue(rel, out var dstPath)) return false;
            if (new FileInfo(dstPath).Length != size) return false;
            // Hash the Vault subtree (memory integrity); size-check suffices elsewhere.
            if (rel.Replace('\\', '/').StartsWith("Vault/", StringComparison.OrdinalIgnoreCase))
            {
                var srcPath = Path.Combine(source, rel);
                if (!HashEquals(srcPath, dstPath)) return false;
            }
        }
        return true;
    }

    private static bool HashEquals(string a, string b)
    {
        using var fa = File.OpenRead(a);
        var ha = SHA256.HashData(fa);
        using var fb = File.OpenRead(b);
        var hb = SHA256.HashData(fb);
        return ha.AsSpan().SequenceEqual(hb);
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* non-fatal */ }
    }
}
