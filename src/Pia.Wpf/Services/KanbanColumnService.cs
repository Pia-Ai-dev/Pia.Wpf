using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

public class KanbanColumnService : IKanbanColumnService
{
    private readonly SqliteContext _context;
    private readonly ILogger<KanbanColumnService> _logger;

    public event EventHandler? ColumnsChanged;

    public KanbanColumnService(SqliteContext context, ILogger<KanbanColumnService> logger)
    {
        _context = context;
        _logger = logger;
    }

    private void OnColumnsChanged() => ColumnsChanged?.Invoke(this, EventArgs.Empty);

    public async Task<IReadOnlyList<KanbanColumn>> GetAllAsync()
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Name, SortOrder, IsDefaultView, IsClosedColumn, CreatedAt, UpdatedAt
            FROM KanbanColumns ORDER BY SortOrder ASC
            """;

        var items = new List<KanbanColumn>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            items.Add(MapKanbanColumn(reader));

        return items.AsReadOnly();
    }

    public async Task<KanbanColumn?> GetAsync(Guid id)
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Name, SortOrder, IsDefaultView, IsClosedColumn, CreatedAt, UpdatedAt
            FROM KanbanColumns WHERE Id = @Id
            """;
        command.Parameters.AddWithValue("@Id", id.ToString());

        using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
            return MapKanbanColumn(reader);

        return null;
    }

    public async Task<KanbanColumn> GetDefaultViewColumnAsync()
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Name, SortOrder, IsDefaultView, IsClosedColumn, CreatedAt, UpdatedAt
            FROM KanbanColumns WHERE IsDefaultView = 1 LIMIT 1
            """;

        using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
            return MapKanbanColumn(reader);

        // Fallback: first non-closed column
        using var fallback = connection.CreateCommand();
        fallback.CommandText = """
            SELECT Id, Name, SortOrder, IsDefaultView, IsClosedColumn, CreatedAt, UpdatedAt
            FROM KanbanColumns WHERE IsClosedColumn = 0 ORDER BY SortOrder ASC LIMIT 1
            """;

        using var fallbackReader = await fallback.ExecuteReaderAsync();
        if (await fallbackReader.ReadAsync())
            return MapKanbanColumn(fallbackReader);

        throw new InvalidOperationException("No kanban columns exist.");
    }

    public async Task<KanbanColumn> GetClosedColumnAsync()
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Name, SortOrder, IsDefaultView, IsClosedColumn, CreatedAt, UpdatedAt
            FROM KanbanColumns WHERE IsClosedColumn = 1 LIMIT 1
            """;

        using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
            return MapKanbanColumn(reader);

        throw new InvalidOperationException("No closed column exists.");
    }

    public async Task<KanbanColumn> CreateAsync(string name)
    {
        var connection = _context.GetConnection();

        // Get max SortOrder from non-closed columns
        using var maxCmd = connection.CreateCommand();
        maxCmd.CommandText = "SELECT COALESCE(MAX(SortOrder), -1) + 1 FROM KanbanColumns WHERE IsClosedColumn = 0";
        var maxResult = await maxCmd.ExecuteScalarAsync();
        var sortOrder = Convert.ToInt32(maxResult);

        var column = new KanbanColumn
        {
            Name = name,
            SortOrder = sortOrder,
            IsDefaultView = false,
            IsClosedColumn = false,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO KanbanColumns (Id, Name, SortOrder, IsDefaultView, IsClosedColumn, CreatedAt, UpdatedAt)
            VALUES (@Id, @Name, @SortOrder, @IsDefaultView, @IsClosedColumn, @CreatedAt, @UpdatedAt)
            """;

        AddColumnParameters(command, column);
        await command.ExecuteNonQueryAsync();

        _logger.LogInformation("Created kanban column {Id}: {Name}", column.Id, name);
        OnColumnsChanged();
        return column;
    }

    public async Task RenameAsync(Guid id, string newName)
    {
        var column = await GetAsync(id)
            ?? throw new InvalidOperationException($"Column {id} not found.");

        if (column.IsClosedColumn)
            throw new InvalidOperationException("Cannot rename the Closed column");

        var now = DateTime.Now;
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE KanbanColumns SET Name = @Name, UpdatedAt = @UpdatedAt WHERE Id = @Id
            """;
        command.Parameters.AddWithValue("@Id", id.ToString());
        command.Parameters.AddWithValue("@Name", newName);
        command.Parameters.AddWithValue("@UpdatedAt", now.ToString("O"));

        await command.ExecuteNonQueryAsync();
        _logger.LogInformation("Renamed kanban column {Id} to {Name}", id, newName);
        OnColumnsChanged();
    }

    public async Task DeleteAsync(Guid id)
    {
        var column = await GetAsync(id)
            ?? throw new InvalidOperationException($"Column {id} not found.");

        if (column.IsClosedColumn)
            throw new InvalidOperationException("Cannot delete the Closed column");

        var connection = _context.GetConnection();

        // Check it's not the last non-closed column
        using var countCmd = connection.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM KanbanColumns WHERE IsClosedColumn = 0";
        var count = Convert.ToInt32(await countCmd.ExecuteScalarAsync());
        if (count <= 1)
            throw new InvalidOperationException("Cannot delete the last non-closed column");

        // Check column has no todos
        using var todoCountCmd = connection.CreateCommand();
        todoCountCmd.CommandText = "SELECT COUNT(*) FROM Todos WHERE ColumnId = @ColumnId";
        todoCountCmd.Parameters.AddWithValue("@ColumnId", id.ToString());
        var todoCount = Convert.ToInt32(await todoCountCmd.ExecuteScalarAsync());
        if (todoCount > 0)
            throw new InvalidOperationException("Cannot delete a column that contains todos");

        // If deleting the default view column, reassign
        if (column.IsDefaultView)
        {
            using var reassignCmd = connection.CreateCommand();
            reassignCmd.CommandText = """
                UPDATE KanbanColumns SET IsDefaultView = 1, UpdatedAt = @UpdatedAt
                WHERE IsClosedColumn = 0 AND Id != @Id
                ORDER BY SortOrder ASC LIMIT 1
                """;
            reassignCmd.Parameters.AddWithValue("@Id", id.ToString());
            reassignCmd.Parameters.AddWithValue("@UpdatedAt", DateTime.Now.ToString("O"));
            await reassignCmd.ExecuteNonQueryAsync();
        }

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM KanbanColumns WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id.ToString());
        await command.ExecuteNonQueryAsync();

        _logger.LogInformation("Deleted kanban column {Id}", id);
        OnColumnsChanged();
    }

    public async Task SetDefaultViewAsync(Guid id)
    {
        var column = await GetAsync(id)
            ?? throw new InvalidOperationException($"Column {id} not found.");

        if (column.IsClosedColumn)
            throw new InvalidOperationException("Cannot set the Closed column as default view");

        var now = DateTime.Now;
        var connection = _context.GetConnection();
        using var transaction = connection.BeginTransaction();

        try
        {
            using var clearCmd = connection.CreateCommand();
            clearCmd.Transaction = transaction;
            clearCmd.CommandText = "UPDATE KanbanColumns SET IsDefaultView = 0, UpdatedAt = @UpdatedAt";
            clearCmd.Parameters.AddWithValue("@UpdatedAt", now.ToString("O"));
            await clearCmd.ExecuteNonQueryAsync();

            using var setCmd = connection.CreateCommand();
            setCmd.Transaction = transaction;
            setCmd.CommandText = "UPDATE KanbanColumns SET IsDefaultView = 1, UpdatedAt = @UpdatedAt WHERE Id = @Id";
            setCmd.Parameters.AddWithValue("@Id", id.ToString());
            setCmd.Parameters.AddWithValue("@UpdatedAt", now.ToString("O"));
            await setCmd.ExecuteNonQueryAsync();

            transaction.Commit();
            _logger.LogInformation("Set kanban column {Id} as default view", id);
            OnColumnsChanged();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task ImportAsync(KanbanColumn column)
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO KanbanColumns (Id, Name, SortOrder, IsDefaultView, IsClosedColumn, CreatedAt, UpdatedAt)
            VALUES (@Id, @Name, @SortOrder, @IsDefaultView, @IsClosedColumn, @CreatedAt, @UpdatedAt)
            """;

        AddColumnParameters(command, column);
        await command.ExecuteNonQueryAsync();

        _logger.LogInformation("Imported kanban column {Id}: {Name}", column.Id, column.Name);
        OnColumnsChanged();
    }

    public async Task<int> GetTodoCountAsync(Guid columnId)
    {
        var connection = _context.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Todos WHERE ColumnId = @ColumnId";
        command.Parameters.AddWithValue("@ColumnId", columnId.ToString());
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    private static KanbanColumn MapKanbanColumn(SqliteDataReader reader)
    {
        return new KanbanColumn
        {
            Id = Guid.Parse(reader.GetString(0)),
            Name = reader.GetString(1),
            SortOrder = reader.GetInt32(2),
            IsDefaultView = reader.GetInt32(3) == 1,
            IsClosedColumn = reader.GetInt32(4) == 1,
            CreatedAt = DateTime.Parse(reader.GetString(5)),
            UpdatedAt = DateTime.Parse(reader.GetString(6))
        };
    }

    private static void AddColumnParameters(SqliteCommand command, KanbanColumn column)
    {
        command.Parameters.AddWithValue("@Id", column.Id.ToString());
        command.Parameters.AddWithValue("@Name", column.Name);
        command.Parameters.AddWithValue("@SortOrder", column.SortOrder);
        command.Parameters.AddWithValue("@IsDefaultView", column.IsDefaultView ? 1 : 0);
        command.Parameters.AddWithValue("@IsClosedColumn", column.IsClosedColumn ? 1 : 0);
        command.Parameters.AddWithValue("@CreatedAt", column.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("@UpdatedAt", column.UpdatedAt.ToString("O"));
    }
}
