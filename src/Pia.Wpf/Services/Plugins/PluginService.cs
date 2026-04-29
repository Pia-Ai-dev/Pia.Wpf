using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure;
using Pia.Logging;
using Pia.Services.Interfaces;
using Pia.Shared.Models;

namespace Pia.Services.Plugins;

public class PluginService : IPluginService
{
    public event EventHandler? PluginsChanged;

    private readonly IMemoryToolHandler _memoryToolHandler;
    private readonly ITodoToolHandler _todoToolHandler;
    private readonly IReminderToolHandler _reminderToolHandler;
    private readonly ILogger<PluginService> _logger;
    private readonly SqliteContext _sqliteContext;
    private readonly CabManagerService? _cabManager;

    private readonly Dictionary<Guid, IPluginToolHandler> _handlers = new();
    private readonly Dictionary<string, IPluginToolHandler> _toolNameRoutes = new();
    private readonly Dictionary<Guid, SyncPlugin> _pluginConfigs = new();
    private readonly List<SyncPluginPreference> _pendingPrefs = [];

    public IReadOnlyList<IPluginToolHandler> ActiveHandlers
    {
        get
        {
            lock (_handlers)
                return _handlers.Values.ToList();
        }
    }

    public PluginService(
        IMemoryToolHandler memoryToolHandler,
        ITodoToolHandler todoToolHandler,
        IReminderToolHandler reminderToolHandler,
        ILogger<PluginService> logger,
        SqliteContext sqliteContext,
        CabManagerService? cabManager = null)
    {
        _memoryToolHandler = memoryToolHandler;
        _todoToolHandler = todoToolHandler;
        _reminderToolHandler = reminderToolHandler;
        _logger = logger;
        _sqliteContext = sqliteContext;
        _cabManager = cabManager;

        InitializeBuiltInPlugins();
        LoadPersistedPlugins();
    }

    private void InitializeBuiltInPlugins()
    {
        foreach (var (id, config) in BuiltInPluginDefaults.Defaults)
        {
            _pluginConfigs[id] = config;

            IPluginToolHandler adapter = GetHandlerId(config.ConfigJson) switch
            {
                "memory" => BuiltInPluginHandler.FromMemoryHandler(_memoryToolHandler, config),
                "todo" => BuiltInPluginHandler.FromTodoHandler(_todoToolHandler, config),
                "reminder" => BuiltInPluginHandler.FromReminderHandler(_reminderToolHandler, config),
                _ => throw new InvalidOperationException($"Unknown built-in handler for plugin {config.Name}")
            };

            RegisterHandler(id, adapter);
        }

        _logger.LogInformation("PluginService initialized with {Count} built-in plugins", _handlers.Count);
    }

    private void LoadPersistedPlugins()
    {
        try
        {
            var plugins = LoadPluginsFromDb();
            foreach (var plugin in plugins)
            {
                if (BuiltInPluginDefaults.PreloadedPluginIds.Contains(plugin.Id))
                    continue; // Built-ins are handled separately

                _pluginConfigs[plugin.Id] = plugin;
            }

            _logger.LogInformation("Loaded {Count} persisted server plugins from database", plugins.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load persisted plugins from database");
        }
    }

    public async Task InitializePersistedPluginsAsync()
    {
        var serverPlugins = _pluginConfigs.Values
            .Where(p => !p.IsPreloaded && !_handlers.ContainsKey(p.Id))
            .ToList();

        foreach (var plugin in serverPlugins)
        {
            await HandleNewServerPluginAsync(plugin);
        }

        if (serverPlugins.Count > 0)
        {
            RebuildToolNameRoutes();
            _logger.LogInformation("Initialized {Count} persisted server plugin handler(s)", serverPlugins.Count);
        }
    }

    private List<SyncPlugin> LoadPluginsFromDb()
    {
        var connection = _sqliteContext.GetConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id, Kind, Name, Description, IconUrl, ConfigJson, Version, IsPreloaded, IsActive, UserEnabled, UpdatedAt FROM Plugins";

        var plugins = new List<SyncPlugin>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            plugins.Add(new SyncPlugin
            {
                Id = Guid.Parse(reader.GetString(0)),
                Kind = reader.GetString(1),
                Name = reader.GetString(2),
                Description = reader.IsDBNull(3) ? null : reader.GetString(3),
                IconUrl = reader.IsDBNull(4) ? null : reader.GetString(4),
                ConfigJson = reader.GetString(5),
                Version = reader.GetString(6),
                IsPreloaded = reader.GetInt32(7) != 0,
                IsActive = reader.GetInt32(8) != 0,
                UserEnabled = reader.IsDBNull(9) ? null : reader.GetInt32(9) != 0,
                UpdatedAt = DateTime.Parse(reader.GetString(10))
            });
        }
        return plugins;
    }

    private void SavePluginToDb(SyncPlugin plugin)
    {
        var connection = _sqliteContext.GetConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO Plugins (Id, Kind, Name, Description, IconUrl, ConfigJson, Version, IsPreloaded, IsActive, UserEnabled, UpdatedAt)
            VALUES (@Id, @Kind, @Name, @Description, @IconUrl, @ConfigJson, @Version, @IsPreloaded, @IsActive, @UserEnabled, @UpdatedAt)
            """;

        cmd.Parameters.AddWithValue("@Id", plugin.Id.ToString());
        cmd.Parameters.AddWithValue("@Kind", plugin.Kind);
        cmd.Parameters.AddWithValue("@Name", plugin.Name);
        cmd.Parameters.AddWithValue("@Description", (object?)plugin.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@IconUrl", (object?)plugin.IconUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ConfigJson", plugin.ConfigJson);
        cmd.Parameters.AddWithValue("@Version", plugin.Version);
        cmd.Parameters.AddWithValue("@IsPreloaded", plugin.IsPreloaded ? 1 : 0);
        cmd.Parameters.AddWithValue("@IsActive", plugin.IsActive ? 1 : 0);
        cmd.Parameters.AddWithValue("@UserEnabled", plugin.UserEnabled.HasValue ? (plugin.UserEnabled.Value ? 1 : 0) : DBNull.Value);
        cmd.Parameters.AddWithValue("@UpdatedAt", plugin.UpdatedAt.ToString("O"));

        cmd.ExecuteNonQuery();
    }

    private void DeletePluginFromDb(Guid pluginId)
    {
        var connection = _sqliteContext.GetConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM Plugins WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", pluginId.ToString());
        cmd.ExecuteNonQuery();
    }

    private void RegisterHandler(Guid pluginId, IPluginToolHandler handler)
    {
        lock (_handlers)
        {
            _handlers[pluginId] = handler;
            foreach (var tool in handler.GetTools())
            {
                var toolName = tool.Name;
                _toolNameRoutes[toolName] = handler;
            }
        }
    }

    private void UnregisterHandler(Guid pluginId)
    {
        lock (_handlers)
        {
            if (_handlers.TryGetValue(pluginId, out var handler))
            {
                foreach (var tool in handler.GetTools())
                    _toolNameRoutes.Remove(tool.Name);
                _handlers.Remove(pluginId);
            }
        }
    }

    public IList<AITool> GetAllTools()
    {
        lock (_handlers)
        {
            var tools = new List<AITool>();
            foreach (var handler in _handlers.Values)
            {
                var config = _pluginConfigs.GetValueOrDefault(handler.PluginId);
                if (config is not null && !IsPluginEnabled(config))
                {
                    _logger.LogWarning("GetAllTools: plugin {PluginName} (id={PluginId}) skipped — IsActive={IsActive}, UserEnabled={UserEnabled}, kind={Kind}",
                        config.Name, config.Id, config.IsActive, config.UserEnabled, config.Kind);
                    continue;
                }
                tools.AddRange(handler.GetTools());
            }
            _logger.LogInformation("GetAllTools: returning {ToolCount} tools from {HandlerCount} active handlers: [{ToolNames}]",
                tools.Count, _handlers.Count,
                string.Join(", ", tools.Select(t => t.Name)));
            return tools;
        }
    }

    public string GetCombinedSystemPromptAdditions()
    {
        lock (_handlers)
        {
            var parts = new List<string>();
            foreach (var handler in _handlers.Values)
            {
                var config = _pluginConfigs.GetValueOrDefault(handler.PluginId);
                if (config is not null && !IsPluginEnabled(config))
                    continue;
                var prompt = handler.GetSystemPromptAddition();
                if (!string.IsNullOrWhiteSpace(prompt))
                    parts.Add(prompt);
            }
            _logger.LogDebug("GetCombinedSystemPromptAdditions: {PartCount} parts, total {Length} chars",
                parts.Count, string.Join("\n\n", parts).Length);
            return string.Join("\n\n", parts);
        }
    }

    public async Task<(object? Result, PluginToolCall? PendingAction)?> RouteToolCallAsync(
        FunctionCallContent toolCall, CancellationToken ct = default)
    {
        IPluginToolHandler? handler;
        lock (_handlers)
        {
            _toolNameRoutes.TryGetValue(toolCall.Name, out handler);
        }

        if (handler is null)
        {
            _logger.LogWarning("No plugin handler found for tool {ToolName}. Registered routes: [{Routes}]",
                toolCall.Name, string.Join(", ", _toolNameRoutes.Keys));
            return null;
        }

        _logger.LogDebug("Routing tool {ToolName} to handler {HandlerName} (pluginId={PluginId})",
            toolCall.Name, handler.PluginName, handler.PluginId);
        return await handler.HandleToolCallAsync(toolCall, ct);
    }

    public IReadOnlyList<SyncPlugin> GetAllPluginConfigs()
    {
        return _pluginConfigs.Values.ToList();
    }

    public async Task ApplyServerPluginsAsync(IReadOnlyList<SyncPlugin> upserted, IReadOnlyList<Guid> deleted)
    {
        // Handle deletions
        foreach (var id in deleted)
        {
            if (BuiltInPluginDefaults.PreloadedPluginIds.Contains(id))
                continue; // Never remove built-in plugins

            if (_handlers.TryGetValue(id, out var handler))
            {
                await handler.ShutdownAsync();
                UnregisterHandler(id);
            }
            _pluginConfigs.Remove(id);
            DeletePluginFromDb(id);
            _logger.LogInformation("Removed plugin {PluginId}", id);
        }

        // Handle upserts
        foreach (var plugin in upserted)
        {
            // Preserve user's local enabled preference across server updates
            if (_pluginConfigs.TryGetValue(plugin.Id, out var existingConfig) && existingConfig.UserEnabled.HasValue)
                plugin.UserEnabled = existingConfig.UserEnabled;

            _pluginConfigs[plugin.Id] = plugin;

            if (!plugin.IsPreloaded)
                SavePluginToDb(plugin);

            if (_handlers.TryGetValue(plugin.Id, out var existing))
            {
                // Update existing handler metadata
                existing.ApplyServerMetadata(plugin);
                _logger.LogDebug("Updated metadata for plugin {PluginName}", plugin.Name);
            }
            else if (!plugin.IsPreloaded)
            {
                // New server-only plugin — run preflight and cab extraction outside the lock
                await HandleNewServerPluginAsync(plugin);
            }
        }

        // Rebuild tool name routes
        RebuildToolNameRoutes();

        _logger.LogInformation("Applied server plugins: {Upserted} upserted, {Deleted} deleted",
            upserted.Count, deleted.Count);

        PluginsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task HandleNewServerPluginAsync(SyncPlugin plugin)
    {
        if (plugin.Kind != "mcp_server")
        {
            // TODO: implement RestApiPluginToolHandler
            _logger.LogInformation("Server-only plugin {PluginName} ({Kind}) received but handler not yet implemented",
                plugin.Name, plugin.Kind);
            return;
        }

        // Parse transport from ConfigJson
        string? transport = null;
        string? command = null;
        string? url = null;
        string[] args = [];
        string? systemPromptAddition = null;

        try
        {
            using var doc = JsonDocument.Parse(plugin.ConfigJson);
            if (doc.RootElement.TryGetProperty("transport", out var transportEl))
                transport = transportEl.GetString();
            if (doc.RootElement.TryGetProperty("command", out var cmdEl))
                command = cmdEl.GetString();
            if (doc.RootElement.TryGetProperty("url", out var urlEl))
                url = urlEl.GetString();
            if (doc.RootElement.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Array)
                args = argsEl.EnumerateArray().Select(a => a.GetString() ?? "").ToArray();
            if (doc.RootElement.TryGetProperty("systemPromptAddition", out var spaEl))
                systemPromptAddition = spaEl.GetString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse ConfigJson for plugin {PluginName}", plugin.Name);
            return;
        }

        if (string.IsNullOrEmpty(transport))
        {
            _logger.LogWarning("Plugin {PluginName} has no transport specified in ConfigJson", plugin.Name);
            return;
        }

        _logger.LogInformation("Plugin {PluginName}: transport={Transport}", plugin.Name, transport);
        _logger.SensitiveDebug("Plugin {PluginName} command='{Command}', args=[{Args}]",
            plugin.Name, command ?? "<null>", string.Join(", ", args));

        // Check prerequisites
        try
        {
            using var doc2 = JsonDocument.Parse(plugin.ConfigJson);
            if (doc2.RootElement.TryGetProperty("prerequisites", out var prereqs))
            {
                var runtime = prereqs.TryGetProperty("runtime", out var rtEl) ? rtEl.GetString() : null;
                var minVersion = prereqs.TryGetProperty("minVersion", out var mvEl) ? mvEl.GetString() : null;

                if (runtime == "node" && !string.IsNullOrEmpty(minVersion))
                {
                    var (meetsMin, actualVersion) = await CheckNodeVersionAsync(minVersion);
                    if (actualVersion is null)
                        _logger.LogWarning("Plugin {PluginName}: prerequisite check failed — Node.js not found on PATH", plugin.Name);
                    else if (!meetsMin)
                        _logger.LogWarning("Plugin {PluginName}: Node.js {Actual} < required {Min}, plugin may fail",
                            plugin.Name, actualVersion, minVersion);
                    else
                        _logger.LogInformation("Plugin {PluginName}: Node.js {Actual} meets minimum {Min}",
                            plugin.Name, actualVersion, minVersion);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Plugin {PluginName}: failed to parse prerequisites", plugin.Name);
        }

        // Preflight checks — run outside any lock on _handlers
        switch (transport)
        {
            case "stdio":
                if (!string.IsNullOrEmpty(command))
                {
                    var commandExists = await CheckCommandOnPathAsync(command);
                    _logger.LogInformation("Plugin {PluginName}: command '{Command}' on PATH = {Exists}",
                        plugin.Name, command, commandExists);
                    if (!commandExists)
                    {
                        // If the plugin has a cab, try extracting it first
                        if (!string.IsNullOrEmpty(plugin.CabHash) && _cabManager is not null)
                        {
                            var extractedPath = await _cabManager.EnsurePluginExtractedAsync(plugin);
                            if (extractedPath is null)
                            {
                                _logger.LogWarning("Plugin {PluginName}: command '{Command}' not found on PATH and cab extraction failed, skipping activation",
                                    plugin.Name, command);
                                return;
                            }

                            _logger.LogInformation("Plugin {PluginName}: cab extracted to {Path}", plugin.Name, extractedPath);
                        }
                        else
                        {
                            _logger.LogWarning("Plugin {PluginName}: command '{Command}' not found on PATH, skipping activation",
                                plugin.Name, command);
                            return;
                        }
                    }
                }

                // Check version prerequisites if specified
                try
                {
                    using var doc = JsonDocument.Parse(plugin.ConfigJson);
                    if (doc.RootElement.TryGetProperty("prerequisites", out var prereqs)
                        && prereqs.TryGetProperty("minVersion", out var minVersionEl))
                    {
                        var minVersion = minVersionEl.GetString();
                        if (!string.IsNullOrEmpty(minVersion))
                        {
                            _logger.LogDebug("Plugin {PluginName} requires minimum version {MinVersion}",
                                plugin.Name, minVersion);
                            // Version check is best-effort; log for diagnostics
                        }
                    }
                }
                catch { /* version check is non-critical */ }

                break;

            case "sse":
                if (!string.IsNullOrEmpty(url))
                {
                    var reachable = await PingUrlAsync(url);
                    if (!reachable)
                    {
                        _logger.LogWarning("Plugin {PluginName}: SSE endpoint '{Url}' is not reachable, skipping activation",
                            plugin.Name, SafeUrl.Format(url));
                        return;
                    }
                }
                break;

            default:
                _logger.LogWarning("Plugin {PluginName}: unknown transport '{Transport}', skipping activation",
                    plugin.Name, transport);
                return;
        }

        // If stdio plugin has a cab and we haven't extracted yet, ensure extraction
        if (transport == "stdio" && !string.IsNullOrEmpty(plugin.CabHash) && _cabManager is not null)
        {
            var extractedPath = await _cabManager.EnsurePluginExtractedAsync(plugin);
            if (extractedPath is not null)
                _logger.LogInformation("Plugin {PluginName}: cab extracted to {Path}", plugin.Name, extractedPath);
        }

        // Create and register McpPluginToolHandler
        var resolvedCommand = command ?? "";
        var handler = new McpPluginToolHandler(
            plugin.Id, plugin.Name,
            resolvedCommand, args,
            systemPromptAddition,
            _logger);

        try
        {
            await handler.InitializeAsync();
            RegisterHandler(plugin.Id, handler);
            _logger.LogInformation("Plugin {PluginName} ({Transport}) activated with {ToolCount} tools",
                plugin.Name, transport, handler.GetTools().Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize McpPluginToolHandler for plugin {PluginName}", plugin.Name);
            handler.Dispose();
        }
    }

    private async Task<(bool MeetsMinimum, string? ActualVersion)> CheckNodeVersionAsync(string minVersion)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "node",
                Arguments = "--version",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true
            };
            using var process = Process.Start(psi);
            if (process is null) return (false, null);
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            var actual = output.Trim().TrimStart('v');
            _logger.LogInformation("Node.js version detected: {Version}", actual);

            if (Version.TryParse(actual, out var actualVer) && Version.TryParse(minVersion, out var minVer))
                return (actualVer >= minVer, actual);

            return (true, actual); // Can't parse, assume OK
        }
        catch
        {
            return (false, null);
        }
    }

    private async Task<bool> CheckCommandOnPathAsync(string command)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = command,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true
            };
            using var process = Process.Start(psi);
            if (process is null) return false;
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> PingUrlAsync(string url)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var client = new HttpClient();
            var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, url), cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public Task SetPluginEnabledAsync(Guid pluginId, bool enabled)
    {
        if (_pluginConfigs.TryGetValue(pluginId, out var config))
        {
            config.UserEnabled = enabled;

            if (!config.IsPreloaded)
                SavePluginToDb(config);

            lock (_pendingPrefs)
            {
                _pendingPrefs.RemoveAll(p => p.PluginId == pluginId);
                _pendingPrefs.Add(new SyncPluginPreference { PluginId = pluginId, IsEnabled = enabled });
            }
            _logger.LogInformation("Plugin {PluginId} enabled={Enabled}", pluginId, enabled);
        }
        return Task.CompletedTask;
    }

    public List<SyncPluginPreference> GetPendingPreferenceChanges()
    {
        lock (_pendingPrefs)
        {
            var prefs = new List<SyncPluginPreference>(_pendingPrefs);
            _pendingPrefs.Clear();
            return prefs;
        }
    }

    public async Task ShutdownAllAsync()
    {
        List<IPluginToolHandler> handlers;
        lock (_handlers)
            handlers = _handlers.Values.ToList();

        foreach (var handler in handlers)
        {
            try { await handler.ShutdownAsync(); }
            catch (Exception ex) { _logger.LogError(ex, "Error shutting down plugin {PluginName}", handler.PluginName); }
        }
    }

    private void RebuildToolNameRoutes()
    {
        lock (_handlers)
        {
            _toolNameRoutes.Clear();
            foreach (var handler in _handlers.Values)
            {
                foreach (var tool in handler.GetTools())
                    _toolNameRoutes[tool.Name] = handler;
            }
        }
    }

    private static bool IsPluginEnabled(SyncPlugin config)
    {
        if (!config.IsActive)
            return false;

        if (config.UserEnabled.HasValue)
            return config.UserEnabled.Value;

        // Fall back to defaultEnabled from ConfigJson
        try
        {
            using var doc = JsonDocument.Parse(config.ConfigJson);
            if (doc.RootElement.TryGetProperty("defaultEnabled", out var el))
                return el.GetBoolean();
        }
        catch { }

        return true; // Default to enabled
    }

    private static string? GetHandlerId(string configJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(configJson);
            if (doc.RootElement.TryGetProperty("handlerId", out var el))
                return el.GetString();
        }
        catch { }
        return null;
    }
}
