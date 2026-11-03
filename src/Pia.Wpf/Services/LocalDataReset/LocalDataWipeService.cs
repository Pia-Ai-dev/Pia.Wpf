using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure;
using Pia.Models;
using Pia.Paths;
using Pia.Services.Interfaces;
using Pia.Services.Plugins;

namespace Pia.Services.LocalDataReset;

/// <summary>Empties every synced store with plain SQL and file operations; the per-entity deletes would reach the server.</summary>
public sealed class LocalDataWipeService(
    SqliteContext db, ISettingsService settingsService, IPolicyService policyService, ILogger<LocalDataWipeService> logger)
{
    private static readonly string[] SyncedFiles = ["templates.json", "pending-sync-deletes.json", "policy-cache.json"];

    // Copies a failed parse moved aside; the device-local stores keep theirs.
    private static readonly string[] AsideStores = [.. SyncedFiles, "providers.json", "settings.json"];

    // Children before parents, so the clear does not depend on the connection enforcing ON DELETE CASCADE.
    private static readonly string[] ClearStatements =
    [
        "DELETE FROM Personas",
        "DELETE FROM ManagedPersonas",
        "DELETE FROM Sessions",
        "DELETE FROM AgentToolExchanges",
        "DELETE FROM AgentTimelineEvents",
        "DELETE FROM AgentSteps",
        "DELETE FROM AgentRuns",
        "DELETE FROM AssistantChatMessages",
        "DELETE FROM AssistantChats",
        "DELETE FROM AssistantChatsFts",
        "DELETE FROM Memories",
        "DELETE FROM Todos",
        "DELETE FROM KanbanColumns",
        "DELETE FROM ScheduledJobs WHERE Kind <> 'MeetingAttendance'",
    ];

    public async Task WipeAsync()
    {
        await EnsureSettingsReadableAsync();
        await policyService.ClearServerPolicyAsync();

        // Signed out first, so a failure further down never leaves the old account signed in.
        var settings = await settingsService.GetSettingsAsync();
        settings.ClearAccountState();
        settings.ResetSyncedData();
        await settingsService.SaveSettingsAsync(settings);

        var deletedFiles = DeleteSyncedFiles();
        var deletedAsides = DeleteUnreadableAsides();
        var removedProviders = await KeepOnlyPiaCloudProvidersAsync();
        var counts = ClearDatabase();

        logger.LogInformation(
            "Local synced data cleared: files {Files}, unreadable copies {Asides}, providers removed {Providers}, rows {Rows}",
            deletedFiles, deletedAsides, removedProviders, string.Join(", ", counts.Select(c => $"{c.Key}={c.Value}")));
    }

    // GetSettingsAsync answers an unreadable or malformed file with defaults, which the save below would make
    // permanent. Throwing here, before anything is cleared, keeps the file and the marker for the next start.
    private static async Task EnsureSettingsReadableAsync()
    {
        var path = Path.Combine(PiaPaths.RoamingDataDirectory, "settings.json");
        if (!File.Exists(path))
            return;

        var json = await File.ReadAllTextAsync(path);
        if (JsonSerializer.Deserialize<AppSettings>(json, JsonPersistenceService<AppSettings>.JsonOptions) is null)
            throw new JsonException("settings.json holds no settings object");
    }

    private static int DeleteSyncedFiles()
    {
        var deleted = 0;
        foreach (var name in SyncedFiles)
        {
            var path = Path.Combine(PiaPaths.RoamingDataDirectory, name);
            if (!File.Exists(path))
                continue;

            File.Delete(path);
            deleted++;
        }

        return deleted;
    }

    private static int DeleteUnreadableAsides()
    {
        var deleted = 0;
        foreach (var name in AsideStores)
        {
            var directory = Path.GetDirectoryName(Path.Combine(PiaPaths.RoamingDataDirectory, name))!;
            if (!Directory.Exists(directory))
                continue;

            // The pattern alone would also match 8.3 short names, so the prefix is checked again.
            var prefix = name + ".unreadable-";
            foreach (var path in Directory.GetFiles(directory, prefix + "*"))
            {
                if (!Path.GetFileName(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                File.Delete(path);
                deleted++;
            }
        }

        return deleted;
    }

    private static async Task<int> KeepOnlyPiaCloudProvidersAsync()
    {
        var path = Path.Combine(PiaPaths.RoamingDataDirectory, "providers.json");
        if (!File.Exists(path))
            return 0;

        var options = JsonPersistenceService<List<AiProvider>>.JsonOptions;
        List<AiProvider> providers;
        try
        {
            providers = JsonSerializer.Deserialize<List<AiProvider>>(await File.ReadAllTextAsync(path), options) ?? [];
        }
        catch (JsonException)
        {
            // ProviderService reads an unparseable file as empty, so writing it empty changes nothing it sees.
            providers = [];
        }

        var kept = providers.Where(p => p.ProviderType == AiProviderType.PiaCloud).ToList();
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(kept, options));
        return providers.Count - kept.Count;
    }

    private Dictionary<string, int> ClearDatabase()
    {
        var connection = db.GetConnection();
        using var transaction = connection.BeginTransaction();
        var counts = new Dictionary<string, int>();

        foreach (var statement in ClearStatements)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = statement;
            counts[statement.Split(' ')[2]] = command.ExecuteNonQuery();
        }

        SqliteContext.SeedDefaultKanbanColumns(connection, transaction, DateTime.UnixEpoch);
        ResetPlugins(connection, transaction, counts);

        transaction.Commit();
        return counts;
    }

    private static void ResetPlugins(SqliteConnection connection, SqliteTransaction transaction, Dictionary<string, int> counts)
    {
        var rows = new List<(string Id, string? ConfigJson)>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT Id, ConfigJson FROM Plugins";
            using var reader = select.ExecuteReader();
            while (reader.Read())
                rows.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        }

        var deleted = 0;
        var reset = 0;
        foreach (var (id, configJson) in rows)
        {
            var preloaded = Guid.TryParse(id, out var guid) && BuiltInPluginDefaults.PreloadedPluginIds.Contains(guid);
            if (!preloaded && LocalMcpConfig.IsLocal(configJson))
                continue;

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = preloaded
                ? "UPDATE Plugins SET UserEnabled = NULL WHERE Id = @Id"
                : "DELETE FROM Plugins WHERE Id = @Id";
            command.Parameters.AddWithValue("@Id", id);
            command.ExecuteNonQuery();

            if (preloaded)
                reset++;
            else
                deleted++;
        }

        counts["Plugins"] = deleted;
        counts["PluginsReset"] = reset;
    }
}
