using System.Diagnostics;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Pia.Infrastructure;
using Pia.Services;
using Pia.Services.Interfaces;
using Xunit;

namespace Pia.Tests.Services;

/// <summary>Measurement probe, not an assertion; Explicit, env-driven. Numbers and how to run it:
/// docs/chat_history_performance/2026-09-11-large-import-slowdown.md.</summary>
public sealed class ChatStorePerfProbe
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Dir => Environment.GetEnvironmentVariable("PIA_PERF_DIR")
        ?? throw new InvalidOperationException("PIA_PERF_DIR not set");

    private static string Export => Environment.GetEnvironmentVariable("PIA_PERF_EXPORT")
        ?? throw new InvalidOperationException("PIA_PERF_EXPORT not set");

    private static void Log(string line)
    {
        Console.WriteLine(line);
        File.AppendAllText(Path.Combine(Dir, "probe.log"), line + Environment.NewLine);
    }

    private static (SqliteContext ctx, AssistantChatService chats) Open()
    {
        Directory.CreateDirectory(Dir);
        var ctx = new SqliteContext(Path.Combine(Dir, "history.db"));
        var runs = new AgentRunService(ctx, NullLogger<AgentRunService>.Instance);
        return (ctx, new AssistantChatService(ctx, runs));
    }

    private static string DbSize()
    {
        var total = 0L;
        foreach (var f in Directory.GetFiles(Dir, "history.db*"))
            total += new FileInfo(f).Length;
        return (total / 1048576.0).ToString("F1") + " MB";
    }

    [Fact(Explicit = true)]
    public async Task Import()
    {
        var (ctx, chats) = Open();
        var archive = new ChatArchiveService(chats, NullLogger<ChatArchiveService>.Instance);

        Log($"--- import {Export} ({new FileInfo(Export).Length / 1048576.0:F1} MB) into {Dir}");
        var sw = Stopwatch.StartNew();
        var lastTick = 0L;
        var progress = new Progress<ChatImportProgress>(p =>
        {
            if (p.Phase != ChatImportPhase.Storing || p.Total == 0) return;
            // One line per 10% so the curve is visible without a line per chat.
            var decile = p.Processed * 10 / p.Total;
            if (decile == lastTick) return;
            lastTick = decile;
            Log($"  stored {p.Processed}/{p.Total} at {sw.Elapsed.TotalSeconds:F1}s");
        });

        var result = await archive.ImportAsync(Export, progress, Ct);
        sw.Stop();

        Log($"import took {sw.Elapsed.TotalSeconds:F1}s: imported={result.Imported} " +
            $"upToDate={result.SkippedUpToDate} empty={result.SkippedEmpty} failed={result.Failed}");
        Log($"db on disk: {DbSize()}");

        chats.Dispose();
        ctx.Dispose();
    }

    [Fact(Explicit = true)]
    public async Task Measure()
    {
        var (ctx, chats) = Open();
        Log($"--- measure against {Dir} ({DbSize()})");

        var total = await chats.CountAsync(ct: Ct);
        Log($"chats in store: {total}");

        // What AssistantHistoryViewModel.LoadChatsAsync actually issues on a navigation.
        var thirtyDays = DateTime.Today.AddDays(-30);
        await TimeNavigationQuery("filter=today-30 (the seeded default)", chats, thirtyDays);
        await TimeNavigationQuery("filter=none (user cleared it)", chats, null);

        // The gate hypothesis: retention holds _gate for the whole delete batch, and every UI query
        // queues behind it.
        var cutoff = DateTime.UtcNow.AddDays(-180);
        var evictable = 0;
        foreach (var c in await chats.SearchAsync(limit: int.MaxValue, ct: Ct))
            if (c.LastAccessedAt < cutoff) evictable++;
        Log($"chats retention would evict at the 180-day default: {evictable} of {total}");

        var evictSw = Stopwatch.StartNew();
        var eviction = Task.Run(() => chats.EvictOlderThanAsync(cutoff, Ct), Ct);

        var blocked = new List<double>();
        while (!eviction.IsCompleted)
        {
            var sw = Stopwatch.StartNew();
            await chats.SearchAsync(fromDate: thirtyDays, limit: 50, ct: Ct);
            await chats.CountAsync(fromDate: thirtyDays, ct: Ct);
            sw.Stop();
            blocked.Add(sw.Elapsed.TotalMilliseconds);
        }

        var evicted = await eviction;
        evictSw.Stop();
        Log($"eviction deleted {evicted.Count} chats in {evictSw.Elapsed.TotalSeconds:F1}s");
        if (blocked.Count > 0)
        {
            Log($"navigation queries issued during eviction: n={blocked.Count} " +
                $"max={blocked.Max():F0}ms median={Median(blocked):F0}ms");
        }

        await TimeNavigationQuery("filter=today-30, after eviction", chats, thirtyDays);
        Log($"chats left: {await chats.CountAsync(ct: Ct)} / db {DbSize()}");

        chats.Dispose();
        ctx.Dispose();
    }

    private static async Task TimeNavigationQuery(string label, AssistantChatService chats, DateTime? from)
    {
        var runs = new List<double>();
        for (var i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            var rows = await chats.SearchAsync(fromDate: from, offset: 0, limit: 50, ct: Ct);
            var count = await chats.CountAsync(fromDate: from, ct: Ct);
            sw.Stop();
            runs.Add(sw.Elapsed.TotalMilliseconds);
            if (i == 0) Log($"{label}: {rows.Count} rows of {count} matched");
        }

        Log($"{label}: first={runs[0]:F0}ms median={Median(runs):F0}ms max={runs.Max():F0}ms");
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }


    // Opening a chat reads its whole transcript under the gate, so under a skewed archive the cost of
    // one click scales with that chat, not with the store.
    [Fact(Explicit = true)]
    public async Task MeasureOpenChat()
    {
        var (ctx, chats) = Open();
        Log($"--- opening a chat, {Dir} ({DbSize()})");

        var rows = await chats.SearchAsync(limit: int.MaxValue, ct: Ct);
        var sized = new List<(Guid Id, int Messages, long Chars, double Ms)>();
        foreach (var row in rows)
        {
            var sw = Stopwatch.StartNew();
            var full = await chats.GetAsync(row.Id, Ct);
            sw.Stop();
            if (full is null) continue;
            sized.Add((row.Id, full.Messages.Count, full.Messages.Sum(m => (long)m.Content.Length),
                sw.Elapsed.TotalMilliseconds));
        }

        foreach (var group in new[]
                 {
                     ("heaviest", sized.OrderByDescending(c => c.Chars).Take(3)),
                     ("lightest", sized.OrderBy(c => c.Chars).Take(3)),
                 })
        {
            foreach (var c in group.Item2)
            {
                Log($"  {group.Item1}: {c.Messages} messages, {c.Chars / 1024} KB of text, "
                    + $"GetAsync {c.Ms:F0} ms");
            }
        }

        Log($"total across {sized.Count} chats: {sized.Sum(c => c.Chars) / 1048576} MB of text, "
            + $"{sized.Sum(c => c.Messages)} messages");

        chats.Dispose();
        ctx.Dispose();
    }

    // Which half of the eviction batch costs the 28 seconds: the row deletes, or the FTS delete the
    // doc calls an unindexed scan. Rolled back, so the store is left as found.
    [Fact(Explicit = true)]
    public async Task MeasureEvictionSql()
    {
        Directory.CreateDirectory(Dir);
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = Path.Combine(Dir, "history.db") }.ToString());
        await connection.OpenAsync(Ct);

        Log($"--- eviction SQL against {Dir}");
        Log("plan, FTS delete: " + await PlanFor(connection,
            "DELETE FROM AssistantChatsFts WHERE ChatId = '00000000-0000-0000-0000-000000000000'"));
        Log("plan, chat delete: " + await PlanFor(connection,
            "DELETE FROM AssistantChats WHERE Id = '00000000-0000-0000-0000-000000000000'"));

        var ids = new List<string>();
        using (var pick = connection.CreateCommand())
        {
            pick.CommandText = "SELECT Id FROM AssistantChats LIMIT 200";
            using var reader = await pick.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct)) ids.Add(reader.GetString(0));
        }

        using var transaction = connection.BeginTransaction();
        var chatMs = await TimeDeletes(connection, transaction, "DELETE FROM AssistantChats WHERE Id = @Id", ids);
        var ftsMs = await TimeDeletes(connection, transaction, "DELETE FROM AssistantChatsFts WHERE ChatId = @Id", ids);
        transaction.Rollback();

        Log($"{ids.Count} chats: row delete {chatMs:F0}ms total ({chatMs / ids.Count:F1}ms each), "
            + $"FTS delete {ftsMs:F0}ms total ({ftsMs / ids.Count:F1}ms each)");
    }

    private static async Task<double> TimeDeletes(
        SqliteConnection connection, SqliteTransaction transaction, string sql, List<string> ids)
    {
        var sw = Stopwatch.StartNew();
        foreach (var id in ids)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue("@Id", id);
            await command.ExecuteNonQueryAsync(Ct);
        }

        return sw.Elapsed.TotalMilliseconds;
    }

    private static async Task<string> PlanFor(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        var lines = new List<string>();
        using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
            lines.Add(reader.GetString(reader.FieldCount - 1));
        return string.Join(" | ", lines);
    }
}
