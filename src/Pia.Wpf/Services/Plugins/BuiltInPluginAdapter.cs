using System.Text.Json;
using Microsoft.Extensions.AI;
using Pia.Services.Interfaces;
using Pia.Shared.Models;

namespace Pia.Services.Plugins;

/// <summary>
/// Wraps an existing tool handler (Memory, Todo, Reminder) as an IPluginToolHandler.
/// Applies server-provided metadata overrides (system prompt, descriptions) without
/// changing the underlying handler code.
/// </summary>
public class BuiltInPluginAdapter : IPluginToolHandler
{
    private readonly Func<IList<AITool>> _getTools;
    private readonly Func<FunctionCallContent, CancellationToken, Task<(object?, PluginToolCall?)>> _handleCall;
    private readonly Func<PluginToolCall, Task<object?>> _executePending;
    private string? _systemPromptAddition;

    public Guid PluginId { get; }
    public string PluginName { get; private set; }

    public BuiltInPluginAdapter(
        Guid pluginId,
        string pluginName,
        Func<IList<AITool>> getTools,
        Func<FunctionCallContent, CancellationToken, Task<(object?, PluginToolCall?)>> handleCall,
        Func<PluginToolCall, Task<object?>> executePending,
        string? systemPromptAddition)
    {
        PluginId = pluginId;
        PluginName = pluginName;
        _getTools = getTools;
        _handleCall = handleCall;
        _executePending = executePending;
        _systemPromptAddition = systemPromptAddition;
    }

    public IList<AITool> GetTools() => _getTools();

    public string? GetSystemPromptAddition() => _systemPromptAddition;

    public Task<(object? Result, PluginToolCall? PendingAction)> HandleToolCallAsync(
        FunctionCallContent toolCall, CancellationToken ct = default)
        => _handleCall(toolCall, ct);

    public Task<object?> ExecutePendingActionAsync(PluginToolCall pendingAction)
        => _executePending(pendingAction);

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task ShutdownAsync() => Task.CompletedTask;

    public void ApplyServerMetadata(SyncPlugin plugin)
    {
        PluginName = plugin.Name;

        try
        {
            using var doc = JsonDocument.Parse(plugin.ConfigJson);
            if (doc.RootElement.TryGetProperty("systemPromptAddition", out var promptEl))
                _systemPromptAddition = promptEl.GetString();
        }
        catch
        {
            // If ConfigJson is malformed, keep existing prompt
        }
    }

    /// <summary>
    /// Factory: creates adapter wrapping IMemoryToolHandler.
    /// </summary>
    public static BuiltInPluginAdapter FromMemoryHandler(
        IMemoryToolHandler handler, SyncPlugin config)
    {
        return new BuiltInPluginAdapter(
            config.Id,
            config.Name,
            handler.GetTools,
            async (toolCall, ct) =>
            {
                var (result, pending) = await handler.HandleToolCallAsync(toolCall, ct);
                if (pending is null) return (result, null);
                return (null, new PluginToolCall(
                    pending.ToolName, config.Name, pending.Description, pending.NewValue, pending.Execute));
            },
            async pluginCall => await pluginCall.Execute(),
            GetSystemPromptFromConfig(config.ConfigJson));
    }

    /// <summary>
    /// Factory: creates adapter wrapping ITodoToolHandler.
    /// </summary>
    public static BuiltInPluginAdapter FromTodoHandler(
        ITodoToolHandler handler, SyncPlugin config)
    {
        return new BuiltInPluginAdapter(
            config.Id,
            config.Name,
            handler.GetTools,
            async (toolCall, ct) =>
            {
                var (result, pending) = await handler.HandleToolCallAsync(toolCall, ct);
                if (pending is null) return (result, null);
                return (null, new PluginToolCall(
                    pending.ToolName, config.Name, pending.Description, pending.Details, pending.Execute));
            },
            async pluginCall => await pluginCall.Execute(),
            GetSystemPromptFromConfig(config.ConfigJson));
    }

    /// <summary>
    /// Factory: creates adapter wrapping IReminderToolHandler.
    /// </summary>
    public static BuiltInPluginAdapter FromReminderHandler(
        IReminderToolHandler handler, SyncPlugin config)
    {
        return new BuiltInPluginAdapter(
            config.Id,
            config.Name,
            handler.GetTools,
            async (toolCall, ct) =>
            {
                var (result, pending) = await handler.HandleToolCallAsync(toolCall, ct);
                if (pending is null) return (result, null);
                return (null, new PluginToolCall(
                    pending.ToolName, config.Name, pending.Description, pending.Details, pending.Execute));
            },
            async pluginCall => await pluginCall.Execute(),
            GetSystemPromptFromConfig(config.ConfigJson));
    }

    private static string? GetSystemPromptFromConfig(string configJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(configJson);
            if (doc.RootElement.TryGetProperty("systemPromptAddition", out var el))
                return el.GetString();
        }
        catch { }
        return null;
    }
}
