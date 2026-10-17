using System.Runtime.InteropServices;

namespace Pia.Native;

internal static partial class AppContainerNative
{
    [LibraryImport("userenv.dll", EntryPoint = "DeleteAppContainerProfile", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DeleteAppContainerProfile(string appContainerName);

    /// <summary>The HRESULT; a failed call is data, so this never throws for a bad name.</summary>
    public static int DeleteProfile(string appContainerName) => DeleteAppContainerProfile(appContainerName);
}
