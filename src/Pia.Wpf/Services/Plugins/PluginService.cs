using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Services.Interfaces;
using Pia.Shared.Models;

namespace Pia.Services.Plugins;

public class PluginService : IPluginService
{
    private readonly IMemoryToolHandler _memoryToolHandler;
    private readonly ITodoToolHandler _todoToolHandler;
    private readonly IReminderToolHandler _reminderToolHandler;
    private readonly ILogger<PluginService> _logger;

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
        ILogger<PluginService> logger)
    {
        _memoryToolHandler = memoryToolHandler;
        _todoToolHandler = todoToolHandler;
        _reminderToolHandler = reminderToolHandler;
        _logger = logger;

        InitializeBuiltInPlugins();
    }

    private void InitializeBuiltInPlugins()
    {
        foreach (var (id, config) in BuiltInPluginDefaults.Defaults)
        {
            _pluginConfigs[id] = config;

            IPluginToolHandler adapter = GetHandlerId(config.ConfigJson) switch
            {
                "memory" => BuiltInPluginAdapter.FromMemoryHandler(_memoryToolHandler, config),
                "todo" => BuiltInPluginAdapter.FromTodoHandler(_todoToolHandler, config),
                "reminder" => BuiltInPluginAdapter.FromReminderHandler(_reminderToolHandler, config),
                _ => throw new InvalidOperationException($"Unknown built-in handler for plugin {config.Name}")
            };

            RegisterHandler(id, adapter);
        }

        _logger.LogInformation("PluginService initialized with {Count} built-in plugins", _handlers.Count);
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
                    continue;
                tools.AddRange(handler.GetTools());
            }
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
            _logger.LogWarning("No plugin handler found for tool {ToolName}", toolCall.Name);
            return null;
        }

        return await handler.HandleToolCallAsync(toolCall, ct);
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
                _pluginConfigs.Remove(id);
                _logger.LogInformation("Removed plugin {PluginId}", id);
            }
        }

        // Handle upserts
        foreach (var plugin in upserted)
        {
            _pluginConfigs[plugin.Id] = plugin;

            if (_handlers.TryGetValue(plugin.Id, out var existing))
            {
                // Update existing handler metadata
                existing.ApplyServerMetadata(plugin);
                _logger.LogDebug("Updated metadata for plugin {PluginName}", plugin.Name);
            }
            else if (!plugin.IsPreloaded)
            {
                // New server-only plugin — create handler based on kind
                // TODO: implement RestApiPluginToolHandler, McpPluginToolHandler
                _logger.LogInformation("Server-only plugin {PluginName} ({Kind}) received but handler not yet implemented",
                    plugin.Name, plugin.Kind);
            }
        }

        // Rebuild tool name routes
        RebuildToolNameRoutes();

        _logger.LogInformation("Applied server plugins: {Upserted} upserted, {Deleted} deleted",
            upserted.Count, deleted.Count);
    }

    public Task SetPluginEnabledAsync(Guid pluginId, bool enabled)
    {
        if (_pluginConfigs.TryGetValue(pluginId, out var config))
        {
            config.UserEnabled = enabled;
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
