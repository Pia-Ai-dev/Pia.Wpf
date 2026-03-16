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
                UpdatedAt TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_Todos_Status ON Todos(Status);
            CREATE INDEX IF NOT EXISTS IX_Todos_Priority ON Todos(Priority);
            CREATE INDEX IF NOT EXISTS IX_Todos_DueDate ON Todos(DueDate);

            CREATE TABLE IF NOT EXISTS ResearchSessions (
                Id TEXT PRIMARY KEY,
                Query TEXT NOT NULL,
                SynthesizedResult TEXT NOT NULL DEFAULT '',
                StepsJson TEXT NOT NULL DEFAULT '[]',
                ProviderId TEXT NOT NULL,
                ProviderName TEXT,
                Status TEXT NOT NULL DEFAULT 'Completed',
                StepCount INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL,
                CompletedAt TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_ResearchSessions_CreatedAt ON ResearchSessions(CreatedAt);
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
