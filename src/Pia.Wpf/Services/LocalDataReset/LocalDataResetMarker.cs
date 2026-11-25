using System.IO;
using Pia.Paths;

namespace Pia.Services.LocalDataReset;

internal static class LocalDataResetMarker
{
    public static bool IsPending => File.Exists(PiaPaths.LocalDataResetMarkerPath);

    public static void Write()
    {
        var path = PiaPaths.LocalDataResetMarkerPath;
        Directory.CreateDirectory(PiaPaths.RoamingDataDirectory);

        // Temp-then-move so a crash never leaves a half-written marker.
        var temp = path + ".tmp";
        File.WriteAllText(temp, DateTime.UtcNow.ToString("O"));
        File.Move(temp, path, overwrite: true);
    }

    public static void Clear()
    {
        var path = PiaPaths.LocalDataResetMarkerPath;
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
