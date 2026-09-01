using System.IO;

namespace Pia.Infrastructure;

/// <summary>
/// Binary counterpart to <see cref="AtomicTextWriter"/> for the docx/xlsx patch engines, which need
/// to build a candidate package into a temp file, validate it, and only then commit — unlike the
/// text writer's single-call "write these bytes" API. Same atomicity guarantee (temp file in the
/// same directory, atomic <see cref="File.Replace(string, string, string?)"/>/<see cref="File.Move(string, string, bool)"/>),
/// no text-specific newline/BOM handling.
/// </summary>
public static class AtomicBinaryWriter
{
    /// <summary>A temp path in <paramref name="targetPath"/>'s directory, same naming convention as
    /// <see cref="AtomicTextWriter"/>'s (a leading dot, the target's file name, a GUID, ".tmp").</summary>
    public static string CreateTempPath(string targetPath)
    {
        var dir = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrEmpty(dir)) dir = Directory.GetCurrentDirectory();
        return Path.Combine(dir, "." + Path.GetFileName(targetPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
    }

    /// <summary>Atomically replaces <paramref name="targetPath"/> with the already-built
    /// <paramref name="tempPath"/> — <see cref="File.Replace(string, string, string?)"/> (preserves
    /// ACLs) when the target exists, else a move. Does not touch <paramref name="tempPath"/> on
    /// failure; the caller's error path is expected to call <see cref="DiscardTempFile"/>.</summary>
    public static void CommitTempFile(string tempPath, string targetPath)
    {
        if (File.Exists(targetPath))
            File.Replace(tempPath, targetPath, destinationBackupFileName: null);
        else
            File.Move(tempPath, targetPath, overwrite: true);
    }

    /// <summary>Best-effort cleanup for a temp file that failed validation or whose write threw.</summary>
    public static void DiscardTempFile(string tempPath)
    {
        try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
    }
}
