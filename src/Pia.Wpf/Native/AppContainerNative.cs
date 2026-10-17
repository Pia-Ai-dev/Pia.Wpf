using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Pia.Native;

internal static partial class AppContainerNative
{
    [LibraryImport("userenv.dll", EntryPoint = "DeleteAppContainerProfile", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DeleteAppContainerProfile(string appContainerName);

    [LibraryImport("userenv.dll", EntryPoint = "DeriveAppContainerSidFromAppContainerName", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DeriveAppContainerSidFromAppContainerName(string appContainerName, out nint sid);

    [LibraryImport("advapi32.dll")]
    private static partial nint FreeSid(nint sid);

    /// <summary>The HRESULT; a failed call is data, so this never throws for a bad name.</summary>
    public static int DeleteProfile(string appContainerName) => DeleteAppContainerProfile(appContainerName);

    /// <summary>The SID a container of this name has, whether or not its profile exists; null when it cannot be derived.</summary>
    public static string? DeriveSid(string appContainerName)
    {
        if (DeriveAppContainerSidFromAppContainerName(appContainerName, out var sid) != 0 || sid == 0)
            return null;

        try
        {
            return new SecurityIdentifier(sid).Value;
        }
        finally
        {
            FreeSid(sid);
        }
    }
}
