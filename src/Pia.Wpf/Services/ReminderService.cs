using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

public class ReminderService : IReminderService
{
    private readonly SqliteContext _context;
    private readonly ILogger<ReminderService> _logger;

    public ReminderService(SqliteContext context, ILogger<ReminderService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<Reminder> CreateAsync(string description, RecurrenceType recurrence, TimeOnly timeOfDay,
        DayOfWeek? dayOfWeek = null, int? dayOfMonth = null, int? month = null, DateTime? specificDate = null)
    {
        var reminder = new Reminder
        {
            Description = description,
            Recurrence = recurrence,
            TimeOfDay = timeOfDay,
            DayOfWeek = dayOfWeek,
            DayOfMonth = dayOfMonth,
            Month = month,
            SpecificDate = specificDate,
            CreatedAt = DateTime.Now
        };

        reminder.NextFireAt = ComputeNextFireAt(reminder);

        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Reminders (Id, Description, Recurrence, TimeOfDay, DayOfWeek, DayOfMonth, Month, SpecificDate, NextFireAt, Status, CreatedAt, LastFiredAt)
            VALUES (@Id, @Description, @Recurrence, @TimeOfDay, @DayOfWeek, @DayOfMonth, @Month, @SpecificDate, @NextFireAt, @Status, @CreatedAt, @LastFiredAt)
            """;

        AddReminderParameters(command, reminder);
        await command.ExecuteNonQueryAsync();

        _logger.LogInformation("Created reminder {Id}: {Description} ({Recurrence})", reminder.Id, description, recurrence);
        return reminder;
    }

    public async Task<IReadOnlyList<Reminder>> GetAllAsync()
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Description, Recurrence, TimeOfDay, DayOfWeek, DayOfMonth, Month, SpecificDate, NextFireAt, Status, CreatedAt, LastFiredAt
            FROM Reminders ORDER BY NextFireAt ASC
            """;

        return await ReadReminders(command);
    }

    public async Task<IReadOnlyList<Reminder>> GetActiveAsync()
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Description, Recurrence, TimeOfDay, DayOfWeek, DayOfMonth, Month, SpecificDate, NextFireAt, Status, CreatedAt, LastFiredAt
            FROM Reminders WHERE Status = 'Active'
            ORDER BY NextFireAt ASC
            """;

        return await ReadReminders(command);
    }

    public async Task<Reminder?> GetAsync(Guid id)
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Description, Recurrence, TimeOfDay, DayOfWeek, DayOfMonth, Month, SpecificDate, NextFireAt, Status, CreatedAt, LastFiredAt
            FROM Reminders WHERE Id = @Id
            """;
        command.Parameters.AddWithValue("@Id", id.ToString());

        using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
            return MapReminder(reader);

        return null;
    }

    public async Task<IReadOnlyList<Reminder>> GetDueRemindersAsync()
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Description, Recurrence, TimeOfDay, DayOfWeek, DayOfMonth, Month, SpecificDate, NextFireAt, Status, CreatedAt, LastFiredAt
            FROM Reminders
            WHERE NextFireAt <= @Now AND Status IN ('Active', 'Snoozed')
            ORDER BY NextFireAt ASC
            """;
        command.Parameters.AddWithValue("@Now", DateTime.Now.ToString("O"));

        return await ReadReminders(command);
    }

    public async Task UpdateAsync(Guid id, string? description = null, RecurrenceType? recurrence = null,
        TimeOnly? timeOfDay = null, DayOfWeek? dayOfWeek = null, int? dayOfMonth = null, int? month = null)
    {
        var existing = await GetAsync(id)
            ?? throw new InvalidOperationException($"Reminder {id} not found");

        if (description is not null) existing.Description = description;
        if (recurrence is not null) existing.Recurrence = recurrence.Value;
        if (timeOfDay is not null) existing.TimeOfDay = timeOfDay.Value;
        if (dayOfWeek is not null) existing.DayOfWeek = dayOfWeek;
        if (dayOfMonth is not null) existing.DayOfMonth = dayOfMonth;
        if (month is not null) existing.Month = month;

        existing.NextFireAt = ComputeNextFireAt(existing);

        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Reminders
            SET Description = @Description, Recurrence = @Recurrence, TimeOfDay = @TimeOfDay,
                DayOfWeek = @DayOfWeek, DayOfMonth = @DayOfMonth, Month = @Month,
                NextFireAt = @NextFireAt
            WHERE Id = @Id
            """;

        command.Parameters.AddWithValue("@Id", existing.Id.ToString());
        command.Parameters.AddWithValue("@Description", existing.Description);
        command.Parameters.AddWithValue("@Recurrence", existing.Recurrence.ToString());
        command.Parameters.AddWithValue("@TimeOfDay", existing.TimeOfDay.ToString("HH:mm"));
        command.Parameters.AddWithValue("@DayOfWeek", existing.DayOfWeek.HasValue ? (object)(int)existing.DayOfWeek.Value : DBNull.Value);
        command.Parameters.AddWithValue("@DayOfMonth", existing.DayOfMonth.HasValue ? (object)existing.DayOfMonth.Value : DBNull.Value);
        command.Parameters.AddWithValue("@Month", existing.Month.HasValue ? (object)existing.Month.Value : DBNull.Value);
        command.Parameters.AddWithValue("@NextFireAt", existing.NextFireAt.ToString("O"));

        await command.ExecuteNonQueryAsync();
        _logger.LogInformation("Updated reminder {Id}", id);
    }

    public async Task DeleteAsync(Guid id)
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Reminders WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id.ToString());

        await command.ExecuteNonQueryAsync();
        _logger.LogInformation("Deleted reminder {Id}", id);
    }

    public async Task SnoozeAsync(Guid id, TimeSpan duration)
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Reminders SET NextFireAt = @NextFireAt, Status = 'Snoozed'
            WHERE Id = @Id
            """;
        command.Parameters.AddWithValue("@Id", id.ToString());
        command.Parameters.AddWithValue("@NextFireAt", DateTime.Now.Add(duration).ToString("O"));

        await command.ExecuteNonQueryAsync();
        _logger.LogInformation("Snoozed reminder {Id} for {Duration}", id, duration);
    }

    public async Task DismissAsync(Guid id)
    {
        var existing = await GetAsync(id);
        if (existing is null) return;

        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();

        if (existing.Recurrence == RecurrenceType.Once)
        {
            command.CommandText = """
                UPDATE Reminders SET Status = 'Completed', LastFiredAt = @Now
                WHERE Id = @Id
                """;
        }
        else
        {
            var nextFire = ComputeNextFireAt(existing);
            command.CommandText = """
                UPDATE Reminders SET NextFireAt = @NextFireAt, Status = 'Active', LastFiredAt = @Now
                WHERE Id = @Id
                """;
            command.Parameters.AddWithValue("@NextFireAt", nextFire.ToString("O"));
        }

        command.Parameters.AddWithValue("@Id", id.ToString());
        command.Parameters.AddWithValue("@Now", DateTime.Now.ToString("O"));

        await command.ExecuteNonQueryAsync();
        _logger.LogInformation("Dismissed reminder {Id}", id);
    }

    public async Task DisableAsync(Guid id)
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Reminders SET Status = 'Disabled' WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id.ToString());

        await command.ExecuteNonQueryAsync();
        _logger.LogInformation("Disabled reminder {Id}", id);
    }

    public async Task EnableAsync(Guid id)
    {
        var existing = await GetAsync(id)
            ?? throw new InvalidOperationException($"Reminder {id} not found");

        var nextFire = ComputeNextFireAt(existing);

        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Reminders SET Status = 'Active', NextFireAt = @NextFireAt WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id.ToString());
        command.Parameters.AddWithValue("@NextFireAt", nextFire.ToString("O"));

        await command.ExecuteNonQueryAsync();
        _logger.LogInformation("Enabled reminder {Id}", id);
    }

    public async Task CleanupCompletedAsync(TimeSpan olderThan)
    {
        var cutoff = DateTime.Now - olderThan;

        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM Reminders
            WHERE Status = 'Completed' AND LastFiredAt < @Cutoff
            """;
        command.Parameters.AddWithValue("@Cutoff", cutoff.ToString("O"));

        var deleted = await command.ExecuteNonQueryAsync();
        if (deleted > 0)
            _logger.LogInformation("Cleaned up {Count} completed reminders", deleted);
    }

    private static DateTime ComputeNextFireAt(Reminder reminder)
    {
        var now = DateTime.Now;
        var todayAtTime = now.Date + reminder.TimeOfDay.ToTimeSpan();

        return reminder.Recurrence switch
        {
            RecurrenceType.Once => reminder.SpecificDate.HasValue
                ? reminder.SpecificDate.Value.Date + reminder.TimeOfDay.ToTimeSpan()
                : todayAtTime > now ? todayAtTime : todayAtTime.AddDays(1),

            RecurrenceType.Daily => todayAtTime > now ? todayAtTime : todayAtTime.AddDays(1),

            RecurrenceType.Weekly => ComputeNextWeekly(now, reminder.TimeOfDay, reminder.DayOfWeek ?? now.DayOfWeek),

            RecurrenceType.Monthly => ComputeNextMonthly(now, reminder.TimeOfDay, reminder.DayOfMonth ?? now.Day),

            RecurrenceType.Yearly => ComputeNextYearly(now, reminder.TimeOfDay, reminder.Month ?? now.Month, reminder.DayOfMonth ?? now.Day),

            _ => todayAtTime > now ? todayAtTime : todayAtTime.AddDays(1)
        };
    }

    private static DateTime ComputeNextWeekly(DateTime now, TimeOnly timeOfDay, DayOfWeek targetDay)
    {
        var daysUntil = ((int)targetDay - (int)now.DayOfWeek + 7) % 7;
        var candidate = now.Date.AddDays(daysUntil) + timeOfDay.ToTimeSpan();

        // If it's today but time has passed, go to next week
        if (candidate <= now)
            candidate = candidate.AddDays(7);

        return candidate;
    }

    private static DateTime ComputeNextMonthly(DateTime now, TimeOnly timeOfDay, int targetDay)
    {
        targetDay = Math.Min(targetDay, DateTime.DaysInMonth(now.Year, now.Month));
        var candidate = new DateTime(now.Year, now.Month, targetDay) + timeOfDay.ToTimeSpan();

        if (candidate <= now)
        {
            var next = now.AddMonths(1);
            targetDay = Math.Min(targetDay, DateTime.DaysInMonth(next.Year, next.Month));
            candidate = new DateTime(next.Year, next.Month, targetDay) + timeOfDay.ToTimeSpan();
        }

        return candidate;
    }

    private static DateTime ComputeNextYearly(DateTime now, TimeOnly timeOfDay, int targetMonth, int targetDay)
    {
        targetDay = Math.Min(targetDay, DateTime.DaysInMonth(now.Year, targetMonth));
        var candidate = new DateTime(now.Year, targetMonth, targetDay) + timeOfDay.ToTimeSpan();

        if (candidate <= now)
        {
            var nextYear = now.Year + 1;
            targetDay = Math.Min(targetDay, DateTime.DaysInMonth(nextYear, targetMonth));
            candidate = new DateTime(nextYear, targetMonth, targetDay) + timeOfDay.ToTimeSpan();
        }

        return candidate;
    }

    private static void AddReminderParameters(SqliteCommand command, Reminder reminder)
    {
        command.Parameters.AddWithValue("@Id", reminder.Id.ToString());
        command.Parameters.AddWithValue("@Description", reminder.Description);
        command.Parameters.AddWithValue("@Recurrence", reminder.Recurrence.ToString());
        command.Parameters.AddWithValue("@TimeOfDay", reminder.TimeOfDay.ToString("HH:mm"));
        command.Parameters.AddWithValue("@DayOfWeek", reminder.DayOfWeek.HasValue ? (object)(int)reminder.DayOfWeek.Value : DBNull.Value);
        command.Parameters.AddWithValue("@DayOfMonth", reminder.DayOfMonth.HasValue ? (object)reminder.DayOfMonth.Value : DBNull.Value);
        command.Parameters.AddWithValue("@Month", reminder.Month.HasValue ? (object)reminder.Month.Value : DBNull.Value);
        command.Parameters.AddWithValue("@SpecificDate", reminder.SpecificDate.HasValue ? (object)reminder.SpecificDate.Value.ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("@NextFireAt", reminder.NextFireAt.ToString("O"));
        command.Parameters.AddWithValue("@Status", reminder.Status.ToString());
        command.Parameters.AddWithValue("@CreatedAt", reminder.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("@LastFiredAt", reminder.LastFiredAt.HasValue ? (object)reminder.LastFiredAt.Value.ToString("O") : DBNull.Value);
    }

    private static async Task<IReadOnlyList<Reminder>> ReadReminders(SqliteCommand command)
    {
        var reminders = new List<Reminder>();
        using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
            reminders.Add(MapReminder(reader));

        return reminders.AsReadOnly();
    }

    private static Reminder MapReminder(SqliteDataReader reader)
    {
        return new Reminder
        {
            Id = Guid.Parse(reader.GetString(0)),
            Description = reader.GetString(1),
            Recurrence = Enum.Parse<RecurrenceType>(reader.GetString(2)),
            TimeOfDay = TimeOnly.Parse(reader.GetString(3)),
            DayOfWeek = reader.IsDBNull(4) ? null : (DayOfWeek)reader.GetInt32(4),
            DayOfMonth = reader.IsDBNull(5) ? null : reader.GetInt32(5),
            Month = reader.IsDBNull(6) ? null : reader.GetInt32(6),
            SpecificDate = reader.IsDBNull(7) ? null : DateTime.Parse(reader.GetString(7)),
            NextFireAt = DateTime.Parse(reader.GetString(8)),
            Status = Enum.Parse<ReminderStatus>(reader.GetString(9)),
            CreatedAt = DateTime.Parse(reader.GetString(10)),
            LastFiredAt = reader.IsDBNull(11) ? null : DateTime.Parse(reader.GetString(11))
        };
    }
}
