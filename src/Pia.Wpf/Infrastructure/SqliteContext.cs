using System.Diagnostics;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Pia.Infrastructure;

public class SqliteContext : IDisposable
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteContext>? _logger;
    private SqliteConnection? _connection;
    private bool _disposed;

    /// <param name="logger">Optional and defaulted so the sixty-odd hand-constructed test sites stay
    /// source-compatible; DI supplies the real one. Null ⇒ the integrity check still RUNS and still records
    /// <see cref="IntegrityStatus"/>, it just says nothing in the log.</param>
    public SqliteContext(ILogger<SqliteContext>? logger = null)
        : this(DefaultDbPath(), logger)
    {
    }

    /// <summary>
    /// Opens the database at an explicit path. Tests pass a temp file so they
    /// never read or write the user's real history.db.
    /// </summary>
    public SqliteContext(string dbPath, ILogger<SqliteContext>? logger = null)
    {
        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        _connectionString = $"Data Source={dbPath}";
        _logger = logger;
    }

    /// <summary>
    /// The connection string for the shared history database. Exposed so components that must write
    /// from background threads (e.g. Flow persistence) can open their own dedicated connection to the
    /// same file rather than contend on the single shared <see cref="GetConnection"/> connection.
    /// </summary>
    public string ConnectionString => _connectionString;

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
            // T2-13b, and the ORDER of these three is the item. `Open()` does not read page 1, so it succeeds
            // even on a file that is not a database; the FIRST statement that reads the header is the first one
            // that can throw. That statement must be the integrity check, or the one damage class that makes a
            // file unopenable is the only class the diagnostic can never report — see CheckIntegrity.
            ApplyBusyTimeout(_connection);
            CheckIntegrity(_connection);
            ApplyWalJournal(_connection);
            EnsureSchema();
        }
        else if (_connection.State != System.Data.ConnectionState.Open)
        {
            _connection.Open();
        }

        return _connection;
    }

    /// <summary>
    /// The result of this process's one integrity check: <c>"ok"</c>, the FIRST problem SQLite reported
    /// (truncated), or <see langword="null"/> when the check has not run or could not run.
    /// <para>
    /// The support log is the real surface — this property exists so a test can assert the outcome without
    /// parsing a log line, and so a future "your history file is damaged" affordance has something to read that
    /// is not a second full scan.
    /// </para>
    /// </summary>
    public string? IntegrityStatus { get; private set; }

    /// <summary>The one healthy answer <c>PRAGMA integrity_check</c> gives.</summary>
    private const string IntegrityOk = "ok";

    /// <summary>
    /// hermes #13's second half (T2-13b): does this database still open cleanly? Runs exactly once, on the
    /// shared connection's FIRST open.
    /// <para>
    /// WHY BEFORE <see cref="EnsureSchema"/>: the check is READ-ONLY and <see cref="EnsureSchema"/> is not. It
    /// issues DDL, conditional <c>ALTER TABLE</c>s, a seed <c>INSERT</c>, an FTS table drop-and-recreate and a
    /// backfill inside a transaction — so on a damaged file the first thing this process would otherwise do is
    /// WRITE to it, and the diagnosis would arrive as whatever cryptic error the DDL happened to throw.
    /// Diagnose first, then modify. Measured, so the claim is bounded rather than sweeping: on a file with an
    /// INTERIOR page destroyed but a readable header, <c>CREATE TABLE IF NOT EXISTS</c> succeeds and grows the
    /// file — that is the class this ordering protects. On a file whose HEADER is unreadable SQLite refuses to
    /// write at all (the bytes come back identical), so there the ordering buys the diagnosis, not the
    /// integrity of the image.
    /// </para>
    /// <para>
    /// WHY BEFORE <see cref="ApplyWalJournal"/> TOO, which is the sharper half of the same rule and was WRONG in
    /// the first cut of this item: <c>Open()</c> does not read page 1, so the WAL pragma is the first statement
    /// that touches the file, and on a header-damaged database IT throws (error 26/11). With the check
    /// downstream of it, the damage class that makes a file unopenable was the one class this diagnostic could
    /// never report: <see cref="IntegrityStatus"/> stayed null and not one line reached the log. Only
    /// <see cref="ApplyBusyTimeout"/> may precede the check, because it reads nothing.
    /// </para>
    /// <para>
    /// WHY IT NEVER THROWS, and why NO REPAIR IS ATTEMPTED. Throwing here would take the whole app down for a
    /// database whose damage may be one index — today the user keeps every feature that does not touch the
    /// broken page. And SQLite's own remedy for a malformed image is a dump-and-reload, i.e. a decision about
    /// the user's own history that must not be made silently at startup; the one in-place move available,
    /// <c>REINDEX</c>, is a WRITE to a file we have just established we cannot reason about — the exact thing
    /// the ordering above exists to avoid — and it would have to be triggered by string-matching SQLite's
    /// diagnostic prose. So this reports, loudly and once, and leaves the choice to a person.
    /// </para>
    /// <para>
    /// <c>integrity_check(1)</c>, not the bare form: on a healthy database the work is identical (a full scan),
    /// but on a damaged one the analysis quits at the first error instead of walking the whole file to build a
    /// wall of text nobody reads.
    /// </para>
    /// <para>
    /// COST, measured rather than assumed: <b>11.6 ms on a real 1.02 MB <c>history.db</c></b> — and the scan is
    /// linear in FILE SIZE, so roughly 12 ms per MB. That is free at the size this file actually reaches today
    /// and would be SECONDS of startup on a several-hundred-MB profile (the embedding blobs in <c>Memories</c>
    /// and <c>Chunks</c> are what could get it there). If that ever arrives, the cheaper trade is
    /// <c>PRAGMA quick_check</c>, measured at 2.4 ms on the same file: it skips the index-content cross-checks,
    /// i.e. gives up exactly the class of damage most likely to be found here, which is why it is not the
    /// default now.
    /// </para>
    /// </summary>
    private void CheckIntegrity(SqliteConnection connection)
    {
        try
        {
            var started = Stopwatch.GetTimestamp();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check(1);";
            var result = command.ExecuteScalar() as string;
            var elapsedMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            if (string.IsNullOrWhiteSpace(result))
            {
                // No row at all is not "ok" and not a failure either — it is a build/provider surprise, and
                // claiming either would be inventing a fact.
                IntegrityStatus = null;
                _logger?.LogWarning("History database integrity check returned no result");
                return;
            }

            // Structural text only (page and row numbers, table and index names — never row CONTENT), so it is
            // release-loggable; bounded anyway, because a support log is not the place for an unbounded string.
            IntegrityStatus = result.Length <= 200 ? result : result[..200] + "…";

            if (string.Equals(result, IntegrityOk, StringComparison.OrdinalIgnoreCase))
                _logger?.LogInformation("History database integrity check passed in {ElapsedMs} ms", elapsedMs);
            else
                _logger?.LogError(
                    "History database integrity check FAILED after {ElapsedMs} ms: {Problem}. The check does not "
                    + "stop the open; anything that reads the damaged pages may still fail",
                    elapsedMs, IntegrityStatus);
        }
        catch (Exception ex)
        {
            // Including the case the check itself cannot complete on a badly damaged file. Never fail the open:
            // this is a diagnostic, and a diagnostic that can brick the app is worse than no diagnostic.
            IntegrityStatus = null;
            _logger?.LogWarning(ex, "History database integrity check could not run");
        }
    }

    /// <summary>
    /// Set on the shared connection's FIRST open, before anything touches the FILE.
    /// <para>
    /// <c>busy_timeout</c> is PER-CONNECTION and must therefore be set on every handle separately (the
    /// dedicated stores each set their own). Without it here, moving the chat store onto its own connection
    /// would merely convert a swallowed intra-connection <see cref="InvalidOperationException"/> into an
    /// instant SQLITE_BUSY for the ten other services still sharing this connection (TodoService,
    /// MemoryService, ReminderService, ScheduledJobService, KanbanColumnService, PersonaService,
    /// PluginService, HistoryService, VaultIndexer, LintService) — none of which handles it.
    /// </para>
    /// <para>
    /// T2-13b: split out of the WAL pragma and hoisted ABOVE the integrity check on purpose. This one sets a
    /// connection-level timeout and reads nothing off the disk, so it cannot fail on a damaged file — which
    /// makes it the only statement that may safely precede the check, and it is worth preceding it: without a
    /// busy timeout the check would fail instantly against any other process holding the write lock.
    /// </para>
    /// </summary>
    private static void ApplyBusyTimeout(SqliteConnection connection)
    {
        using var busy = connection.CreateCommand();
        busy.CommandText = "PRAGMA busy_timeout=3000;";
        busy.ExecuteNonQuery();
    }

    /// <summary>
    /// <c>journal_mode=WAL</c> is a PERSISTENT PER-FILE setting, so applying it where the file is first
    /// opened also covers every dedicated connection to the same file (<c>AgentRunService</c>,
    /// <c>FlowPersistenceStore</c>, <c>IngestStateStore</c>, <c>AssistantChatService</c>). It is what makes a
    /// second writer survivable at all: in the default rollback-journal mode a write transaction holds
    /// RESERVED from its first write and EXCLUSIVE through COMMIT, so any write from another connection
    /// during that window fails IMMEDIATELY with "database is locked" — and WAL additionally lets this
    /// connection's READERS proceed while another connection's writer is mid-transaction.
    /// <para>
    /// T2-13b: this is the first statement of the open sequence that READS the file, which is why the
    /// integrity check now runs before it rather than after. Measured, on Microsoft.Data.Sqlite 10.0.9: on a
    /// file whose header is unreadable (a truncated restore, a sync-conflict stub) <c>Open()</c> still
    /// succeeds and THIS pragma is what throws — SQLite error 26 "file is not a database" or 11 "database disk
    /// image is malformed". With the check downstream of it, that whole damage class produced no verdict and
    /// no log line at all.
    /// </para>
    /// </summary>
    private static void ApplyWalJournal(SqliteConnection connection)
    {
        using var journal = connection.CreateCommand();
        journal.CommandText = "PRAGMA journal_mode=WAL;";
        journal.ExecuteNonQuery();
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

            CREATE TABLE IF NOT EXISTS Chunks (
                FilePath TEXT NOT NULL,
                Heading  TEXT NOT NULL,
                Slug     TEXT NOT NULL,
                ContentHash TEXT NOT NULL,
                Embedding BLOB,
                IndexedAt TEXT NOT NULL,
                PRIMARY KEY (FilePath, Slug)
            );
            CREATE INDEX IF NOT EXISTS IX_Chunks_FilePath ON Chunks(FilePath);

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
                OwnerDeviceId TEXT NULL,
                GrantedTools TEXT NOT NULL DEFAULT '[]',
                -- T2-18 quiet mode. Device-local like the three execution-state columns above it: absent from
                -- SyncScheduledJob and from UpsertFromSyncAsync's SET list, so a pull cannot reset it.
                QuietOnSuccess INTEGER NOT NULL DEFAULT 0
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
                OutputFormat TEXT,
                ModelType TEXT
            );

            CREATE INDEX IF NOT EXISTS IX_Personas_UpdatedAt ON Personas(UpdatedAt);

            -- Admin-authored personas pulled read-only via the sync pull's managedPersonas channel.
            -- A SEPARATE table rather than a flag column on Personas: Personas is the PUSH SOURCE
            -- (SyncClientService reads it to build personas.upserted), so a managed row living there is
            -- exactly the shadow-copy hazard the server quarantines against. Apart, "never push a managed
            -- row" is structural instead of a filter someone can forget.
            -- The column list and types are DELIBERATELY identical to Personas so PersonaService's
            -- existing MapPersona reader and AddPersonaParameters writer are reused with only the table
            -- name changed. That reuse is why PreferredProviderId stays here even though the wire DTO has
            -- no such field: keeping the column shape identical is worth more than dropping one column
            -- that is simply always NULL for managed rows, so the reader/writer never special-case it.
            -- No index, unlike IX_Personas_UpdatedAt: the table is tiny (server caps it at 25 per group)
            -- and is only ever read whole or by primary key.
            CREATE TABLE IF NOT EXISTS ManagedPersonas (
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
                OutputFormat TEXT,
                ModelType TEXT
            );

            CREATE TABLE IF NOT EXISTS AssistantChats (
                Id              TEXT PRIMARY KEY,
                SchemaVersion   INTEGER NOT NULL DEFAULT 1,
                Title           TEXT,
                CreatedAt       TEXT NOT NULL,
                UpdatedAt       TEXT NOT NULL,
                LastAccessedAt  TEXT NOT NULL,
                WindowMode      TEXT NOT NULL,
                ProviderId      TEXT,
                WorkingDirectory TEXT,
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

            CREATE TABLE IF NOT EXISTS AgentRuns (
                Id                  TEXT PRIMARY KEY,
                SchemaVersion       INTEGER NOT NULL DEFAULT 1,
                ChatId              TEXT    NOT NULL,
                RunShape            INTEGER NOT NULL,
                State               INTEGER NOT NULL,
                TriggerKind         INTEGER NOT NULL,
                TriggerRef          TEXT    NULL,
                -- ParentRunId is deliberately NOT a self-referencing foreign key. The cascade that exists is
                -- AssistantChats → AgentRuns per chat (below), and a child run lives in its OWN chat: an
                -- ON DELETE CASCADE here would delete a child's whole run history the moment the PARENT's chat
                -- was deleted, while a non-cascading FK would make that delete throw from inside a swallowing
                -- Safe* wrapper. A dangling ParentRunId is the correct outcome — same reasoning, and same house
                -- precedent, as AgentTimelineEvents.StepId below.
                ParentRunId         TEXT    NULL,
                OwnerDeviceId       TEXT    NULL,
                Goal                TEXT    NULL,
                FirstMessageId      TEXT    NULL,
                LastMessageId       TEXT    NULL,
                PolicyJson          TEXT    NULL,
                LedgerJson          TEXT    NULL,
                -- Answers to this run's clarification questions, as a JSON array of strings. Not part of
                -- ExtraJson because both resume claims SET ExtraJson=NULL, which would destroy an answer kept there.
                ClarificationsJson  TEXT    NULL,
                CreatedAt           TEXT    NOT NULL,
                UpdatedAt           TEXT    NOT NULL,
                StartedAt           TEXT    NULL,
                CompletedAt         TEXT    NULL,
                ExtraJson           TEXT    NULL,
                FOREIGN KEY (ChatId) REFERENCES AssistantChats(Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_AgentRuns_ChatId     ON AgentRuns(ChatId);
            CREATE INDEX IF NOT EXISTS IX_AgentRuns_State      ON AgentRuns(State);
            CREATE INDEX IF NOT EXISTS IX_AgentRuns_UpdatedAt  ON AgentRuns(UpdatedAt);
            CREATE INDEX IF NOT EXISTS IX_AgentRuns_TriggerRef ON AgentRuns(TriggerRef);
            -- "Which children is this parent still waiting on" is a query, not a counter on the parent row:
            -- the child ROWS are the marker, so they need an index on the link. This block re-runs on EVERY
            -- open, so an existing database gets the index at next launch with no MigrateSchema entry.
            CREATE INDEX IF NOT EXISTS IX_AgentRuns_ParentRunId ON AgentRuns(ParentRunId);

            CREATE TABLE IF NOT EXISTS AgentSteps (
                Id                  TEXT PRIMARY KEY,
                RunId               TEXT    NOT NULL,
                Ordinal             INTEGER NOT NULL,
                Title               TEXT    NOT NULL,
                Intent              TEXT    NULL,
                Status              INTEGER NOT NULL,
                ExpectedArtifact    TEXT    NULL,
                AssignedPersonaId   TEXT    NULL,
                DependsOnJson       TEXT    NULL,
                ReRunnable          INTEGER NOT NULL DEFAULT 1,
                FirstMessageId      TEXT    NULL,
                LastMessageId       TEXT    NULL,
                CreatedAt           TEXT    NOT NULL,
                UpdatedAt           TEXT    NOT NULL,
                ExtraJson           TEXT    NULL,
                FOREIGN KEY (RunId) REFERENCES AgentRuns(Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_AgentSteps_RunId ON AgentSteps(RunId, Ordinal);

            CREATE TABLE IF NOT EXISTS AgentTimelineEvents (
                Id                  TEXT PRIMARY KEY,
                SchemaVersion       INTEGER NOT NULL DEFAULT 1,
                RunId               TEXT    NOT NULL,
                -- StepId is deliberately NOT a foreign key. ReplaceStepsAsync DELETEs every AgentSteps row
                -- for the run and re-inserts on EVERY replan, keeping only the Done ones: a CASCADE would
                -- wipe the audit trail of the steps that already ran, and a non-cascading FK would make that
                -- DELETE throw into a swallowing Safe* wrapper, leaving the run executing a stale plan. A
                -- dangling StepId is the correct outcome here — the trail outlives the plan row it names.
                StepId              TEXT    NULL,
                -- Monotonic per RUN, allocated in memory at emit time. NOT a timestamp: DateTime.UtcNow has
                -- ~1 ms resolution on Windows and several tool calls in one round finish faster than that.
                Seq                 INTEGER NOT NULL,
                Kind                INTEGER NOT NULL,
                Surface             INTEGER NOT NULL,
                Decision            INTEGER NOT NULL,
                Outcome             INTEGER NOT NULL,
                ToolName            TEXT    NOT NULL,
                ToolClass           INTEGER NOT NULL,
                PluginId            TEXT    NULL,
                -- METADATA ONLY (03 §3): lengths, never content. No args, no results, no paths, no hashes,
                -- and deliberately no ExtraJson — a free-text column on an audit table is where payloads go
                -- to hide, so the column list is asserted exactly by a test rather than left to review.
                ArgsChars           INTEGER NULL,
                ResultChars         INTEGER NULL,
                DurationMs          INTEGER NULL,
                CreatedAt           TEXT    NOT NULL,
                -- ---- gated-call correlation (T2-14). ALL FIVE NULLABLE: an existing user's rows keep every
                -- value they had and read back NULL here, which SchemaVersion disambiguates (a v1 row never
                -- recorded these; a v2 row with NULL genuinely had none). Still METADATA ONLY.
                -- Provider-side correlation token for one gated call (FunctionCallContent.CallId), so a row
                -- lines up with the provider round-trip in a log. Shape- and length-bounded by
                -- AgentTimelineScope.SanitizeCallId (tool-identifier charset, 128 chars) on every arm, which is
                -- what keeps an argument, a path or a JSON blob out of it — not a promise that the value is
                -- unreadable. NULL when the provider gave none.
                ToolCallId          TEXT    NULL,
                -- The provider tool-loop counter (1-based, as every log line in that loop prints it), carried
                -- from AiClientService through the tool-handler dispatch context. NULL on the synthetic
                -- truncation marker, which belongs to no round.
                Round               INTEGER NULL,
                -- Monotonic per STEP, allocated in memory in the same critical section as Seq (which is per
                -- RUN). NULL when StepId is NULL (run-level turn, truncation marker): an ordinal without a
                -- step would invent one, and Seq already orders those rows.
                StepOrdinal         INTEGER NULL,
                -- The instant the authorization question was posed (policy consulted, or the card shown to a
                -- human) and the instant it was answered. Timestamps CARRIED to the single write-once row,
                -- never a second row. DecidedAt is NULL while a decision is genuinely still pending (the
                -- unattended ParkedForApproval row); both are NULL on the unrouted arm, which asked no gate.
                RequestedAt         TEXT    NULL,
                DecidedAt           TEXT    NULL,
                FOREIGN KEY (RunId) REFERENCES AgentRuns(Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_AgentTimelineEvents_RunId     ON AgentTimelineEvents(RunId, Seq);
            CREATE INDEX IF NOT EXISTS IX_AgentTimelineEvents_CreatedAt ON AgentTimelineEvents(CreatedAt);
            """;
        command.ExecuteNonQuery();

        MigrateSchema();
        EnsureMemoriesFts();
        EnsureChunksFts();
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

        // (Legacy ResearchSessions column/index migrations removed — the table is dropped below
        // now that research results are persisted as assistant chats.)

        // Add UpdatedAt and OwnerDeviceId to ScheduledJobs for sync, and GrantedTools for the
        // per-job background-turn tool policy.
        var hasJobUpdatedAt = false;
        var hasOwnerDeviceId = false;
        var hasGrantedTools = false;
        var hasQuietOnSuccess = false;
        using (var p = _connection!.CreateCommand())
        {
            p.CommandText = "PRAGMA table_info(ScheduledJobs)";
            using var r = p.ExecuteReader();
            while (r.Read())
            {
                var col = r.GetString(1);
                if (col == "UpdatedAt") hasJobUpdatedAt = true;
                else if (col == "OwnerDeviceId") hasOwnerDeviceId = true;
                else if (col == "GrantedTools") hasGrantedTools = true;
                else if (col == "QuietOnSuccess") hasQuietOnSuccess = true;
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
        if (!hasGrantedTools)
        {
            using var addCol = _connection.CreateCommand();
            addCol.CommandText = "ALTER TABLE ScheduledJobs ADD COLUMN GrantedTools TEXT NOT NULL DEFAULT '[]'";
            addCol.ExecuteNonQuery();
        }
        if (!hasQuietOnSuccess)
        {
            // T2-18: DEFAULT 0, so every job an existing profile already has keeps notifying — quiet mode is
            // something a person turns on, never something a migration decides for them.
            using var addCol = _connection.CreateCommand();
            addCol.CommandText = "ALTER TABLE ScheduledJobs ADD COLUMN QuietOnSuccess INTEGER NOT NULL DEFAULT 0";
            addCol.ExecuteNonQuery();
        }

        // The research view was removed; research results are now assistant chats. Drop the
        // legacy standalone research store (data was never user-facing outside that view).
        using (var dropResearch = _connection.CreateCommand())
        {
            dropResearch.CommandText = "DROP TABLE IF EXISTS ResearchSessions";
            dropResearch.ExecuteNonQuery();
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
                    OutputFormat TEXT,
                    ModelType TEXT
                );

                CREATE INDEX IF NOT EXISTS IX_Personas_UpdatedAt ON Personas(UpdatedAt);
                """;
            createPersonas.ExecuteNonQuery();
        }

        // Create the ManagedPersonas table for databases that predate it. This is how an existing profile
        // gains the table on startup WITHOUT touching Personas — the two stores stay independent because
        // Personas is the push source and a managed row must never end up in it. Same defensive
        // presence-check idiom as above; EnsureSchema already creates it on fresh installs.
        var hasManagedPersonasTable = false;
        using (var p = _connection!.CreateCommand())
        {
            p.CommandText = "PRAGMA table_info(ManagedPersonas)";
            using var r = p.ExecuteReader();
            if (r.Read()) hasManagedPersonasTable = true;
        }
        if (!hasManagedPersonasTable)
        {
            using var createManagedPersonas = _connection.CreateCommand();
            createManagedPersonas.CommandText = """
                CREATE TABLE IF NOT EXISTS ManagedPersonas (
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
                    OutputFormat TEXT,
                    ModelType TEXT
                );
                """;
            createManagedPersonas.ExecuteNonQuery();
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

        // Persona model-type routing hint, added after the persona tables shipped. Both persona tables
        // stay column-identical (PersonaService shares one reader/writer across them), so one PRAGMA
        // pass per table, same idiom as OutputFormat above.
        foreach (var personaTable in new[] { "Personas", "ManagedPersonas" })
        {
            var hasModelType = false;
            using (var p = _connection!.CreateCommand())
            {
                p.CommandText = $"PRAGMA table_info({personaTable})";
                using var r = p.ExecuteReader();
                while (r.Read())
                {
                    if (r.GetString(1) == "ModelType") { hasModelType = true; break; }
                }
            }
            if (!hasModelType)
            {
                using var addCol = _connection.CreateCommand();
                addCol.CommandText = $"ALTER TABLE {personaTable} ADD COLUMN ModelType TEXT";
                addCol.ExecuteNonQuery();
            }

            // Backfill rows that predate the default: every persona routes with at least "general".
            // Idempotent and cheap (both tables are tiny), so it runs on every startup rather than
            // being gated on the ALTER above — a row can also arrive blank via an older client's push.
            using (var backfill = _connection.CreateCommand())
            {
                backfill.CommandText =
                    $"UPDATE {personaTable} SET ModelType = 'general' WHERE ModelType IS NULL OR TRIM(ModelType) = ''";
                backfill.ExecuteNonQuery();
            }
        }

        // Per-chat working directory (relative to the assistant-files sandbox root), added after
        // AssistantChats shipped. Fresh tables already include the column via CREATE TABLE above,
        // so the PRAGMA check short-circuits and no ALTER is issued.
        var hasWorkingDirectory = false;
        using (var p = _connection!.CreateCommand())
        {
            p.CommandText = "PRAGMA table_info(AssistantChats)";
            using var r = p.ExecuteReader();
            while (r.Read())
            {
                if (r.GetString(1) == "WorkingDirectory") { hasWorkingDirectory = true; break; }
            }
        }
        if (!hasWorkingDirectory)
        {
            using var addCol = _connection.CreateCommand();
            addCol.CommandText = "ALTER TABLE AssistantChats ADD COLUMN WorkingDirectory TEXT";
            addCol.ExecuteNonQuery();
        }

        // AgentTimelineEvents gained gated-call correlation columns (T2-14). Fresh databases already have them
        // from the CREATE TABLE above, so these PRAGMA checks short-circuit and no ALTER is issued. All five
        // are NULLABLE with no default: an existing user's rows keep every value they had, and a NULL means
        // "this build did not record it" (a SchemaVersion 1 row) rather than a lost fact. One PRAGMA pass for
        // all five rather than five passes, because they always arrive together.
        var hasToolCallId = false;
        var hasRound = false;
        var hasStepOrdinal = false;
        var hasRequestedAt = false;
        var hasDecidedAt = false;
        using (var p = _connection!.CreateCommand())
        {
            p.CommandText = "PRAGMA table_info(AgentTimelineEvents)";
            using var r = p.ExecuteReader();
            while (r.Read())
            {
                switch (r.GetString(1))
                {
                    case "ToolCallId": hasToolCallId = true; break;
                    case "Round": hasRound = true; break;
                    case "StepOrdinal": hasStepOrdinal = true; break;
                    case "RequestedAt": hasRequestedAt = true; break;
                    case "DecidedAt": hasDecidedAt = true; break;
                }
            }
        }
        if (!hasToolCallId)
        {
            using var addCol = _connection.CreateCommand();
            addCol.CommandText = "ALTER TABLE AgentTimelineEvents ADD COLUMN ToolCallId TEXT NULL";
            addCol.ExecuteNonQuery();
        }
        if (!hasRound)
        {
            using var addCol = _connection.CreateCommand();
            addCol.CommandText = "ALTER TABLE AgentTimelineEvents ADD COLUMN Round INTEGER NULL";
            addCol.ExecuteNonQuery();
        }
        if (!hasStepOrdinal)
        {
            using var addCol = _connection.CreateCommand();
            addCol.CommandText = "ALTER TABLE AgentTimelineEvents ADD COLUMN StepOrdinal INTEGER NULL";
            addCol.ExecuteNonQuery();
        }
        if (!hasRequestedAt)
        {
            using var addCol = _connection.CreateCommand();
            addCol.CommandText = "ALTER TABLE AgentTimelineEvents ADD COLUMN RequestedAt TEXT NULL";
            addCol.ExecuteNonQuery();
        }
        if (!hasDecidedAt)
        {
            using var addCol = _connection.CreateCommand();
            addCol.CommandText = "ALTER TABLE AgentTimelineEvents ADD COLUMN DecidedAt TEXT NULL";
            addCol.ExecuteNonQuery();
        }

        // Fresh databases already have ClarificationsJson from the CREATE TABLE above, so this short-circuits.
        var hasClarificationsJson = false;
        using (var p = _connection!.CreateCommand())
        {
            p.CommandText = "PRAGMA table_info(AgentRuns)";
            using var r = p.ExecuteReader();
            while (r.Read())
            {
                if (r.GetString(1) == "ClarificationsJson") { hasClarificationsJson = true; break; }
            }
        }
        if (!hasClarificationsJson)
        {
            using var addCol = _connection.CreateCommand();
            addCol.CommandText = "ALTER TABLE AgentRuns ADD COLUMN ClarificationsJson TEXT NULL";
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

    private void EnsureChunksFts()
    {
        // Contentless FTS5 over vault chunk bodies: bodies are NOT stored, only
        // indexed. The indexer manages rows explicitly (INSERT with an explicit
        // rowid equal to the Chunks rowid so matches map back), so there are no
        // triggers — mirroring the AssistantChatsFts style.
        using var command = _connection!.CreateCommand();
        command.CommandText = """
            CREATE VIRTUAL TABLE IF NOT EXISTS ChunksFts USING fts5(
                FilePath UNINDEXED,
                Heading,
                Body,
                content='',
                contentless_delete=1
            );
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
