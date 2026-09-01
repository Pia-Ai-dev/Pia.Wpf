using System.IO;
using Microsoft.Data.Sqlite;
using Pia.Infrastructure;
using Pia.Services;
using Pia.Shared.Models;
using Pia.Tests.TestInfrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Pia.Tests.Infrastructure;

/// <summary>
/// Verifies the PRAGMA-detect migration that adds <c>AssistantChatMessages.AttachedFiles</c> to databases
/// that predate the column: the column is added, an existing message survives with no chips, and a chat
/// saved afterwards round-trips its attachments. This is the one path where a mistake corrupts history a
/// user already has.
/// </summary>
public class AssistantChatMessagesAttachedFilesMigrationTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string _dbPath;

    public AssistantChatMessagesAttachedFilesMigrationTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "PiaAttachMigrationTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        _dbPath = Path.Combine(_tmpDir, "history.db");
    }

    public void Dispose()
    {
        SqlitePool.ClearFor($"Data Source={_dbPath}");
        TempPath.Remove(_tmpDir);
    }

    private void SeedOldSchema(Guid chatId, Guid messageId)
    {
        var now = DateTime.UtcNow.ToString("O");
        using var seed = new SqliteConnection($"Data Source={_dbPath}");
        seed.Open();

        using (var create = seed.CreateCommand())
        {
            // The AssistantChatMessages shape as it stood before AttachedFiles — everything else current,
            // so only the one missing column is under test.
            create.CommandText = """
                CREATE TABLE AssistantChats (
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
                CREATE TABLE AssistantChatMessages (
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
                    ProviderName    TEXT,
                    IsProtectedRoute INTEGER NOT NULL DEFAULT 0,
                    FOREIGN KEY (ChatId) REFERENCES AssistantChats(Id) ON DELETE CASCADE
                );
                """;
            create.ExecuteNonQuery();
        }

        using (var insert = seed.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO AssistantChats (Id, SchemaVersion, Title, CreatedAt, UpdatedAt, LastAccessedAt, WindowMode)
                VALUES (@ChatId, 1, 'before the column existed', @Now, @Now, @Now, 'Assistant');
                INSERT INTO AssistantChatMessages (Id, ChatId, Ordinal, Role, Content, Timestamp)
                VALUES (@MessageId, @ChatId, 0, 'user', 'an older question', @Now);
                """;
            insert.Parameters.AddWithValue("@ChatId", chatId.ToString());
            insert.Parameters.AddWithValue("@MessageId", messageId.ToString());
            insert.Parameters.AddWithValue("@Now", now);
            insert.ExecuteNonQuery();
        }
    }

    [Fact]
    public void MigrateSchema_AddsAttachedFiles_AndTheOlderMessageStillReads()
    {
        var chatId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        SeedOldSchema(chatId, messageId);
        SqlitePool.ClearFor($"Data Source={_dbPath}");

        // GetConnection() runs EnsureSchema()/MigrateSchema(); CREATE TABLE IF NOT EXISTS leaves the
        // seeded table alone, so the ALTER TABLE branch is what is under test.
        using var ctx = new SqliteContext(_dbPath);
        var conn = ctx.GetConnection();

        var hasColumn = false;
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(AssistantChatMessages)";
            using var r = pragma.ExecuteReader();
            while (r.Read())
            {
                if (r.GetString(1) == "AttachedFiles") { hasColumn = true; break; }
            }
        }
        Assert.True(hasColumn, "AttachedFiles column should be added by the migration.");

        using var select = conn.CreateCommand();
        select.CommandText = "SELECT Content, AttachedFiles FROM AssistantChatMessages WHERE Id = @Id";
        select.Parameters.AddWithValue("@Id", messageId.ToString());
        using var reader = select.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("an older question", reader.GetString(0));
        Assert.True(reader.IsDBNull(1));
    }

    [Fact]
    public async Task AfterMigration_AChatWithAttachmentsRoundTrips()
    {
        var chatId = Guid.NewGuid();
        SeedOldSchema(chatId, Guid.NewGuid());
        SqlitePool.ClearFor($"Data Source={_dbPath}");

        using var ctx = new SqliteContext(_dbPath);
        using var service = new AssistantChatService(
            ctx, new AgentRunService(ctx, NullLogger<AgentRunService>.Instance));

        var now = DateTime.UtcNow;
        var chat = new SyncAssistantChat
        {
            Id = Guid.NewGuid(),
            Title = "after the migration",
            CreatedAt = now,
            UpdatedAt = now,
            LastAccessedAt = now,
            WindowMode = "Assistant",
            Messages =
            [
                new SyncAssistantChatMessage
                {
                    Id = Guid.NewGuid(),
                    Role = "user",
                    Content = "summarise this",
                    Timestamp = now,
                    AttachedFiles =
                    [
                        new SyncMessageAttachedFile
                        {
                            FileName = "report.docx",
                            RelativePath = "Playground/report.docx",
                        },
                    ],
                },
            ],
        };

        await service.SaveAsync(chat, TestContext.Current.CancellationToken);
        var back = await service.GetAsync(chat.Id, TestContext.Current.CancellationToken);

        var files = back!.Messages[0].AttachedFiles;
        Assert.NotNull(files);
        Assert.Equal("report.docx", Assert.Single(files).FileName);
        Assert.Equal("Playground/report.docx", files[0].RelativePath);
    }
}
