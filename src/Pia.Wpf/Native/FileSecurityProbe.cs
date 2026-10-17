using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Pia.Native;

internal static partial class FileSecurityProbe
{
    private const uint WRITE_DAC = 0x00040000;
    private const uint FILE_SHARE_ALL = 0x7;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    /// <summary>True when this process may rewrite the file's or folder's ACL, which is what a sandbox must do to grant it.</summary>
    public static bool CanWriteDacl(string path)
    {
        using var handle = CreateFile(path, WRITE_DAC, FILE_SHARE_ALL, 0, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, 0);
        return !handle.IsInvalid;
    }
}
