using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Pia.Native;

internal static partial class FinalPathProbe
{
    private const uint FILE_SHARE_ALL = 0x7;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_NAME_NORMALIZED_VOLUME_NAME_DOS = 0;
    private const int MaxAttempts = 3;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandle(SafeFileHandle file, char* filePath, uint filePathLength, uint flags);

    /// <summary>The path with every symlink and junction resolved; null when it does not exist.</summary>
    public static string? Resolve(string path)
    {
        using var handle = CreateFile(path, 0, FILE_SHARE_ALL, 0, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, 0);
        if (handle.IsInvalid)
            return null;

        var buffer = new char[260];
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var length = Query(handle, buffer);
            if (length == 0)
                return null;
            if (length < buffer.Length)
                return StripExtendedPrefix(new string(buffer, 0, (int)length));

            // Too small: the return value is the size needed, terminator included.
            buffer = new char[length];
        }

        return null;
    }

    internal static string StripExtendedPrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + path[8..];

        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    private static unsafe uint Query(SafeFileHandle handle, char[] buffer)
    {
        fixed (char* destination = buffer)
        {
            return GetFinalPathNameByHandle(handle, destination, (uint)buffer.Length, FILE_NAME_NORMALIZED_VOLUME_NAME_DOS);
        }
    }
}
