using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Infrastructure;
using Pia.Logging;
using Pia.Services.Interfaces;
using Pia.Services.Operators;
using Pia.Shared.Models;

namespace Pia.Services.Plugins;

public class PluginService : IPluginService
{
    public event EventHandler? PluginsChanged;

    private readonly IMemoryToolHandler _memoryToolHandler;
    private readonly ITodoToolHandler _todoToolHandler;
    private readonly IReminderToolHandler _reminderToolHandler;
    private readonly IScheduledJobToolHandler _scheduledJobToolHandler;
    private readonly IFilesToolHandler _filesToolHandler;
    private readonly IIngestToolHandler _ingestToolHandler;
    private readonly IGitToolHandler _gitToolHandler;
    private readonly IChatHistoryToolHandler _chatHistoryToolHandler;
    private readonly IAssignmentToolHandler _assignmentToolHandler;
    private readonly IScreenCaptureToolHandler _screenCaptureToolHandler;
    private readonly IAssignmentSurfaceCache _assignmentSurfaceCache;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<PluginService> _logger;
    private readonly SqliteContext _sqliteContext;
    private readonly CabManagerService? _cabManager;
    private readonly DpapiHelper? _dpapiHelper;

    private readonly Dictionary<Guid, IPluginToolHandler> _handlers = new();
    private readonly Dictionary<string, IPluginToolHandler> _toolNameRoutes = new();
    private readonly Dictionary<Guid, SyncPlugin> _pluginConfigs = new();
    private readonly List<SyncPluginPreference> _pendingPrefs = [];

    /// <summary>Why a server never got as far as a handler, so the settings row can say so.</summary>
    private readonly ConcurrentDictionary<Guid, string> _startFailures = new();

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
        IScheduledJobToolHandler scheduledJobToolHandler,
        IFilesToolHandler filesToolHandler,
        IIngestToolHandler ingestToolHandler,
        IGitToolHandler gitToolHandler,
        IChatHistoryToolHandler chatHistoryToolHandler,
        IAssignmentToolHandler assignmentToolHandler,
        IScreenCaptureToolHandler screenCaptureToolHandler,
        IAssignmentSurfaceCache assignmentSurfaceCache,
        ISettingsService settingsService,
        ILogger<PluginService> logger,
        SqliteContext sqliteContext,
        CabManagerService? cabManager = null,
        DpapiHelper? dpapiHelper = null)
    {
        _memoryToolHandler = memoryToolHandler;
        _todoToolHandler = todoToolHandler;
        _reminderToolHandler = reminderToolHandler;
        _scheduledJobToolHandler = scheduledJobToolHandler;
        _filesToolHandler = filesToolHandler;
        _ingestToolHandler = ingestToolHandler;
        _gitToolHandler = gitToolHandler;
        _chatHistoryToolHandler = chatHistoryToolHandler;
        _assignmentToolHandler = assignmentToolHandler;
        _screenCaptureToolHandler = screenCaptureToolHandler;
        _assignmentSurfaceCache = assignmentSurfaceCache;
        _settingsService = settingsService;
        _logger = logger;
        _sqliteContext = sqliteContext;
        _cabManager = cabManager;
        _dpapiHelper = dpapiHelper;

        InitializeBuiltInPlugins();
        LoadPersistedPlugins();

        // Plugins whose tool list depends on settings (files plugin's sandbox folder)
        // need their routes rebuilt whenever those settings change.
        _settingsService.SettingsChanged += (_, _) => RebuildToolNameRoutes();

        // The assignment pack's availability is a server probe, not a setting, so it flips outside every
        // other rebuild trigger. Without this the first probe that turns it on offers tools with no route.
        _assignmentSurfaceCache.Changed += (_, _) => RebuildToolNameRoutes();
    }

    private void InitializeBuiltInPlugins()
    {
        foreach (var (id, shipped) in BuiltInPluginDefaults.Defaults)
        {
            // A copy: Defaults holds one shared instance per plugin, so writing UserEnabled onto it would
            // follow the process into every other service that reads the same static.
            var config = CopyOf(shipped);
            _pluginConfigs[id] = config;

            IPluginToolHandler adapter = GetHandlerId(config.ConfigJson) switch
            {
                "memory" => BuiltInPluginHandler.FromMemoryHandler(_memoryToolHandler, config),
                "todo" => BuiltInPluginHandler.FromTodoHandler(_todoToolHandler, config),
                "reminder" => BuiltInPluginHandler.FromReminderHandler(_reminderToolHandler, config),
                "scheduled-research" => BuiltInPluginHandler.FromScheduledJobHandler(_scheduledJobToolHandler, config),
                "files" => BuiltInPluginHandler.FromFilesHandler(_filesToolHandler, config),
                "ingest" => BuiltInPluginHandler.FromIngestHandler(_ingestToolHandler, config),
                "git" => BuiltInPluginHandler.FromGitHandler(_gitToolHandler, config),
                "chat-history" => BuiltInPluginHandler.FromChatHistoryHandler(_chatHistoryToolHandler, config),
                "assignments" => BuiltInPluginHandler.FromAssignmentHandler(_assignmentToolHandler, config),
                "screen" => BuiltInPluginHandler.FromScreenCaptureHandler(_screenCaptureToolHandler, config),
                _ => throw new InvalidOperationException($"Unknown built-in handler for plugin {config.Name}")
            };

            RegisterHandler(id, adapter);
        }

        _logger.LogInformation("PluginService initialized with {Count} built-in plugins", _handlers.Count);
    }

    private static SyncPlugin CopyOf(SyncPlugin source) => new()
    {
        Id = source.Id,
        Kind = source.Kind,
        Name = source.Name,
        Description = source.Description,
        IconUrl = source.IconUrl,
        ConfigJson = source.ConfigJson,
        Version = source.Version,
        IsPreloaded = source.IsPreloaded,
        IsActive = source.IsActive,
        UserEnabled = source.UserEnabled,
        UpdatedAt = source.UpdatedAt,
        CabHash = source.CabHash,
        CabSize = source.CabSize
    };

    private void LoadPersistedPlugins()
    {
        try
        {
            var plugins = LoadPluginsFromDb();
            foreach (var plugin in plugins)
            {
                if (BuiltInPluginDefaults.PreloadedPluginIds.Contains(plugin.Id))
                {
                    // A built-in's definition ships in code, so the row contributes nothing but the
                    // user's switch — taking the whole row would pin a retired release's prompt.
                    if (_pluginConfigs.TryGetValue(plugin.Id, out var builtIn))
                        builtIn.UserEnabled = plugin.UserEnabled;
                    continue;
                }

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
        // A disabled local server is skipped rather than started-then-hidden: activation spawns its process,
        // which the user switched off precisely to avoid.
        var serverPlugins = _pluginConfigs.Values
            .Where(p => !p.IsPreloaded && !_handlers.ContainsKey(p.Id))
            .Where(p => !LocalMcpConfig.IsLocal(p.ConfigJson) || IsPluginEnabled(p))
            .ToList();

        foreach (var plugin in serverPlugins)
        {
            await ActivateMcpPluginAsync(plugin);

            // Per server, not once at the end: Settings is built while this loop is still running, and
            // nothing else tells it one came up — its rows would freeze at whatever was true mid-startup.
            RebuildToolNameRoutes();
            PluginsChanged?.Invoke(this, EventArgs.Empty);
        }

        if (serverPlugins.Count > 0)
            _logger.LogInformation("Initialized {Count} persisted server plugin handler(s)", serverPlugins.Count);
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

    public IReadOnlyList<ToolCatalogEntry> GetToolCatalog()
    {
        lock (_handlers)
        {
            var entries = new List<ToolCatalogEntry>();
            foreach (var handler in _handlers.Values)
            {
                // The same skip GetAllTools uses: a plugin the user turned off must contribute nothing grantable.
                var config = _pluginConfigs.GetValueOrDefault(handler.PluginId);
                if (config is not null && !IsPluginEnabled(config))
                    continue;

                foreach (var tool in handler.GetTools())
                {
                    entries.Add(new ToolCatalogEntry(
                        handler.PluginId,
                        handler.PluginName,
                        tool.Name,
                        tool.Description,
                        IsMcpTool(tool.Name),
                        handler.DeclaresDestructive(tool.Name)));
                }
            }
            return entries;
        }
    }

    public string GetCombinedSystemPromptAdditions(IReadOnlySet<string>? excludedToolNames = null)
    {
        lock (_handlers)
        {
            var parts = new List<string>();
            foreach (var handler in _handlers.Values)
            {
                var config = _pluginConfigs.GetValueOrDefault(handler.PluginId);
                if (config is not null && !IsPluginEnabled(config))
                    continue;
                if (excludedToolNames is { Count: > 0 }
                    && handler.GetTools().Any(t => excludedToolNames.Contains(t.Name)))
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

    public bool IsMcpTool(string toolName)
    {
        lock (_handlers)
            return _toolNameRoutes.TryGetValue(toolName, out var handler) && handler is McpPluginToolHandler;
    }

    public IReadOnlyList<SyncPlugin> GetAllPluginConfigs()
    {
        return _pluginConfigs.Values.ToList();
    }

    public IReadOnlyList<SyncPlugin> GetLocalMcpPlugins() =>
        [.. _pluginConfigs.Values.Where(p => LocalMcpConfig.IsLocal(p.ConfigJson)).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)];

    public LocalMcpDefinition? GetLocalMcpDefinition(Guid pluginId) =>
        _pluginConfigs.TryGetValue(pluginId, out var plugin) ? ReadLocalDefinition(plugin) : null;

    private LocalMcpDefinition? ReadLocalDefinition(SyncPlugin plugin) =>
        LocalMcpConfig.FromConfigJson(plugin.Name, plugin.Description, plugin.ConfigJson, Decrypt);

    private string Encrypt(string value) => _dpapiHelper?.Encrypt(value) ?? value;

    private string Decrypt(string value) => _dpapiHelper?.Decrypt(value) ?? value;

    public LocalMcpStatus GetLocalMcpStatus(Guid pluginId)
    {
        lock (_handlers)
        {
            // A start that threw still leaves a registered handler, so the handler's own error — not its
            // presence — is what says whether the server is up.
            if (_handlers.TryGetValue(pluginId, out var handler) && handler is McpPluginToolHandler mcp)
                return new LocalMcpStatus(
                    mcp.LastError is null, mcp.DiscoveredTools, [.. mcp.GetTools().Select(t => t.Name)], mcp.LastError);
        }

        return new LocalMcpStatus(false, [], [],
            _startFailures.TryGetValue(pluginId, out var failure) ? failure : null);
    }

    public Task<McpProbeResult> ProbeLocalMcpAsync(LocalMcpDefinition definition, CancellationToken ct = default) =>
        McpServerProbe.ProbeAsync(definition, timeout: null, ct);

    public async Task<Guid> SaveLocalMcpAsync(Guid? pluginId, LocalMcpDefinition definition, CancellationToken ct = default)
    {
        var id = pluginId ?? Guid.NewGuid();
        var existing = _pluginConfigs.TryGetValue(id, out var found) ? found : null;

        if (existing is not null && !LocalMcpConfig.IsLocal(existing.ConfigJson))
            throw new InvalidOperationException($"Plugin {id} is not a locally added MCP server.");

        var stored = definition with { ToolPrefix = UniqueToolPrefix(definition, id) };

        var plugin = new SyncPlugin
        {
            Id = id,
            Kind = "mcp_server",
            Name = stored.Name,
            Description = stored.Description,
            ConfigJson = LocalMcpConfig.ToConfigJson(stored, Encrypt),
            Version = existing?.Version ?? "1.0.0",
            IsPreloaded = false,
            IsActive = true,
            UserEnabled = existing?.UserEnabled ?? stored.DefaultEnabled,
            UpdatedAt = DateTime.UtcNow
        };

        await ShutdownHandlerAsync(id);

        _pluginConfigs[id] = plugin;
        SavePluginToDb(plugin);

        if (IsPluginEnabled(plugin))
            await ActivateMcpPluginAsync(plugin);

        RebuildToolNameRoutes();
        PluginsChanged?.Invoke(this, EventArgs.Empty);
        _logger.LogInformation("Local MCP server {PluginId} saved with prefix {Prefix}", id, stored.ToolPrefix);
        return id;
    }

    public async Task RemoveLocalMcpAsync(Guid pluginId)
    {
        if (!_pluginConfigs.TryGetValue(pluginId, out var plugin) || !LocalMcpConfig.IsLocal(plugin.ConfigJson))
            return;

        await ShutdownHandlerAsync(pluginId);
        _pluginConfigs.Remove(pluginId);
        DeletePluginFromDb(pluginId);

        lock (_pendingPrefs)
            _pendingPrefs.RemoveAll(p => p.PluginId == pluginId);

        RebuildToolNameRoutes();
        PluginsChanged?.Invoke(this, EventArgs.Empty);
        _logger.LogInformation("Local MCP server {PluginId} removed", pluginId);
    }

    private async Task ShutdownHandlerAsync(Guid pluginId)
    {
        IPluginToolHandler? handler;
        lock (_handlers)
            _handlers.TryGetValue(pluginId, out handler);

        if (handler is null) return;

        try
        {
            await handler.ShutdownAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error shutting down handler for plugin {PluginId}", pluginId);
        }

        UnregisterHandler(pluginId);
        (handler as IDisposable)?.Dispose();
    }

    /// <summary>Two servers sharing a prefix would collide again on the very names the prefix exists to keep
    /// apart.</summary>
    private string UniqueToolPrefix(LocalMcpDefinition definition, Guid id)
    {
        var baseName = string.IsNullOrWhiteSpace(definition.ToolPrefix)
            ? LocalMcpConfig.DeriveToolPrefix(definition.Name)
            : definition.ToolPrefix;

        var taken = _pluginConfigs.Values
            .Where(p => p.Id != id && LocalMcpConfig.IsLocal(p.ConfigJson))
            .Select(p => ReadLocalDefinition(p)?.ToolPrefix)
            .Where(p => !string.IsNullOrEmpty(p))
            .ToHashSet(StringComparer.Ordinal);

        if (!taken.Contains(baseName)) return baseName;

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{baseName}_{suffix}";
            if (!taken.Contains(candidate)) return candidate;
        }
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
                await ActivateMcpPluginAsync(plugin);
            }
        }

        // Rebuild tool name routes
        RebuildToolNameRoutes();

        _logger.LogInformation("Applied server plugins: {Upserted} upserted, {Deleted} deleted",
            upserted.Count, deleted.Count);

        PluginsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task ActivateMcpPluginAsync(SyncPlugin plugin)
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

        var localDefinition = ReadLocalDefinition(plugin);
        _startFailures.TryRemove(plugin.Id, out _);

        if (localDefinition is not null && transport != "stdio")
        {
            _logger.LogWarning("Local MCP server {PluginName}: transport '{Transport}' is not supported, only stdio",
                plugin.Name, transport);
            _startFailures[plugin.Id] = $"Transport '{transport}' is not supported. Only stdio servers can be added here.";
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
                // A local server skips the PATH guess: the user picked the command, so the launch failure
                // itself is the answer they need, not `where.exe`'s opinion of it.
                if (!string.IsNullOrEmpty(command) && localDefinition is null)
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
            _logger,
            localDefinition?.Env,
            localDefinition?.WorkingDirectory,
            localDefinition?.ToolPrefix,
            localDefinition?.AllowedTools);

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
        // where.exe reads a rooted path as its own "directory:pattern" syntax and errors out, so an absolute
        // command has to be answered off the filesystem.
        if (Path.IsPathRooted(command))
            return File.Exists(command);

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

    public async Task SetPluginEnabledAsync(Guid pluginId, bool enabled)
    {
        if (_pluginConfigs.TryGetValue(pluginId, out var config))
        {
            config.UserEnabled = enabled;

            // Built-ins too: the server round-trip below is a push, so the row is the only thing that
            // carries the switch across a restart.
            SavePluginToDb(config);

            if (LocalMcpConfig.IsLocal(config.ConfigJson))
            {
                // The switch owns the subprocess for a local server — there is no admin push to start or
                // stop it on the user's behalf.
                if (enabled)
                    await ActivateMcpPluginAsync(config);
                else
                    await ShutdownHandlerAsync(pluginId);

                RebuildToolNameRoutes();
            }
            else
            {
                lock (_pendingPrefs)
                {
                    _pendingPrefs.RemoveAll(p => p.PluginId == pluginId);
                    _pendingPrefs.Add(new SyncPluginPreference { PluginId = pluginId, IsEnabled = enabled });
                }
            }

            _logger.LogInformation("Plugin {PluginId} enabled={Enabled}", pluginId, enabled);
            // The tool catalogue skips disabled plugins, so without this it keeps the pre-toggle shape.
            PluginsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public List<SyncPluginPreference> GetPendingPreferenceChanges()
    {
        // Peek only — do NOT drain here. Draining at request-build time loses the changes
        // whenever the push then fails (e.g. 403 e2ee_required, a transient 500, or the
        // no-op short-circuit). Mirrors SyncDeleteTrackerService: the queue is cleared by
        // ClearPreferenceChangesAfterSuccessfulPush once the push actually succeeds.
        lock (_pendingPrefs)
        {
            return new List<SyncPluginPreference>(_pendingPrefs);
        }
    }

    public void ClearPreferenceChangesAfterSuccessfulPush()
    {
        lock (_pendingPrefs)
        {
            _pendingPrefs.Clear();
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
