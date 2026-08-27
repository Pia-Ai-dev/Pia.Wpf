using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services;
using Pia.Services.Interfaces;
using Pia.Services.Scheduling;
using Pia.Tests.TestInfrastructure;
using Xunit;

namespace Pia.Tests.Infrastructure;

/// <summary>
/// The additive half of the ScheduledJobs pin and blueprint-key migration. Every test and every fresh profile
/// takes the <c>CREATE TABLE</c> path, so nothing else in the suite would notice a missing <c>ALTER TABLE</c> —
/// it would throw only on a real user's existing database.
/// </summary>
public sealed class ScheduledJobsPinMigrationTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string _dbPath;

    public ScheduledJobsPinMigrationTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "PiaPinMigration_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        _dbPath = Path.Combine(_tmpDir, "history.db");
    }

    public void Dispose()
    {
        SqlitePool.ClearFor($"Data Source={_dbPath}");
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task MigrateSchema_AddsThePinAndBlueprintKeyColumns_PreservingTheExistingRow()
    {
        var jobId = Guid.NewGuid();
        var now = DateTime.Now.ToString("O");

        // The pre-pin ScheduledJobs shape: everything up to and including QuietOnSuccess, and neither the two
        // pin columns nor BlueprintKey.
        using (var seed = new SqliteConnection($"Data Source={_dbPath}"))
        {
            seed.Open();
            using (var create = seed.CreateCommand())
            {
                create.CommandText = """
                    CREATE TABLE ScheduledJobs (
                        Id TEXT PRIMARY KEY,
                        Name TEXT NOT NULL,
                        Query TEXT NOT NULL,
                        Kind TEXT NOT NULL DEFAULT 'Research',
                        AnswerLength TEXT NOT NULL DEFAULT 'Balanced',
                        ProviderId TEXT NULL,
                        Recurrence TEXT NOT NULL,
                        TimeOfDay TEXT NOT NULL,
                        DayOfWeek INTEGER NULL,
                        DayOfMonth INTEGER NULL,
                        Month INTEGER NULL,
                        SpecificDate TEXT NULL,
                        NextFireAt TEXT NOT NULL,
                        Status TEXT NOT NULL DEFAULT 'Active',
                        CreatedAt TEXT NOT NULL,
                        UpdatedAt TEXT NOT NULL DEFAULT '',
                        LastFiredAt TEXT NULL,
                        LastResultEntryId TEXT NULL,
                        ConsecutiveFailures INTEGER NOT NULL DEFAULT 0,
                        OwnerDeviceId TEXT NULL,
                        GrantedTools TEXT NOT NULL DEFAULT '[]',
                        QuietOnSuccess INTEGER NOT NULL DEFAULT 0
                    );
                    """;
                create.ExecuteNonQuery();
            }
            using (var insert = seed.CreateCommand())
            {
                insert.CommandText = """
                    INSERT INTO ScheduledJobs
                    (Id, Name, Query, Kind, Recurrence, TimeOfDay, NextFireAt, Status, CreatedAt, UpdatedAt)
                    VALUES (@Id, 'pre-migration', 'what changed', 'Research', 'Daily', '09:00',
                            @Now, 'Active', @Now, @Now)
                    """;
                insert.Parameters.AddWithValue("@Id", jobId.ToString());
                insert.Parameters.AddWithValue("@Now", now);
                insert.ExecuteNonQuery();
            }
        }
        SqlitePool.ClearFor($"Data Source={_dbPath}");

        // GetConnection() runs EnsureSchema()/MigrateSchema(); CREATE TABLE IF NOT EXISTS is a no-op here, so
        // only the ALTER path can produce the columns.
        using var ctx = new SqliteContext(_dbPath);
        var columns = new List<string>();
        using (var pragma = ctx.GetConnection().CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(ScheduledJobs)";
            using var reader = pragma.ExecuteReader();
            while (reader.Read())
                columns.Add(reader.GetString(1));
        }

        Assert.Contains("PersonaId", columns);
        Assert.Contains("ReasoningEffort", columns);
        Assert.Contains("BlueprintKey", columns);

        // The positional read is what a real user's next launch actually does, so exercise it rather than
        // trusting the PRAGMA alone.
        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());
        var jobs = new ScheduledJobService(ctx, new RecurrenceCalculator(), settings,
            new SyncDeleteTrackerService(_tmpDir, NullLogger<SyncDeleteTrackerService>.Instance),
            NullLogger<ScheduledJobService>.Instance);

        var migrated = await jobs.GetAsync(jobId);

        Assert.NotNull(migrated);
        Assert.Equal("pre-migration", migrated!.Name);
        Assert.Null(migrated.PersonaId);
        Assert.Null(migrated.ReasoningEffort);
        Assert.Null(migrated.BlueprintKey);
    }

    [Fact]
    public async Task MigrateSchema_AddsTheMeetingColumns_AndRoundTripsThem()
    {
        var jobId = Guid.NewGuid();
        var now = DateTime.Now.ToString("O");

        // The pre-meeting shape: everything through BlueprintKey, and neither meeting column.
        using (var seed = new SqliteConnection($"Data Source={_dbPath}"))
        {
            seed.Open();
            using (var create = seed.CreateCommand())
            {
                create.CommandText = """
                    CREATE TABLE ScheduledJobs (
                        Id TEXT PRIMARY KEY,
                        Name TEXT NOT NULL,
                        Query TEXT NOT NULL,
                        Kind TEXT NOT NULL DEFAULT 'Research',
                        AnswerLength TEXT NOT NULL DEFAULT 'Balanced',
                        ProviderId TEXT NULL,
                        Recurrence TEXT NOT NULL,
                        TimeOfDay TEXT NOT NULL,
                        DayOfWeek INTEGER NULL,
                        DayOfMonth INTEGER NULL,
                        Month INTEGER NULL,
                        SpecificDate TEXT NULL,
                        NextFireAt TEXT NOT NULL,
                        Status TEXT NOT NULL DEFAULT 'Active',
                        CreatedAt TEXT NOT NULL,
                        UpdatedAt TEXT NOT NULL DEFAULT '',
                        LastFiredAt TEXT NULL,
                        LastResultEntryId TEXT NULL,
                        ConsecutiveFailures INTEGER NOT NULL DEFAULT 0,
                        OwnerDeviceId TEXT NULL,
                        GrantedTools TEXT NOT NULL DEFAULT '[]',
                        QuietOnSuccess INTEGER NOT NULL DEFAULT 0,
                        PersonaId TEXT NULL,
                        ReasoningEffort TEXT NULL,
                        BlueprintKey TEXT NULL
                    );
                    """;
                create.ExecuteNonQuery();
            }
            using (var insert = seed.CreateCommand())
            {
                insert.CommandText = """
                    INSERT INTO ScheduledJobs
                    (Id, Name, Query, Kind, Recurrence, TimeOfDay, NextFireAt, Status, CreatedAt, UpdatedAt)
                    VALUES (@Id, 'pre-meeting', 'what changed', 'Research', 'Daily', '09:00',
                            @Now, 'Active', @Now, @Now)
                    """;
                insert.Parameters.AddWithValue("@Id", jobId.ToString());
                insert.Parameters.AddWithValue("@Now", now);
                insert.ExecuteNonQuery();
            }
        }
        SqlitePool.ClearFor($"Data Source={_dbPath}");

        using var ctx = new SqliteContext(_dbPath);
        var columns = new List<string>();
        using (var pragma = ctx.GetConnection().CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(ScheduledJobs)";
            using var reader = pragma.ExecuteReader();
            while (reader.Read())
                columns.Add(reader.GetString(1));
        }

        Assert.Contains("MeetingUrl", columns);
        Assert.Contains("MeetingConsentAckAt", columns);

        var settings = Substitute.For<ISettingsService>();
        settings.GetSettingsAsync().Returns(new AppSettings());
        var jobs = new ScheduledJobService(ctx, new RecurrenceCalculator(), settings,
            new SyncDeleteTrackerService(_tmpDir, NullLogger<SyncDeleteTrackerService>.Instance),
            NullLogger<ScheduledJobService>.Instance);

        var migrated = await jobs.GetAsync(jobId);
        Assert.NotNull(migrated);
        Assert.Null(migrated!.MeetingUrl);
        Assert.Null(migrated.MeetingConsentAckAt);

        // The positional read is only half the contract; a create has to survive the round trip too.
        var consentAt = new DateTime(2026, 8, 27, 9, 30, 0, DateTimeKind.Unspecified);
        var created = await jobs.CreateAsync(
            "Standup", "Standup", RecurrenceType.Daily, new TimeOnly(9, 0),
            kind: ScheduledJobKind.MeetingAttendance,
            meetingUrl: "https://teams.microsoft.com/l/meetup-join/x",
            meetingConsentAckAt: consentAt);

        var reread = await jobs.GetAsync(created.Id);
        Assert.Equal(ScheduledJobKind.MeetingAttendance, reread!.Kind);
        Assert.Equal("https://teams.microsoft.com/l/meetup-join/x", reread.MeetingUrl);
        Assert.Equal(consentAt, reread.MeetingConsentAckAt);
    }
}
