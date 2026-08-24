using System.Globalization;
using System.IO;
using Pia.Logging;
using Xunit;

namespace Pia.Tests.Logging;

/// <summary>
/// Temp directories only. The sweep resolves no path of its own, so nothing here needs
/// RedirectedProfileFixture or the PiaPathsStatic collection, and nothing here can reach the real profile.
/// </summary>
public sealed class LogFileRetentionTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 8, 24);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pia-retention-{Guid.NewGuid():N}");

    public LogFileRetentionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp directory is not worth failing a test over.
        }
    }

    private void Write(string name) => File.WriteAllText(Path.Combine(_root, name), "line\n");

    private static string Log(DateOnly date, string suffix = "") => $"pia-{date:yyyy-MM-dd}{suffix}.log";

    private string[] Surviving() =>
        [.. Directory.GetFiles(_root).Select(p => Path.GetFileName(p)!).OrderBy(n => n, StringComparer.Ordinal)];

    [Fact]
    public void OldFilesGo_AndTheNewestDaysStay()
    {
        Write(Log(Today));
        Write(Log(Today.AddDays(-1)));
        Write(Log(Today.AddDays(-5)));
        Write(Log(Today.AddDays(-29)));
        Write(Log(Today.AddDays(-30)));
        Write(Log(Today.AddDays(-45)));

        var outcome = LogFileRetention.Sweep(_root, 30, Today);

        Assert.Equal(
            [Log(Today.AddDays(-29)), Log(Today.AddDays(-5)), Log(Today.AddDays(-1)), Log(Today)],
            Surviving());
        Assert.Equal(4, outcome.Kept);
        Assert.Equal(2, outcome.Deleted);
        Assert.Equal(0, outcome.Skipped);
        Assert.Equal(Today.AddDays(-29), outcome.Cutoff);
    }

    [Fact]
    public void TheBoundaryDay_IsDeleted_BecauseTodayCountsAsDayOne()
    {
        Write(Log(Today.AddDays(-6)));
        Write(Log(Today.AddDays(-7)));

        var outcome = LogFileRetention.Sweep(_root, 7, Today);

        Assert.Equal([Log(Today.AddDays(-6))], Surviving());
        Assert.Equal(Today.AddDays(-6), outcome.Cutoff);
        Assert.Equal(1, outcome.Deleted);
    }

    /// <summary>A name dated after today came from a skewed clock, and nothing else would ever age it out.</summary>
    [Fact]
    public void AFutureDatedFileIsOutOfTheWindowToo()
    {
        Write(Log(Today.AddDays(1)));
        Write(Log(Today.AddDays(400)));
        Write(Log(Today));

        var outcome = LogFileRetention.Sweep(_root, 30, Today);

        Assert.Equal([Log(Today)], Surviving());
        Assert.Equal(2, outcome.Deleted);
        Assert.Equal(1, outcome.Kept);
    }

    [Fact]
    public void AnUnparseableNameIsKept()
    {
        Write("pia.log");
        Write("pia-notadate.log");
        Write("pia-2026-13-45.log");
        Write("pia-2026-06-28-copy.log");
        Write("pia-2026-06-28x.log");
        Write("pia-2026-06-2.log");

        var outcome = LogFileRetention.Sweep(_root, 30, Today);

        Assert.Equal(
            [
                "pia-2026-06-2.log", "pia-2026-06-28-copy.log", "pia-2026-06-28x.log",
                "pia-2026-13-45.log", "pia-notadate.log", "pia.log",
            ],
            Surviving());
        Assert.Equal(0, outcome.Deleted);
        Assert.Equal(6, outcome.Kept);
    }

    [Fact]
    public void ARollSuffixIsTreatedAsThatDay()
    {
        var old = Today.AddDays(-60);
        Write(Log(old, "1"));
        Write(Log(old, "-001"));
        Write(Log(old, "_2"));
        Write(Log(Today.AddDays(-1), "1"));

        var outcome = LogFileRetention.Sweep(_root, 30, Today);

        Assert.Equal([Log(Today.AddDays(-1), "1")], Surviving());
        Assert.Equal(3, outcome.Deleted);
        Assert.Equal(1, outcome.Kept);
    }

    [Fact]
    public void AFileItDoesNotOwnSurvives()
    {
        Write("providers.json");
        Write("settings.json");
        Write("history.db");
        Write("Logs.zip");
        Write("winwright.log");
        Write("pia-2026-06-28.logbak");
        Write(Log(Today.AddDays(-60)));

        var outcome = LogFileRetention.Sweep(_root, 30, Today);

        Assert.Equal(
            [
                "Logs.zip", "history.db", "pia-2026-06-28.logbak", "providers.json", "settings.json",
                "winwright.log",
            ],
            Surviving());
        Assert.Equal(1, outcome.Deleted);
        Assert.Equal(0, outcome.Kept);
    }

    [Fact]
    public void ALockedFileIsSkippedNotThrown()
    {
        var locked = Log(Today.AddDays(-60));
        var other = Log(Today.AddDays(-61));
        Write(locked);
        Write(other);

        LogFileRetentionOutcome outcome;
        using (File.Open(Path.Combine(_root, locked), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            outcome = LogFileRetention.Sweep(_root, 30, Today);
        }

        Assert.Equal([locked], Surviving());
        Assert.Equal(1, outcome.Skipped);
        Assert.Equal(1, outcome.Deleted);
        Assert.Equal(0, outcome.Kept);
    }

    [Fact]
    public void AMissingDirectoryIsANoOp()
    {
        var missing = Path.Combine(_root, "nope");

        var outcome = LogFileRetention.Sweep(missing, 30, Today);

        Assert.Equal(0, outcome.Kept);
        Assert.Equal(0, outcome.Deleted);
        Assert.Equal(0, outcome.Skipped);
        Assert.Equal(Today.AddDays(-29), outcome.Cutoff);
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void AnEmptyDirectoryIsANoOp()
    {
        var outcome = LogFileRetention.Sweep(_root, 30, Today);

        Assert.Equal(0, outcome.Kept);
        Assert.Equal(0, outcome.Deleted);
        Assert.Equal(0, outcome.Skipped);
    }

    [Fact]
    public void TodaysFileSurvivesTheTightestWindow()
    {
        var name = $"pia-{DateTime.Now:yyyy-MM-dd}.log";
        Write(name);

        var outcome = LogFileRetention.Sweep(_root, 1);

        Assert.Equal([name], Surviving());
        Assert.Equal(1, outcome.Kept);
        Assert.Equal(0, outcome.Deleted);
        // Not just "the file survived": at a one-day window only a LOCAL-clock cutoff lands on today.
        Assert.Equal(DateOnly.FromDateTime(DateTime.Now), outcome.Cutoff);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ANonPositiveWindowIsRejected(int retainedDays) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => LogFileRetention.Sweep(_root, retainedDays, Today));

    [Theory]
    [InlineData("pia-2026-08-24", 0)]
    [InlineData("pia-2026-08-241", 1)]
    [InlineData("pia-2026-08-24-001", 1)]
    [InlineData("pia-2026-08-24_12", 12)]
    // A digit run too long for an int is still ours; it only loses the ordering hint.
    [InlineData("pia-2026-08-2499999999999999", 0)]
    public void SliceOf_ReadsTheRollIndex(string nameWithoutExtension, int expectedRoll)
    {
        var slice = LogFileRetention.SliceOf(nameWithoutExtension);

        Assert.NotNull(slice);
        Assert.Equal(new DateOnly(2026, 8, 24), slice.Value.Date);
        Assert.Equal(expectedRoll, slice.Value.Roll);
    }

    [Theory]
    [InlineData("pia-2026-08-24", "2026-08-24")]
    [InlineData("pia-2026-08-241", "2026-08-24")]
    [InlineData("pia-2026-08-24-001", "2026-08-24")]
    [InlineData("pia-2026-08-24_2", "2026-08-24")]
    [InlineData("pia-2026-08-24.3", "2026-08-24")]
    [InlineData("pia", null)]
    [InlineData("pia.log", null)]
    [InlineData("pia-2026-99-99", null)]
    [InlineData("pia-2026-08-24-", null)]
    [InlineData("pia-2026-08-24-old", null)]
    [InlineData("notpia-2026-08-24", null)]
    public void DateOf_ParsesOnlyOurNames(string nameWithoutExtension, string? expected)
    {
        var actual = LogFileRetention.DateOf(nameWithoutExtension);

        var wanted = expected is null
            ? (DateOnly?)null
            : DateOnly.ParseExact(expected, "yyyy-MM-dd", CultureInfo.InvariantCulture);

        Assert.Equal(wanted, actual);
    }
}
