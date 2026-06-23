using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Pia.Infrastructure;

/// <summary>
/// Sandboxes file access to a configured root directory. Every user-supplied
/// relative path must be resolved through <see cref="TryResolveInside"/> before
/// being passed to the filesystem.
/// </summary>
public static class SafeFolderPath
{
    /// <summary>
    /// Resolves <paramref name="userPath"/> against <paramref name="sandboxRoot"/>
    /// and verifies the result stays inside the root.
    /// Rejects: rooted paths (C:\..., \\server\...), invalid path characters,
    /// and any combination that escapes the root via "..\" traversal.
    /// </summary>
    public static bool TryResolveInside(string? sandboxRoot, string? userPath, out string resolved)
    {
        resolved = string.Empty;
        if (string.IsNullOrWhiteSpace(sandboxRoot)) return false;
        if (string.IsNullOrWhiteSpace(userPath)) return false;

        var trimmed = userPath.Trim();

        // Block absolute paths, UNC paths, drive-relative paths.
        if (Path.IsPathRooted(trimmed)) return false;
        if (trimmed.Contains('\0')) return false;
        if (trimmed.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;

        return TryContain(sandboxRoot, trimmed, out resolved);
    }

    /// <summary>
    /// Resolves <paramref name="userPath"/> against <paramref name="sandboxRoot"/>, accepting
    /// BOTH relative and rooted/absolute inputs, and verifies the result stays inside the root.
    /// Unlike <see cref="TryResolveInside"/> this does NOT reject rooted paths; instead the input
    /// is normalized and then <b>canonicalized via <see cref="Canonicalize"/></b> (resolving
    /// junctions/symlinks through the OS, including intermediate reparse points) so links that
    /// point outside the sandbox are caught.
    /// <para>
    /// Non-existent paths are supported (write_file creates new files; read_file may miss):
    /// canonicalization requires an existing handle, so the longest existing ancestor is
    /// canonicalized and the non-existent leaf re-appended before the lexical containment check.
    /// </para>
    /// </summary>
    public static bool TryResolveInsideAllowingAbsolute(string? sandboxRoot, string? userPath, out string resolved)
    {
        resolved = string.Empty;
        if (string.IsNullOrWhiteSpace(sandboxRoot)) return false;
        if (string.IsNullOrWhiteSpace(userPath)) return false;

        var trimmed = userPath.Trim();
        if (trimmed.Contains('\0')) return false;
        if (trimmed.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;

        // Canonicalize the root the SAME way as the candidate (below) so the StartsWith
        // containment check is symmetric — a junction/casing difference in the root path
        // itself must not produce false rejects of legitimately-inside paths.
        string fullRoot;
        try
        {
            fullRoot = Path.GetFullPath(sandboxRoot);
            if (Directory.Exists(fullRoot)) fullRoot = Canonicalize(fullRoot);
        }
        catch { return false; }

        // Normalize the candidate (collapses "..", makes absolute) against the root.
        // The base-path overload handles relative-vs-absolute in one call: an already
        // absolute "trimmed" is returned as-is; a relative one is combined with fullRoot.
        string candidate;
        try { candidate = Path.GetFullPath(trimmed, fullRoot); }
        catch { return false; }

        // Canonicalize to resolve junctions/symlinks. Canonicalize requires an existing
        // handle, so for a path that does not exist canonicalize the longest existing
        // ancestor and re-append the (lexical) remainder.
        string canonical;
        try
        {
            if (File.Exists(candidate) || Directory.Exists(candidate))
            {
                canonical = Canonicalize(candidate);
            }
            else
            {
                var ancestor = Path.GetDirectoryName(candidate);
                while (!string.IsNullOrEmpty(ancestor) && !Directory.Exists(ancestor))
                    ancestor = Path.GetDirectoryName(ancestor);

                if (string.IsNullOrEmpty(ancestor))
                    return false; // no existing ancestor to anchor against

                var realAncestor = Canonicalize(ancestor);
                // candidate was already collapsed by GetFullPath, so the relative
                // remainder contains no ".." segments — purely lexical recombine.
                var remainder = Path.GetRelativePath(ancestor, candidate);
                canonical = remainder == "."
                    ? realAncestor
                    : Path.GetFullPath(Path.Combine(realAncestor, remainder));
            }
        }
        catch { return false; }

        return TryContain(fullRoot, canonical, out resolved);
    }

    /// <summary>
    /// Lexical containment guard shared by both resolvers: <paramref name="candidate"/> is combined
    /// with the full sandbox root and the result must stay inside the root (trailing-separator-aware,
    /// rejecting the root itself). <paramref name="candidate"/> may be relative or absolute —
    /// <see cref="Path.Combine"/> returns an absolute candidate as-is and <see cref="Path.GetFullPath(string)"/>
    /// is idempotent on it.
    /// </summary>
    private static bool TryContain(string sandboxRoot, string candidate, out string resolved)
    {
        resolved = string.Empty;

        string fullRoot;
        try { fullRoot = Path.GetFullPath(sandboxRoot); }
        catch { return false; }

        if (!fullRoot.EndsWith(Path.DirectorySeparatorChar))
            fullRoot += Path.DirectorySeparatorChar;

        string full;
        try { full = Path.GetFullPath(Path.Combine(fullRoot, candidate)); }
        catch { return false; }

        // Comparison must include the trailing separator so "/rootEvil" is not
        // accepted as being inside "/root".
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) return false;
        if (full.Length == fullRoot.Length - 1) return false; // resolved to root itself

        resolved = full;
        return true;
    }

    /// <summary>
    /// Canonicalizes an <b>existing</b> file or directory path, resolving every reparse point
    /// (junctions/symlinks) the OS would, including intermediate ones — equivalent to a real-path
    /// resolution. Implemented via <c>GetFinalPathNameByHandle</c> (.NET has no managed equivalent
    /// on this target). Throws if the path does not exist or cannot be opened.
    /// </summary>
    public static string Canonicalize(string existingPath)
    {
        // FILE_FLAG_BACKUP_SEMANTICS is required to open a directory handle.
        using var handle = CreateFileW(
            existingPath,
            dwDesiredAccess: 0, // no read/write — metadata only
            dwShareMode: FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            lpSecurityAttributes: IntPtr.Zero,
            dwCreationDisposition: OPEN_EXISTING,
            dwFlagsAndAttributes: FILE_FLAG_BACKUP_SEMANTICS,
            hTemplateFile: IntPtr.Zero);

        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        var buffer = new StringBuilder(512);
        var len = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (len == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (len > buffer.Capacity)
        {
            buffer = new StringBuilder((int)len);
            len = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (len == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var result = buffer.ToString();
        // Strip the \\?\ (or \\?\UNC\) extended-length prefix the API prepends.
        if (result.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
            result = @"\\" + result.Substring(8);
        else if (result.StartsWith(@"\\?\", StringComparison.Ordinal))
            result = result.Substring(4);

        return result;
    }

    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint FILE_SHARE_DELETE = 0x4;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle hFile,
        StringBuilder lpszFilePath,
        uint cchFilePath,
        uint dwFlags);

    /// <summary>
    /// Returns true if <paramref name="sandboxRoot"/> is a usable directory.
    /// </summary>
    public static bool IsConfiguredAndExists(string? sandboxRoot)
    {
        if (string.IsNullOrWhiteSpace(sandboxRoot)) return false;
        try { return Directory.Exists(Path.GetFullPath(sandboxRoot)); }
        catch { return false; }
    }
}
