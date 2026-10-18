using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;

namespace Pia.Native;

internal static partial class TokenAccessProbe
{
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenLinkedToken = 19;
    private const uint WRITE_DAC = 0x00040000;
    private const int PrivilegeSetBytes = 256;

    private static readonly GenericMapping FileMapping = new(0x120089, 0x120116, 0x1200A0, 0x1F01FF);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct GenericMapping(uint Read, uint Write, uint Execute, uint All);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint desiredAccess, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(
        SafeAccessTokenHandle token,
        int informationClass,
        out nint information,
        int informationLength,
        out int returnLength);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AccessCheck(
        byte[] securityDescriptor,
        SafeAccessTokenHandle clientToken,
        uint desiredAccess,
        in GenericMapping genericMapping,
        byte[] privilegeSet,
        ref int privilegeSetLength,
        out uint grantedAccess,
        out int accessStatus);

    /// <summary>Whether the medium-IL token linked to this elevated one may rewrite the DACL; null when there is no linked token (UAC off).</summary>
    public static bool? CanLinkedTokenWriteDacl(string path)
    {
        using var linked = LinkedToken();
        if (linked is null)
            return null;

        byte[] descriptor;
        try
        {
            descriptor = SecurityDescriptor(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException
                                       or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return false;
        }

        var privileges = new byte[PrivilegeSetBytes];
        var privilegesLength = privileges.Length;
        return AccessCheck(descriptor, linked, WRITE_DAC, in FileMapping, privileges, ref privilegesLength, out _, out var granted)
               && granted != 0;
    }

    private static SafeAccessTokenHandle? LinkedToken()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out var raw))
            return null;

        using var process = new SafeAccessTokenHandle(raw);
        if (!GetTokenInformation(process, TokenLinkedToken, out var linked, IntPtr.Size, out _) || linked == 0)
            return null;

        return new SafeAccessTokenHandle(linked);
    }

    // AccessCheck rejects a descriptor without owner and group, and the owner carries implicit WRITE_DAC.
    private static byte[] SecurityDescriptor(string path)
    {
        const AccessControlSections sections = AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access;
        FileSystemSecurity security = Directory.Exists(path)
            ? new DirectoryInfo(path).GetAccessControl(sections)
            : new FileInfo(path).GetAccessControl(sections);
        return security.GetSecurityDescriptorBinaryForm();
    }
}
