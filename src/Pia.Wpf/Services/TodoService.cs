using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

public class TodoService : ITodoService
{
    private readonly SqliteContext _context;
    private readonly ILogger<TodoService> _logger;

    public event EventHandler? TodoChanged;

    public TodoService(SqliteContext context, ILogger<TodoService> logger)
    {
        _context = context;
        _logger = logger;
    }

    private void OnTodoChanged() => TodoChanged?.Invoke(this, EventArgs.Empty);

    public async Task<TodoItem> CreateAsync(string title, TodoPriority priority = TodoPriority.Medium,
                                             string? notes = null, DateTime? dueDate = null)
    {
        var todo = new TodoItem
        {
            Title = title,
            Priority = priority,
            Notes = notes,
            DueDate = dueDate,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };

        var connection = _context.GetConnection();

        // Assign SortOrder = max + 1 (append to end)
        using var maxCmd = connection.CreateCommand();
        maxCmd.CommandText = "SELECT COALESCE(MAX(SortOrder), -1) + 1 FROM Todos WHERE Status = 0";
        var maxResult = await maxCmd.ExecuteScalarAsync();
        todo.SortOrder = Convert.ToInt32(maxResult);

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Todos (Id, Title, Notes, Priority, Status, DueDate, LinkedReminderId, CreatedAt, CompletedAt, UpdatedAt, SortOrder)
            VALUES (@Id, @Title, @Notes, @Priority, @Status, @DueDate, @LinkedReminderId, @CreatedAt, @CompletedAt, @UpdatedAt, @SortOrder)
            """;

        AddTodoParameters(command, todo);
        await command.ExecuteNonQueryAsync();

        _logger.LogInformation("Created todo {Id}: {Title} (Priority: {Priority})", todo.Id, title, priority);
        OnTodoChanged();
        return todo;
    }

    public async Task<TodoItem?> GetAsync(Guid id)
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Title, Notes, Priority, Status, DueDate, LinkedReminderId, CreatedAt, CompletedAt, UpdatedAt, SortOrder
            FROM Todos WHERE Id = @Id
            """;
        command.Parameters.AddWithValue("@Id", id.ToString());

        using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
            return MapTodoItem(reader);

        return null;
    }

    public async Task<IReadOnlyList<TodoItem>> GetAllAsync()
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Title, Notes, Priority, Status, DueDate, LinkedReminderId, CreatedAt, CompletedAt, UpdatedAt, SortOrder
            FROM Todos ORDER BY SortOrder ASC, CreatedAt ASC
            """;

        return await ReadTodoItems(command);
    }

    public async Task<IReadOnlyList<TodoItem>> GetPendingAsync()
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Title, Notes, Priority, Status, DueDate, LinkedReminderId, CreatedAt, CompletedAt, UpdatedAt, SortOrder
            FROM Todos WHERE Status = 0
            ORDER BY SortOrder ASC, CreatedAt ASC
            """;

        return await ReadTodoItems(command);
    }

    public async Task<IReadOnlyList<TodoItem>> GetCompletedAsync()
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Title, Notes, Priority, Status, DueDate, LinkedReminderId, CreatedAt, CompletedAt, UpdatedAt, SortOrder
            FROM Todos WHERE Status = 1
            ORDER BY CompletedAt DESC
            """;

        return await ReadTodoItems(command);
    }

    public async Task<IReadOnlyList<TodoItem>> GetCompletedTodayAsync()
    {
        var today = DateTime.Now.Date;

        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Title, Notes, Priority, Status, DueDate, LinkedReminderId, CreatedAt, CompletedAt, UpdatedAt, SortOrder
            FROM Todos WHERE Status = 1 AND CompletedAt >= @Today
            ORDER BY CompletedAt DESC
            """;
        command.Parameters.AddWithValue("@Today", today.ToString("O"));

        return await ReadTodoItems(command);
    }

    public async Task<int> GetPendingCountAsync()
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Todos WHERE Status = 0";
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    public async Task UpdateAsync(TodoItem item)
    {
        item.UpdatedAt = DateTime.Now;

        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Todos
            SET Title = @Title, Notes = @Notes, Priority = @Priority, Status = @Status,
                DueDate = @DueDate, LinkedReminderId = @LinkedReminderId,
                CompletedAt = @CompletedAt, UpdatedAt = @UpdatedAt, SortOrder = @SortOrder
            WHERE Id = @Id
            """;

        AddTodoParameters(command, item);
        await command.ExecuteNonQueryAsync();

        _logger.LogInformation("Updated todo {Id}: {Title}", item.Id, item.Title);
        OnTodoChanged();
    }

    public async Task ImportAsync(TodoItem item)
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO Todos (Id, Title, Notes, Priority, Status, DueDate, LinkedReminderId, CreatedAt, CompletedAt, UpdatedAt, SortOrder)
            VALUES (@Id, @Title, @Notes, @Priority, @Status, @DueDate, @LinkedReminderId, @CreatedAt, @CompletedAt, @UpdatedAt, @SortOrder)
            """;

        AddTodoParameters(command, item);
        await command.ExecuteNonQueryAsync();

        _logger.LogInformation("Imported todo {Id}: {Title}", item.Id, item.Title);
        OnTodoChanged();
    }

    public async Task CompleteAsync(Guid id)
    {
        var now = DateTime.Now;

        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Todos SET Status = 1, CompletedAt = @CompletedAt, UpdatedAt = @UpdatedAt
            WHERE Id = @Id
            """;
        command.Parameters.AddWithValue("@Id", id.ToString());
        command.Parameters.AddWithValue("@CompletedAt", now.ToString("O"));
        command.Parameters.AddWithValue("@UpdatedAt", now.ToString("O"));

        await command.ExecuteNonQueryAsync();
        _logger.LogInformation("Completed todo {Id}", id);
        OnTodoChanged();
    }

    public async Task UncompleteAsync(Guid id)
    {
        var now = DateTime.Now;

        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Todos SET Status = 0, CompletedAt = NULL, UpdatedAt = @UpdatedAt
            WHERE Id = @Id
            """;
        command.Parameters.AddWithValue("@Id", id.ToString());
        command.Parameters.AddWithValue("@UpdatedAt", now.ToString("O"));

        await command.ExecuteNonQueryAsync();
        _logger.LogInformation("Uncompleted todo {Id}", id);
        OnTodoChanged();
    }

    public async Task DeleteAsync(Guid id)
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Todos WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id.ToString());

        await command.ExecuteNonQueryAsync();
        _logger.LogInformation("Deleted todo {Id}", id);
        OnTodoChanged();
    }

    public async Task LinkReminderAsync(Guid todoId, Guid reminderId)
    {
        var now = DateTime.Now;

        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Todos SET LinkedReminderId = @ReminderId, UpdatedAt = @UpdatedAt
            WHERE Id = @Id
            """;
        command.Parameters.AddWithValue("@Id", todoId.ToString());
        command.Parameters.AddWithValue("@ReminderId", reminderId.ToString());
        command.Parameters.AddWithValue("@UpdatedAt", now.ToString("O"));

        await command.ExecuteNonQueryAsync();
        _logger.LogInformation("Linked reminder {ReminderId} to todo {TodoId}", reminderId, todoId);
    }

    public async Task UnlinkReminderAsync(Guid todoId)
    {
        var now = DateTime.Now;

        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Todos SET LinkedReminderId = NULL, UpdatedAt = @UpdatedAt
            WHERE Id = @Id
            """;
        command.Parameters.AddWithValue("@Id", todoId.ToString());
        command.Parameters.AddWithValue("@UpdatedAt", now.ToString("O"));

        await command.ExecuteNonQueryAsync();
        _logger.LogInformation("Unlinked reminder from todo {TodoId}", todoId);
    }

    public async Task CleanupOldCompletedAsync(int olderThanDays = 30)
    {
        var cutoff = DateTime.Now.AddDays(-olderThanDays);

        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM Todos
            WHERE Status = 1 AND CompletedAt < @Cutoff
            """;
        command.Parameters.AddWithValue("@Cutoff", cutoff.ToString("O"));

        var deleted = await command.ExecuteNonQueryAsync();
        if (deleted > 0)
            _logger.LogInformation("Cleaned up {Count} old completed todos", deleted);
    }

    private static void AddTodoParameters(SqliteCommand command, TodoItem todo)
    {
        command.Parameters.AddWithValue("@Id", todo.Id.ToString());
        command.Parameters.AddWithValue("@Title", todo.Title);
        command.Parameters.AddWithValue("@Notes", todo.Notes is not null ? (object)todo.Notes : DBNull.Value);
        command.Parameters.AddWithValue("@Priority", (int)todo.Priority);
        command.Parameters.AddWithValue("@Status", (int)todo.Status);
        command.Parameters.AddWithValue("@DueDate", todo.DueDate.HasValue ? (object)todo.DueDate.Value.ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("@LinkedReminderId", todo.LinkedReminderId.HasValue ? (object)todo.LinkedReminderId.Value.ToString() : DBNull.Value);
        command.Parameters.AddWithValue("@CreatedAt", todo.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("@CompletedAt", todo.CompletedAt.HasValue ? (object)todo.CompletedAt.Value.ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("@UpdatedAt", todo.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("@SortOrder", todo.SortOrder);
    }

    private static async Task<IReadOnlyList<TodoItem>> ReadTodoItems(SqliteCommand command)
    {
        var items = new List<TodoItem>();
        using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
            items.Add(MapTodoItem(reader));

        return items.AsReadOnly();
    }

    private static TodoItem MapTodoItem(SqliteDataReader reader)
    {
        return new TodoItem
        {
            Id = Guid.Parse(reader.GetString(0)),
            Title = reader.GetString(1),
            Notes = reader.IsDBNull(2) ? null : reader.GetString(2),
            Priority = (TodoPriority)reader.GetInt32(3),
            Status = (TodoStatus)reader.GetInt32(4),
            DueDate = reader.IsDBNull(5) ? null : DateTime.Parse(reader.GetString(5)),
            LinkedReminderId = reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6)),
            CreatedAt = DateTime.Parse(reader.GetString(7)),
            CompletedAt = reader.IsDBNull(8) ? null : DateTime.Parse(reader.GetString(8)),
            UpdatedAt = DateTime.Parse(reader.GetString(9)),
            SortOrder = reader.GetInt32(10)
        };
    }

    public async Task UpdateSortOrderAsync(IReadOnlyList<(Guid Id, int SortOrder)> updates)
    {
        var connection = _context.GetConnection();
        using var transaction = connection.BeginTransaction();

        try
        {
            foreach (var (id, sortOrder) in updates)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE Todos SET SortOrder = @SortOrder, UpdatedAt = @UpdatedAt WHERE Id = @Id";
                command.Parameters.AddWithValue("@Id", id.ToString());
                command.Parameters.AddWithValue("@SortOrder", sortOrder);
                command.Parameters.AddWithValue("@UpdatedAt", DateTime.Now.ToString("O"));
                await command.ExecuteNonQueryAsync();
            }

            transaction.Commit();
            _logger.LogInformation("Updated sort order for {Count} todos", updates.Count);
            OnTodoChanged();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }
}
