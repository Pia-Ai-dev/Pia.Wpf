using System.IO;
using Microsoft.Data.Sqlite;

namespace Pia.Infrastructure;

public class SqliteContext : IDisposable
{
    private readonly string _connectionString;
    private SqliteConnection? _connection;
    private bool _disposed;

    public SqliteContext()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dbDirectory = Path.Combine(localAppData, "Pia");
        Directory.CreateDirectory(dbDirectory);

        var dbPath = Path.Combine(dbDirectory, "history.db");
        _connectionString = $"Data Source={dbPath}";
    }

    public SqliteConnection GetConnection()
    {
        if (_connection is null)
        {
            _connection = new SqliteConnection(_connectionString);
            _connection.Open();
            EnsureSchema();
        }
        else if (_connection.State != System.Data.ConnectionState.Open)
        {
            _connection.Open();
        }

        return _connection;
    }

    private void EnsureSchema()
    {
        using var command = _connection!.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Sessions (
                Id TEXT PRIMARY KEY,
                OriginalText TEXT NOT NULL,
                OptimizedText TEXT NOT NULL,
                TemplateId TEXT NOT NULL,
                TemplateName TEXT,
                ProviderId TEXT NOT NULL,
                ProviderName TEXT,
                WasTranscribed INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL,
                TokensUsed INTEGER NOT NULL DEFAULT 0,
                ProcessingTimeMs INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS IX_Sessions_CreatedAt ON Sessions(CreatedAt);
            CREATE INDEX IF NOT EXISTS IX_Sessions_TemplateId ON Sessions(TemplateId);

            CREATE TABLE IF NOT EXISTS Memories (
                Id TEXT PRIMARY KEY,
                Type TEXT NOT NULL,
                Label TEXT NOT NULL,
                Data TEXT NOT NULL DEFAULT '{}',
                Embedding BLOB,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                LastAccessedAt TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_Memories_Type ON Memories(Type);
            CREATE INDEX IF NOT EXISTS IX_Memories_UpdatedAt ON Memories(UpdatedAt);
            CREATE INDEX IF NOT EXISTS IX_Memories_LastAccessedAt ON Memories(LastAccessedAt);

            CREATE TABLE IF NOT EXISTS Reminders (
                Id TEXT PRIMARY KEY,
                Description TEXT NOT NULL,
                Recurrence TEXT NOT NULL,
                TimeOfDay TEXT NOT NULL,
                DayOfWeek INTEGER,
                DayOfMonth INTEGER,
                Month INTEGER,
                SpecificDate TEXT,
                NextFireAt TEXT NOT NULL,
                Status TEXT NOT NULL DEFAULT 'Active',
                CreatedAt TEXT NOT NULL,
                LastFiredAt TEXT
            );

            CREATE INDEX IF NOT EXISTS IX_Reminders_NextFireAt ON Reminders(NextFireAt);
            CREATE INDEX IF NOT EXISTS IX_Reminders_Status ON Reminders(Status);

            CREATE TABLE IF NOT EXISTS Todos (
                Id TEXT PRIMARY KEY,
                Title TEXT NOT NULL,
                Notes TEXT,
                Priority INTEGER NOT NULL DEFAULT 1,
                Status INTEGER NOT NULL DEFAULT 0,
                DueDate TEXT,
                LinkedReminderId TEXT,
                CreatedAt TEXT NOT NULL,
                CompletedAt TEXT,
                UpdatedAt TEXT NOT NULL,
                SortOrder INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS IX_Todos_Status ON Todos(Status);
            CREATE INDEX IF NOT EXISTS IX_Todos_Priority ON Todos(Priority);
            CREATE INDEX IF NOT EXISTS IX_Todos_DueDate ON Todos(DueDate);

            CREATE TABLE IF NOT EXISTS KanbanColumns (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                SortOrder INTEGER NOT NULL DEFAULT 0,
                IsDefaultView INTEGER NOT NULL DEFAULT 0,
                IsClosedColumn INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();

        MigrateSchema();
        EnsureMemoriesFts();
    }

    private void MigrateSchema()
    {
        // Add ProcessingTimeMs column if it doesn't exist (for existing databases)
        using var pragma = _connection!.CreateCommand();
        pragma.CommandText = "PRAGMA table_info(Sessions)";
        using var reader = pragma.ExecuteReader();
        var hasProcessingTimeMs = false;
        while (reader.Read())
        {
            if (reader.GetString(1) == "ProcessingTimeMs")
            {
                hasProcessingTimeMs = true;
                break;
            }
        }
        reader.Close();

        if (!hasProcessingTimeMs)
        {
            using var alter = _connection.CreateCommand();
            alter.CommandText = "ALTER TABLE Sessions ADD COLUMN ProcessingTimeMs INTEGER NOT NULL DEFAULT 0";
            alter.ExecuteNonQuery();
        }

        // Add SortOrder column to Todos if it doesn't exist
        using var todoPragma = _connection!.CreateCommand();
        todoPragma.CommandText = "PRAGMA table_info(Todos)";
        using var todoReader = todoPragma.ExecuteReader();
        var hasSortOrder = false;
        while (todoReader.Read())
        {
            if (todoReader.GetString(1) == "SortOrder")
            {
                hasSortOrder = true;
                break;
            }
        }
        todoReader.Close();

        if (!hasSortOrder)
        {
            using var addCol = _connection.CreateCommand();
            addCol.CommandText = "ALTER TABLE Todos ADD COLUMN SortOrder INTEGER NOT NULL DEFAULT 0";
            addCol.ExecuteNonQuery();

            // Backfill sort order from existing priority + creation order
            using var backfill = _connection.CreateCommand();
            backfill.CommandText = """
                UPDATE Todos SET SortOrder = (
                    SELECT COUNT(*) FROM Todos AS t2
                    WHERE t2.Status = Todos.Status
                    AND (t2.Priority > Todos.Priority
                         OR (t2.Priority = Todos.Priority AND t2.CreatedAt < Todos.CreatedAt)
                         OR (t2.Priority = Todos.Priority AND t2.CreatedAt = Todos.CreatedAt AND t2.Id < Todos.Id))
                )
                """;
            backfill.ExecuteNonQuery();
        }

        // Seed default KanbanColumns if table is empty
        using var countCmd = _connection!.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM KanbanColumns";
        var columnCount = Convert.ToInt64(countCmd.ExecuteScalar());

        if (columnCount == 0)
        {
            var now = DateTime.UtcNow.ToString("O");

            using var seedCmd = _connection.CreateCommand();
            seedCmd.CommandText = $"""
                INSERT INTO KanbanColumns (Id, Name, SortOrder, IsDefaultView, IsClosedColumn, CreatedAt, UpdatedAt)
                VALUES ('00000000-0000-0000-0000-000000000001', 'To Do', 0, 1, 0, '{now}', '{now}');

                INSERT INTO KanbanColumns (Id, Name, SortOrder, IsDefaultView, IsClosedColumn, CreatedAt, UpdatedAt)
                VALUES ('00000000-0000-0000-0000-000000000002', 'Closed', 2147483647, 0, 1, '{now}', '{now}');
                """;
            seedCmd.ExecuteNonQuery();
        }

        // Add ColumnId column to Todos if it doesn't exist
        using var columnIdPragma = _connection!.CreateCommand();
        columnIdPragma.CommandText = "PRAGMA table_info(Todos)";
        using var columnIdReader = columnIdPragma.ExecuteReader();
        var hasColumnId = false;
        while (columnIdReader.Read())
        {
            if (columnIdReader.GetString(1) == "ColumnId")
            {
                hasColumnId = true;
                break;
            }
        }
        columnIdReader.Close();

        if (!hasColumnId)
        {
            using var addColumnId = _connection.CreateCommand();
            addColumnId.CommandText = "ALTER TABLE Todos ADD COLUMN ColumnId TEXT";
            addColumnId.ExecuteNonQuery();

            using var backfillPending = _connection.CreateCommand();
            backfillPending.CommandText = "UPDATE Todos SET ColumnId = '00000000-0000-0000-0000-000000000001' WHERE Status = 0 AND ColumnId IS NULL";
            backfillPending.ExecuteNonQuery();

            using var backfillCompleted = _connection.CreateCommand();
            backfillCompleted.CommandText = "UPDATE Todos SET ColumnId = '00000000-0000-0000-0000-000000000002' WHERE Status = 1 AND ColumnId IS NULL";
            backfillCompleted.ExecuteNonQuery();

            using var createIndex = _connection.CreateCommand();
            createIndex.CommandText = "CREATE INDEX IF NOT EXISTS IX_Todos_ColumnId ON Todos(ColumnId)";
            createIndex.ExecuteNonQuery();
        }
    }

    private void EnsureMemoriesFts()
    {
        using var command = _connection!.CreateCommand();
        command.CommandText = """
            CREATE VIRTUAL TABLE IF NOT EXISTS MemoriesFts USING fts5(
                Id UNINDEXED,
                Label,
                Data,
                content='Memories',
                content_rowid='rowid'
            );

            CREATE TRIGGER IF NOT EXISTS Memories_ai AFTER INSERT ON Memories BEGIN
                INSERT INTO MemoriesFts(rowid, Id, Label, Data)
                VALUES (new.rowid, new.Id, new.Label, new.Data);
            END;

            CREATE TRIGGER IF NOT EXISTS Memories_ad AFTER DELETE ON Memories BEGIN
                INSERT INTO MemoriesFts(MemoriesFts, rowid, Id, Label, Data)
                VALUES ('delete', old.rowid, old.Id, old.Label, old.Data);
            END;

            CREATE TRIGGER IF NOT EXISTS Memories_au AFTER UPDATE ON Memories BEGIN
                INSERT INTO MemoriesFts(MemoriesFts, rowid, Id, Label, Data)
                VALUES ('delete', old.rowid, old.Id, old.Label, old.Data);
                INSERT INTO MemoriesFts(rowid, Id, Label, Data)
                VALUES (new.rowid, new.Id, new.Label, new.Data);
            END;
            """;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            _connection?.Dispose();
            _connection = null;
        }

        _disposed = true;
    }
}
