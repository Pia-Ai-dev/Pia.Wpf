using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure;
using Pia.Logging;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Services.Scheduling;

namespace Pia.Services;

public class ScheduledJobService : IScheduledJobService
{
    private const int MaxConsecutiveFailures = 5;

    private readonly SqliteContext _context;
    private readonly IRecurrenceCalculator _calculator;
    private readonly ILogger<ScheduledJobService> _logger;

    public ScheduledJobService(SqliteContext context, IRecurrenceCalculator calculator, ILogger<ScheduledJobService> logger)
    {
        _context = context;
        _calculator = calculator;
        _logger = logger;
    }

    public async Task<ScheduledJob> CreateAsync(string name, string query, RecurrenceType recurrence, TimeOnly timeOfDay,
        DayOfWeek? dayOfWeek = null, int? dayOfMonth = null, int? month = null, DateTime? specificDate = null,
        ResearchAnswerLength answerLength = ResearchAnswerLength.Balanced, Guid? providerId = null)
    {
        var job = new ScheduledJob
        {
            Name = name,
            Query = query,
            Recurrence = recurrence,
            TimeOfDay = timeOfDay,
            DayOfWeek = dayOfWeek,
            DayOfMonth = dayOfMonth,
            Month = month,
            SpecificDate = specificDate,
            AnswerLength = answerLength,
            ProviderId = providerId,
            CreatedAt = DateTime.Now
        };

        job.NextFireAt = _calculator.ComputeNextFireAt(
            job.Recurrence, job.TimeOfDay, job.SpecificDate, job.DayOfWeek, job.DayOfMonth, job.Month, DateTime.Now);

        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ScheduledJobs
            (Id, Name, Query, Kind, AnswerLength, ProviderId, Recurrence, TimeOfDay,
             DayOfWeek, DayOfMonth, Month, SpecificDate, NextFireAt, Status, CreatedAt,
             LastFiredAt, LastResultEntryId, ConsecutiveFailures)
            VALUES (@Id, @Name, @Query, @Kind, @AnswerLength, @ProviderId, @Recurrence, @TimeOfDay,
                    @DayOfWeek, @DayOfMonth, @Month, @SpecificDate, @NextFireAt, @Status, @CreatedAt,
                    @LastFiredAt, @LastResultEntryId, @ConsecutiveFailures)
            """;
        AddJobParameters(command, job);
        await command.ExecuteNonQueryAsync();

        _logger.LogInformation("Created scheduled job {Id} ({Recurrence})", job.Id, recurrence);
        _logger.SensitiveDebug("Created scheduled job {Id} name: {Name} query: {Query}", job.Id, name, query);
        return job;
    }

    public async Task<IReadOnlyList<ScheduledJob>> GetAllAsync() =>
        await ReadAsync("ORDER BY NextFireAt ASC", _ => { });

    public async Task<IReadOnlyList<ScheduledJob>> GetActiveAsync() =>
        await ReadAsync("WHERE Status = 'Active' ORDER BY NextFireAt ASC", _ => { });

    public async Task<ScheduledJob?> GetAsync(Guid id)
    {
        var list = await ReadAsync("WHERE Id = @Id", cmd => cmd.Parameters.AddWithValue("@Id", id.ToString()));
        return list.FirstOrDefault();
    }

    public async Task<IReadOnlyList<ScheduledJob>> GetDueJobsAsync() =>
        await ReadAsync(
            "WHERE NextFireAt <= @Now AND Status = 'Active' ORDER BY NextFireAt ASC",
            cmd => cmd.Parameters.AddWithValue("@Now", DateTime.Now.ToString("O")));

    public async Task UpdateAsync(Guid id, string? name = null, string? query = null,
        RecurrenceType? recurrence = null, TimeOnly? timeOfDay = null,
        DayOfWeek? dayOfWeek = null, int? dayOfMonth = null, int? month = null,
        ResearchAnswerLength? answerLength = null, Guid? providerId = null)
    {
        var existing = await GetAsync(id) ?? throw new InvalidOperationException($"ScheduledJob {id} not found");

        if (name is not null) existing.Name = name;
        if (query is not null) existing.Query = query;
        if (recurrence is not null) existing.Recurrence = recurrence.Value;
        if (timeOfDay is not null) existing.TimeOfDay = timeOfDay.Value;
        if (dayOfWeek is not null) existing.DayOfWeek = dayOfWeek;
        if (dayOfMonth is not null) existing.DayOfMonth = dayOfMonth;
        if (month is not null) existing.Month = month;
        if (answerLength is not null) existing.AnswerLength = answerLength.Value;
        if (providerId is not null) existing.ProviderId = providerId;

        existing.NextFireAt = _calculator.ComputeNextFireAt(
            existing.Recurrence, existing.TimeOfDay, existing.SpecificDate,
            existing.DayOfWeek, existing.DayOfMonth, existing.Month, DateTime.Now);

        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ScheduledJobs
            SET Name=@Name, Query=@Query, Recurrence=@Recurrence, TimeOfDay=@TimeOfDay,
                DayOfWeek=@DayOfWeek, DayOfMonth=@DayOfMonth, Month=@Month,
                AnswerLength=@AnswerLength, ProviderId=@ProviderId, NextFireAt=@NextFireAt
            WHERE Id=@Id
            """;
        command.Parameters.AddWithValue("@Id", existing.Id.ToString());
        command.Parameters.AddWithValue("@Name", existing.Name);
        command.Parameters.AddWithValue("@Query", existing.Query);
        command.Parameters.AddWithValue("@Recurrence", existing.Recurrence.ToString());
        command.Parameters.AddWithValue("@TimeOfDay", existing.TimeOfDay.ToString("HH:mm"));
        command.Parameters.AddWithValue("@DayOfWeek", existing.DayOfWeek.HasValue ? (object)(int)existing.DayOfWeek.Value : DBNull.Value);
        command.Parameters.AddWithValue("@DayOfMonth", existing.DayOfMonth.HasValue ? (object)existing.DayOfMonth.Value : DBNull.Value);
        command.Parameters.AddWithValue("@Month", existing.Month.HasValue ? (object)existing.Month.Value : DBNull.Value);
        command.Parameters.AddWithValue("@AnswerLength", existing.AnswerLength.ToString());
        command.Parameters.AddWithValue("@ProviderId", existing.ProviderId.HasValue ? (object)existing.ProviderId.Value.ToString() : DBNull.Value);
        command.Parameters.AddWithValue("@NextFireAt", existing.NextFireAt.ToString("O"));

        await command.ExecuteNonQueryAsync();
        _logger.LogInformation("Updated scheduled job {Id}", id);
    }

    public async Task DeleteAsync(Guid id)
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ScheduledJobs WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id.ToString());
        await command.ExecuteNonQueryAsync();
        _logger.LogInformation("Deleted scheduled job {Id}", id);
    }

    public async Task DisableAsync(Guid id)
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ScheduledJobs SET Status = 'Disabled' WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id.ToString());
        await command.ExecuteNonQueryAsync();
        _logger.LogInformation("Disabled scheduled job {Id}", id);
    }

    public async Task EnableAsync(Guid id)
    {
        var existing = await GetAsync(id) ?? throw new InvalidOperationException($"ScheduledJob {id} not found");
        existing.NextFireAt = _calculator.ComputeNextFireAt(
            existing.Recurrence, existing.TimeOfDay, existing.SpecificDate,
            existing.DayOfWeek, existing.DayOfMonth, existing.Month, DateTime.Now);

        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ScheduledJobs SET Status = 'Active', NextFireAt = @NextFireAt, ConsecutiveFailures = 0 WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id.ToString());
        command.Parameters.AddWithValue("@NextFireAt", existing.NextFireAt.ToString("O"));
        await command.ExecuteNonQueryAsync();
        _logger.LogInformation("Enabled scheduled job {Id}", id);
    }

    public async Task MarkRunCompleteAsync(Guid id, Guid resultEntryId)
    {
        var existing = await GetAsync(id) ?? throw new InvalidOperationException($"ScheduledJob {id} not found");
        var nextFire = _calculator.ComputeNextFireAt(
            existing.Recurrence, existing.TimeOfDay, existing.SpecificDate,
            existing.DayOfWeek, existing.DayOfMonth, existing.Month, DateTime.Now);

        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ScheduledJobs
            SET LastFiredAt=@Now, LastResultEntryId=@EntryId, ConsecutiveFailures=0, NextFireAt=@NextFireAt
            WHERE Id=@Id
            """;
        command.Parameters.AddWithValue("@Id", id.ToString());
        command.Parameters.AddWithValue("@Now", DateTime.Now.ToString("O"));
        command.Parameters.AddWithValue("@EntryId", resultEntryId.ToString());
        command.Parameters.AddWithValue("@NextFireAt", nextFire.ToString("O"));
        await command.ExecuteNonQueryAsync();
        _logger.LogInformation("Scheduled job {Id} run completed; next fire {NextFireAt:g}", id, nextFire);
    }

    public async Task MarkRunFailedAsync(Guid id, string reason)
    {
        var existing = await GetAsync(id) ?? throw new InvalidOperationException($"ScheduledJob {id} not found");
        var newFailureCount = existing.ConsecutiveFailures + 1;
        var newStatus = newFailureCount >= MaxConsecutiveFailures ? ScheduledJobStatus.Failed : existing.Status;

        var nextFire = _calculator.ComputeNextFireAt(
            existing.Recurrence, existing.TimeOfDay, existing.SpecificDate,
            existing.DayOfWeek, existing.DayOfMonth, existing.Month, DateTime.Now);

        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ScheduledJobs
            SET LastFiredAt=@Now, ConsecutiveFailures=@Failures, Status=@Status, NextFireAt=@NextFireAt
            WHERE Id=@Id
            """;
        command.Parameters.AddWithValue("@Id", id.ToString());
        command.Parameters.AddWithValue("@Now", DateTime.Now.ToString("O"));
        command.Parameters.AddWithValue("@Failures", newFailureCount);
        command.Parameters.AddWithValue("@Status", newStatus.ToString());
        command.Parameters.AddWithValue("@NextFireAt", nextFire.ToString("O"));
        await command.ExecuteNonQueryAsync();
        _logger.LogWarning("Scheduled job {Id} run failed (count={Count}, status={Status})",
            id, newFailureCount, newStatus);
        _logger.SensitiveDebug("Scheduled job {Id} run failed reason: {Reason}", id, reason);
    }

    private async Task<IReadOnlyList<ScheduledJob>> ReadAsync(string whereOrOrder, Action<SqliteCommand> bind)
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT Id, Name, Query, Kind, AnswerLength, ProviderId, Recurrence, TimeOfDay,
                   DayOfWeek, DayOfMonth, Month, SpecificDate, NextFireAt, Status, CreatedAt,
                   LastFiredAt, LastResultEntryId, ConsecutiveFailures
            FROM ScheduledJobs
            {whereOrOrder}
            """;
        bind(command);

        var list = new List<ScheduledJob>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            list.Add(MapJob(reader));

        return list.AsReadOnly();
    }

    private static void AddJobParameters(SqliteCommand command, ScheduledJob job)
    {
        command.Parameters.AddWithValue("@Id", job.Id.ToString());
        command.Parameters.AddWithValue("@Name", job.Name);
        command.Parameters.AddWithValue("@Query", job.Query);
        command.Parameters.AddWithValue("@Kind", job.Kind.ToString());
        command.Parameters.AddWithValue("@AnswerLength", job.AnswerLength.ToString());
        command.Parameters.AddWithValue("@ProviderId", job.ProviderId.HasValue ? (object)job.ProviderId.Value.ToString() : DBNull.Value);
        command.Parameters.AddWithValue("@Recurrence", job.Recurrence.ToString());
        command.Parameters.AddWithValue("@TimeOfDay", job.TimeOfDay.ToString("HH:mm"));
        command.Parameters.AddWithValue("@DayOfWeek", job.DayOfWeek.HasValue ? (object)(int)job.DayOfWeek.Value : DBNull.Value);
        command.Parameters.AddWithValue("@DayOfMonth", job.DayOfMonth.HasValue ? (object)job.DayOfMonth.Value : DBNull.Value);
        command.Parameters.AddWithValue("@Month", job.Month.HasValue ? (object)job.Month.Value : DBNull.Value);
        command.Parameters.AddWithValue("@SpecificDate", job.SpecificDate.HasValue ? (object)job.SpecificDate.Value.ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("@NextFireAt", job.NextFireAt.ToString("O"));
        command.Parameters.AddWithValue("@Status", job.Status.ToString());
        command.Parameters.AddWithValue("@CreatedAt", job.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("@LastFiredAt", job.LastFiredAt.HasValue ? (object)job.LastFiredAt.Value.ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("@LastResultEntryId", job.LastResultEntryId.HasValue ? (object)job.LastResultEntryId.Value.ToString() : DBNull.Value);
        command.Parameters.AddWithValue("@ConsecutiveFailures", job.ConsecutiveFailures);
    }

    private static ScheduledJob MapJob(SqliteDataReader r) => new()
    {
        Id = Guid.Parse(r.GetString(0)),
        Name = r.GetString(1),
        Query = r.GetString(2),
        Kind = Enum.Parse<ScheduledJobKind>(r.GetString(3)),
        AnswerLength = Enum.Parse<ResearchAnswerLength>(r.GetString(4)),
        ProviderId = r.IsDBNull(5) ? null : Guid.Parse(r.GetString(5)),
        Recurrence = Enum.Parse<RecurrenceType>(r.GetString(6)),
        TimeOfDay = TimeOnly.Parse(r.GetString(7)),
        DayOfWeek = r.IsDBNull(8) ? null : (DayOfWeek)r.GetInt32(8),
        DayOfMonth = r.IsDBNull(9) ? null : r.GetInt32(9),
        Month = r.IsDBNull(10) ? null : r.GetInt32(10),
        SpecificDate = r.IsDBNull(11) ? null : DateTime.Parse(r.GetString(11)),
        NextFireAt = DateTime.Parse(r.GetString(12)),
        Status = Enum.Parse<ScheduledJobStatus>(r.GetString(13)),
        CreatedAt = DateTime.Parse(r.GetString(14)),
        LastFiredAt = r.IsDBNull(15) ? null : DateTime.Parse(r.GetString(15)),
        LastResultEntryId = r.IsDBNull(16) ? null : Guid.Parse(r.GetString(16)),
        ConsecutiveFailures = r.GetInt32(17)
    };
}
