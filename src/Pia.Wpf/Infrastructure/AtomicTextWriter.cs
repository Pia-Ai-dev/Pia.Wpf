using System.IO;
using System.Text;

namespace Pia.Infrastructure;

/// <summary>
/// Crash-safe text write that preserves an existing file's dominant end-of-line style and a leading
/// byte-order mark. Writes to a temp file in the SAME directory, flushes to disk, then atomically
/// replaces the target via <see cref="File.Replace"/> (preserving ACLs) — falling back to an
/// overwrite move when there is no existing target. The temp file is removed on any error so a
/// failed write can never leave a partial target.
/// </summary>
public static class AtomicTextWriter
{
    /// <summary>Result of a successful write: bytes actually written and the EOL/BOM choices applied.</summary>
    public readonly record struct WriteResult(long BytesWritten, bool UsedCrlf, bool HadBom);

    /// <summary>
    /// Normalizes <paramref name="content"/>'s newlines to the target file's detected EOL (CRLF for a
    /// new file — repo convention), re-prepends a BOM iff the existing file had one, and writes
    /// atomically to <paramref name="targetPath"/>. Assumes the parent directory already exists.
    /// </summary>
    public static WriteResult Write(string targetPath, string content)
    {
        bool exists = File.Exists(targetPath);
        bool useCrlf = true;   // new file → CRLF
        bool hadBom = false;   // new file → no BOM

        if (exists)
        {
            // Sample the CURRENT bytes (not a stale prepare-time snapshot) to decide EOL + BOM.
            var existing = File.ReadAllBytes(targetPath);
            hadBom = HasUtf8Bom(existing);
            useCrlf = DetectDominantCrlf(existing);
        }

        var normalized = NormalizeNewlines(content, useCrlf);

        // UTF-8; emit a BOM only to match the original. UTF8Encoding(false) never emits a BOM,
        // so we prepend the 3-byte preamble manually when required.
        var body = Encoding.UTF8.GetBytes(normalized);
        byte[] bytes;
        if (hadBom)
        {
            bytes = new byte[3 + body.Length];
            bytes[0] = 0xEF; bytes[1] = 0xBB; bytes[2] = 0xBF;
            Array.Copy(body, 0, bytes, 3, body.Length);
        }
        else
        {
            bytes = body;
        }

        var dir = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrEmpty(dir)) dir = Directory.GetCurrentDirectory();
        var temp = Path.Combine(dir, "." + Path.GetFileName(targetPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");

        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }

            if (exists)
            {
                // Atomic replace preserves the original's ACLs/attributes. No backup file.
                File.Replace(temp, targetPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temp, targetPath, overwrite: true);
            }
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
            throw;
        }

        return new WriteResult(bytes.Length, useCrlf, hadBom);
    }

    private static bool HasUtf8Bom(byte[] bytes)
        => bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

    /// <summary>
    /// True when CRLF is the dominant line ending. Counts CRLF pairs vs. bare LFs; ties (or no
    /// newlines at all) default to CRLF, matching the repo convention for new content.
    /// </summary>
    private static bool DetectDominantCrlf(byte[] bytes)
    {
        int crlf = 0, lf = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\n')
            {
                if (i > 0 && bytes[i - 1] == (byte)'\r') crlf++;
                else lf++;
            }
        }
        if (crlf == 0 && lf == 0) return true; // no newlines: keep repo convention
        return crlf >= lf;
    }

    /// <summary>Collapses all CR/CRLF/LF to a single form then re-expands to the chosen EOL.</summary>
    private static string NormalizeNewlines(string content, bool crlf)
    {
        // First normalize every variant to LF, then map LF → target.
        var lf = content.Replace("\r\n", "\n").Replace("\r", "\n");
        return crlf ? lf.Replace("\n", "\r\n") : lf;
    }
}
