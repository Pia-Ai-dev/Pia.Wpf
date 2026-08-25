using System.Globalization;
using System.IO;

namespace Pia.Logging;

/// <summary>
/// Deletes log files outside a window of DAYS, taking its directory as a parameter so a test never points it
/// at the real profile. Age comes from the date in the NAME: the export copies files, so mtime lies.
/// </summary>
public static class LogFileRetention
{
    public const int DefaultRetainedDays = 30;

    private const string SearchPattern = "pia*.log";
    private const string NamePrefix = "pia-";
    private const int StampLength = 10;

    /// <summary>The sink stamps names from the LOCAL clock, so the window has to be measured on it too.</summary>
    public static LogFileRetentionOutcome Sweep(string logDirectory, int retainedDays) =>
        Sweep(logDirectory, retainedDays, DateOnly.FromDateTime(DateTime.Now));

    public static LogFileRetentionOutcome Sweep(string logDirectory, int retainedDays, DateOnly today)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(retainedDays);

        var cutoff = today.AddDays(-(retainedDays - 1));

        if (string.IsNullOrWhiteSpace(logDirectory) || !Directory.Exists(logDirectory))
            return new LogFileRetentionOutcome(0, 0, 0, cutoff);

        string[] paths;
        try
        {
            paths = Directory.GetFiles(logDirectory, SearchPattern);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new LogFileRetentionOutcome(0, 0, 0, cutoff);
        }

        int kept = 0, deleted = 0, skipped = 0;
        foreach (var path in paths)
        {
            var date = DateOf(Path.GetFileNameWithoutExtension(path));
            // A name dated after today came from a skewed clock: left alone it never ages out, and it
            // outranks the live slice in an export.
            if (date is null || (date.Value >= cutoff && date.Value <= today))
            {
                kept++;
                continue;
            }

            try
            {
                File.Delete(path);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++;
            }
        }

        return new LogFileRetentionOutcome(kept, deleted, skipped, cutoff);
    }

    /// <summary>The date a file name claims, accepting a roll suffix after it. Null means the name is not ours.</summary>
    public static DateOnly? DateOf(string nameWithoutExtension) => SliceOf(nameWithoutExtension)?.Date;

    /// <summary>The day and roll a file name claims. Null means the name is not ours.</summary>
    public static (DateOnly Date, int Roll)? SliceOf(string nameWithoutExtension)
    {
        if (nameWithoutExtension is null
            || nameWithoutExtension.Length < NamePrefix.Length + StampLength
            || !nameWithoutExtension.StartsWith(NamePrefix, StringComparison.Ordinal))
            return null;

        var stamp = nameWithoutExtension.AsSpan(NamePrefix.Length);
        if (!DateOnly.TryParseExact(
                stamp[..StampLength], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None,
                out var date))
            return null;

        var suffix = stamp[StampLength..];
        if (suffix.Length == 0)
            return (date, 0);

        // The sink appends the roll index with NO separator, so pia-2026-08-24.log rolls to
        // pia-2026-08-241.log; the optional separator only covers a name written by hand or by an older sink.
        if (suffix[0] is '-' or '_' or '.')
            suffix = suffix[1..];

        if (suffix.Length == 0)
            return null;

        foreach (var c in suffix)
        {
            if (!char.IsAsciiDigit(c))
                return null;
        }

        // A digit run too long for an int is still ours; only the ordering hint is lost.
        return int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var roll)
            ? (date, roll)
            : (date, 0);
    }
}

/// <summary>Kept counts in-window files AND names that did not parse; Skipped means only "could not delete".</summary>
public sealed record LogFileRetentionOutcome(int Kept, int Deleted, int Skipped, DateOnly Cutoff);
