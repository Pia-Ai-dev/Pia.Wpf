using System.IO;
using Microsoft.Data.Sqlite;

namespace Pia.Infrastructure;

public class SqliteContext : IDisposable
{
    private readonly string _connectionString;
    private SqliteConnection? _connection;
    private bool _disposed;

    public SqliteContext()
        : this(DefaultDbPath())
    {
    }

    /// <summary>
    /// Opens the database at an explicit path. Tests pass a temp file so they
    /// never read or write the user's real history.db.
    /// </summary>
    public SqliteContext(string dbPath)
    {
        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        _connectionString = $"Data Source={dbPath}";
    }

    private static string DefaultDbPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dbDirectory = Path.Combine(localAppData, "Pia");
        return Path.Combine(dbDirectory, "history.db");
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
                CompletedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL DEFAULT ''
            );

            CREATE INDEX IF NOT EXISTS IX_ResearchSessions_CreatedAt ON ResearchSessions(CreatedAt);

            CREATE TABLE IF NOT EXISTS KanbanColumns (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                SortOrder INTEGER NOT NULL DEFAULT 0,
                IsDefaultView INTEGER NOT NULL DEFAULT 0,
                IsClosedColumn INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Plugins (
                Id TEXT PRIMARY KEY,
                Kind TEXT NOT NULL,
                Name TEXT NOT NULL,
                Description TEXT,
                IconUrl TEXT,
                ConfigJson TEXT NOT NULL DEFAULT '{}',
                Version TEXT NOT NULL DEFAULT '1.0.0',
                IsPreloaded INTEGER NOT NULL DEFAULT 0,
                IsActive INTEGER NOT NULL DEFAULT 1,
                UserEnabled INTEGER,
                UpdatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS ScheduledJobs (
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
                OwnerDeviceId TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_ScheduledJobs_NextFireAt ON ScheduledJobs(NextFireAt, Status);

            CREATE TABLE IF NOT EXISTS Personas (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                Tagline TEXT,
                SystemPrompt TEXT NOT NULL,
                Guardrails TEXT,
                Archetype TEXT,
                Expertise TEXT,
                Emoji TEXT,
                AccentColor TEXT,
                ToolScope INTEGER NOT NULL DEFAULT 2,
                PreferredProviderId TEXT,
                ReasoningEffort INTEGER,
                SchemaVersion INTEGER NOT NULL DEFAULT 1,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                OutputFormat TEXT
            );

            CREATE INDEX IF NOT EXISTS IX_Personas_UpdatedAt ON Personas(UpdatedAt);

            CREATE TABLE IF NOT EXISTS AssistantChats (
                Id              TEXT PRIMARY KEY,
                SchemaVersion   INTEGER NOT NULL DEFAULT 1,
                Title           TEXT,
                CreatedAt       TEXT NOT NULL,
                UpdatedAt       TEXT NOT NULL,
                LastAccessedAt  TEXT NOT NULL,
                WindowMode      TEXT NOT NULL,
                ProviderId      TEXT,
                ExtraJson       TEXT
            );

            CREATE INDEX IF NOT EXISTS IX_AssistantChats_UpdatedAt
                ON AssistantChats(UpdatedAt);
            CREATE INDEX IF NOT EXISTS IX_AssistantChats_LastAccessedAt
                ON AssistantChats(LastAccessedAt);

            CREATE TABLE IF NOT EXISTS AssistantChatMessages (
                Id              TEXT PRIMARY KEY,
                ChatId          TEXT NOT NULL,
                Ordinal         INTEGER NOT NULL,
                Role            TEXT NOT NULL,
                Content         TEXT NOT NULL,
                ThinkingContent TEXT,
                Timestamp       TEXT NOT NULL,
                Tokens          INTEGER,
                ModelName       TEXT,
                PersonaId       TEXT,
                PersonaName     TEXT,
                PersonaEmoji    TEXT,
                FOREIGN KEY (ChatId) REFERENCES AssistantChats(Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_AssistantChatMessages_ChatId_Ordinal
                ON AssistantChatMessages(ChatId, Ordinal);
            """;
        command.ExecuteNonQuery();

        MigrateSchema();
        EnsureMemoriesFts();
        EnsureAssistantChatsFts();
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

        // Add ScheduledJobId and Embedding columns to ResearchSessions if they don't exist
        using var rsPragma = _connection!.CreateCommand();
        rsPragma.CommandText = "PRAGMA table_info(ResearchSessions)";
        using var rsReader = rsPragma.ExecuteReader();
        var hasScheduledJobId = false;
        var hasEmbedding = false;
        while (rsReader.Read())
        {
            var columnName = rsReader.GetString(1);
            if (columnName == "ScheduledJobId") hasScheduledJobId = true;
            else if (columnName == "Embedding") hasEmbedding = true;
        }
        rsReader.Close();

        if (!hasScheduledJobId)
        {
            using var addCol = _connection.CreateCommand();
            addCol.CommandText = "ALTER TABLE ResearchSessions ADD COLUMN ScheduledJobId TEXT NULL";
            addCol.ExecuteNonQuery();

            using var addIdx = _connection.CreateCommand();
            addIdx.CommandText = "CREATE INDEX IF NOT EXISTS IX_ResearchSessions_ScheduledJobId ON ResearchSessions(ScheduledJobId)";
            addIdx.ExecuteNonQuery();
        }

        if (!hasEmbedding)
        {
            using var addEmb = _connection.CreateCommand();
            addEmb.CommandText = "ALTER TABLE ResearchSessions ADD COLUMN Embedding BLOB NULL";
            addEmb.ExecuteNonQuery();
        }

        // Add UpdatedAt to ResearchSessions for sync dirty-tracking; backfill from CreatedAt
        var hasResearchUpdatedAt = false;
        using (var p = _connection!.CreateCommand())
        {
            p.CommandText = "PRAGMA table_info(ResearchSessions)";
            using var r = p.ExecuteReader();
            while (r.Read())
                if (r.GetString(1) == "UpdatedAt") { hasResearchUpdatedAt = true; break; }
        }
        if (!hasResearchUpdatedAt)
        {
            using var addCol = _connection.CreateCommand();
            addCol.CommandText = "ALTER TABLE ResearchSessions ADD COLUMN UpdatedAt TEXT NOT NULL DEFAULT ''";
            addCol.ExecuteNonQuery();
            using var backfill = _connection.CreateCommand();
            backfill.CommandText = "UPDATE ResearchSessions SET UpdatedAt = CreatedAt WHERE UpdatedAt = ''";
            backfill.ExecuteNonQuery();
        }
        // Ensure index exists for both fresh installs and migrated databases.
        using (var idx = _connection.CreateCommand())
        {
            idx.CommandText = "CREATE INDEX IF NOT EXISTS IX_ResearchSessions_UpdatedAt ON ResearchSessions(UpdatedAt)";
            idx.ExecuteNonQuery();
        }

        // Add UpdatedAt and OwnerDeviceId to ScheduledJobs for sync
        var hasJobUpdatedAt = false;
        var hasOwnerDeviceId = false;
        using (var p = _connection!.CreateCommand())
        {
            p.CommandText = "PRAGMA table_info(ScheduledJobs)";
            using var r = p.ExecuteReader();
            while (r.Read())
            {
                var col = r.GetString(1);
                if (col == "UpdatedAt") hasJobUpdatedAt = true;
                else if (col == "OwnerDeviceId") hasOwnerDeviceId = true;
            }
        }
        if (!hasJobUpdatedAt)
        {
            using var addCol = _connection.CreateCommand();
            addCol.CommandText = "ALTER TABLE ScheduledJobs ADD COLUMN UpdatedAt TEXT NOT NULL DEFAULT ''";
            addCol.ExecuteNonQuery();
            using var backfill = _connection.CreateCommand();
            backfill.CommandText = "UPDATE ScheduledJobs SET UpdatedAt = CreatedAt WHERE UpdatedAt = ''";
            backfill.ExecuteNonQuery();
        }
        if (!hasOwnerDeviceId)
        {
            using var addCol = _connection.CreateCommand();
            addCol.CommandText = "ALTER TABLE ScheduledJobs ADD COLUMN OwnerDeviceId TEXT NULL";
            addCol.ExecuteNonQuery();
        }
        using (var idx = _connection.CreateCommand())
        {
            idx.CommandText = """
                CREATE INDEX IF NOT EXISTS IX_ScheduledJobs_UpdatedAt ON ScheduledJobs(UpdatedAt);
                CREATE INDEX IF NOT EXISTS IX_ScheduledJobs_OwnerDeviceId ON ScheduledJobs(OwnerDeviceId);
                """;
            idx.ExecuteNonQuery();
        }

        // Persona attribution snapshot on assistant messages.
        var hasPersonaId = false;
        var hasPersonaName = false;
        var hasPersonaEmoji = false;
        using (var p = _connection!.CreateCommand())
        {
            p.CommandText = "PRAGMA table_info(AssistantChatMessages)";
            using var r = p.ExecuteReader();
            while (r.Read())
            {
                var col = r.GetString(1);
                if (col == "PersonaId") hasPersonaId = true;
                else if (col == "PersonaName") hasPersonaName = true;
                else if (col == "PersonaEmoji") hasPersonaEmoji = true;
            }
        }
        if (!hasPersonaId)
        {
            using var addCol = _connection.CreateCommand();
            addCol.CommandText = "ALTER TABLE AssistantChatMessages ADD COLUMN PersonaId TEXT";
            addCol.ExecuteNonQuery();
        }
        if (!hasPersonaName)
        {
            using var addCol = _connection.CreateCommand();
            addCol.CommandText = "ALTER TABLE AssistantChatMessages ADD COLUMN PersonaName TEXT";
            addCol.ExecuteNonQuery();
        }
        if (!hasPersonaEmoji)
        {
            using var addCol = _connection.CreateCommand();
            addCol.CommandText = "ALTER TABLE AssistantChatMessages ADD COLUMN PersonaEmoji TEXT";
            addCol.ExecuteNonQuery();
        }

        // Create the Personas table for databases that predate it (only user personas are stored;
        // built-ins are merged in-memory by PersonaService). EnsureSchema already creates it on
        // fresh installs via CREATE TABLE IF NOT EXISTS — this is a defensive presence check.
        var hasPersonasTable = false;
        using (var p = _connection!.CreateCommand())
        {
            p.CommandText = "PRAGMA table_info(Personas)";
            using var r = p.ExecuteReader();
            if (r.Read()) hasPersonasTable = true;
        }
        if (!hasPersonasTable)
        {
            using var createPersonas = _connection.CreateCommand();
            createPersonas.CommandText = """
                CREATE TABLE IF NOT EXISTS Personas (
                    Id TEXT PRIMARY KEY,
                    Name TEXT NOT NULL,
                    Tagline TEXT,
                    SystemPrompt TEXT NOT NULL,
                    Guardrails TEXT,
                    Archetype TEXT,
                    Expertise TEXT,
                    Emoji TEXT,
                    AccentColor TEXT,
                    ToolScope INTEGER NOT NULL DEFAULT 2,
                    PreferredProviderId TEXT,
                    ReasoningEffort INTEGER,
                    SchemaVersion INTEGER NOT NULL DEFAULT 1,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    OutputFormat TEXT
                );

                CREATE INDEX IF NOT EXISTS IX_Personas_UpdatedAt ON Personas(UpdatedAt);
                """;
            createPersonas.ExecuteNonQuery();
        }

        // Per-persona output-format guidance, added after the Personas table shipped. Runs after the
        // defensive create above, so the table is guaranteed to exist (fresh tables already include
        // the column, so the check below short-circuits and no ALTER is issued).
        var hasOutputFormat = false;
        using (var p = _connection!.CreateCommand())
        {
            p.CommandText = "PRAGMA table_info(Personas)";
            using var r = p.ExecuteReader();
            while (r.Read())
            {
                if (r.GetString(1) == "OutputFormat") { hasOutputFormat = true; break; }
            }
        }
        if (!hasOutputFormat)
        {
            using var addCol = _connection.CreateCommand();
            addCol.CommandText = "ALTER TABLE Personas ADD COLUMN OutputFormat TEXT";
            addCol.ExecuteNonQuery();
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

    private void EnsureAssistantChatsFts()
    {
        // FTS5 over both Chats (title) and ChatMessages (body). The service
        // manages rows explicitly on save/delete — no triggers, because a
        // single FTS row represents an aggregated chat document.
        //
        // The previous schema set content='' (contentless), which silently
        // dropped column values: SELECT ChatId FROM ... WHERE MATCH ...
        // then returned NULLs and the outer WHERE Id IN (...) never matched.
        // Detect and rebuild that old table; the service backfills on first
        // SaveAsync, and on startup we re-index any chats that lost their
        // FTS row in the drop.
        using (var existing = _connection!.CreateCommand())
        {
            existing.CommandText = "SELECT sql FROM sqlite_master WHERE name = 'AssistantChatsFts'";
            var sql = existing.ExecuteScalar() as string;
            if (sql is not null && sql.Contains("content=''", StringComparison.Ordinal))
            {
                using var drop = _connection.CreateCommand();
                drop.CommandText = "DROP TABLE AssistantChatsFts";
                drop.ExecuteNonQuery();
            }
        }

        using var create = _connection.CreateCommand();
        create.CommandText = """
            CREATE VIRTUAL TABLE IF NOT EXISTS AssistantChatsFts USING fts5(
                ChatId UNINDEXED,
                Title,
                Body
            );
            """;
        create.ExecuteNonQuery();

        BackfillAssistantChatsFts();
    }

    private void BackfillAssistantChatsFts()
    {
        using var count = _connection!.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM AssistantChatsFts";
        var ftsRows = Convert.ToInt32(count.ExecuteScalar());
        if (ftsRows > 0) return;

        using var hasChats = _connection.CreateCommand();
        hasChats.CommandText = "SELECT COUNT(*) FROM AssistantChats";
        var chatRows = Convert.ToInt32(hasChats.ExecuteScalar());
        if (chatRows == 0) return;

        using var transaction = _connection.BeginTransaction();
        using var insert = _connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO AssistantChatsFts (ChatId, Title, Body)
            SELECT
                c.Id,
                COALESCE(c.Title, ''),
                COALESCE((SELECT GROUP_CONCAT(m.Content, char(10) || char(10))
                          FROM AssistantChatMessages m
                          WHERE m.ChatId = c.Id), '')
            FROM AssistantChats c;
            """;
        insert.ExecuteNonQuery();
        transaction.Commit();
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
