using System.IO;

namespace Pia.Services;

/// <summary>Deletes dictation WAVs a crash or an abandoned recording left behind.</summary>
public static class RecordingFileStore
{
    // Old enough that no live dictation or transcription in a second instance can still own the file.
    public static readonly TimeSpan MinimumAge = TimeSpan.FromHours(1);

    private const string FilePrefix = "pia_recording_";
    private const string SearchPattern = FilePrefix + "*.wav";

    public static string NewRecordingPath(string directory)
    {
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{FilePrefix}{Guid.NewGuid()}.wav");
    }

    public static int Sweep(IEnumerable<string> directories) => Sweep(directories, DateTime.UtcNow);

    public static int Sweep(IEnumerable<string> directories, DateTime nowUtc)
    {
        var deleted = 0;
        foreach (var directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                continue;

            string[] paths;
            try
            {
                paths = Directory.GetFiles(directory, SearchPattern);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var path in paths)
            {
                try
                {
                    if (nowUtc - File.GetLastWriteTimeUtc(path) < MinimumAge)
                        continue;

                    File.Delete(path);
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Still open elsewhere; the next launch retries.
                }
            }
        }

        return deleted;
    }

    public static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
