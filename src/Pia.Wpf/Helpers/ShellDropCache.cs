using System.IO;
using Pia.Paths;

namespace Pia.Helpers;

/// <summary>
/// The scratch area a virtual-file drop writes into. The files are read once, during the drop, and are dead by
/// the time the staging call returns — nothing downstream re-opens the path, because the chip carries the
/// extracted text. So they are deleted at startup, and each drop first sweeps the ones left by earlier drops.
/// </summary>
public static class ShellDropCache
{
    /// <summary>A drop's own files are consumed within milliseconds; the grace period only protects a second
    /// drop that lands while the first is still reading.</summary>
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(2);

    /// <summary>A fresh directory per drop, so two drags of the same mail cannot overwrite each other.</summary>
    public static string CreateDropDirectory()
    {
        SweepStale();
        var directory = Path.Combine(PiaPaths.DropCacheDirectory, Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>Called at startup: nothing in here survives a run.</summary>
    public static void Clear() => Delete(PiaPaths.DropCacheDirectory);

    public static void Delete(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void SweepStale()
    {
        try
        {
            if (!Directory.Exists(PiaPaths.DropCacheDirectory)) return;

            var cutoff = DateTime.UtcNow - Grace;
            foreach (var directory in Directory.EnumerateDirectories(PiaPaths.DropCacheDirectory))
            {
                if (Directory.GetLastWriteTimeUtc(directory) < cutoff) Delete(directory);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
