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
public class BuiltInPluginHandler : IPluginToolHandler
{
    private readonly Func<IList<AITool>> _getTools;
    private readonly Func<FunctionCallContent, CancellationToken, Task<(object?, PluginToolCall?)>> _handleCall;
    private readonly Func<PluginToolCall, Task<object?>> _executePending;
    private readonly Func<bool>? _isAvailable;
    private string? _systemPromptAddition;

    public Guid PluginId { get; }
    public string PluginName { get; private set; }

    public BuiltInPluginHandler(
        Guid pluginId,
        string pluginName,
        Func<IList<AITool>> getTools,
        Func<FunctionCallContent, CancellationToken, Task<(object?, PluginToolCall?)>> handleCall,
        Func<PluginToolCall, Task<object?>> executePending,
        string? systemPromptAddition,
        Func<bool>? isAvailable = null)
    {
        PluginId = pluginId;
        PluginName = pluginName;
        _getTools = getTools;
        _handleCall = handleCall;
        _executePending = executePending;
        _systemPromptAddition = systemPromptAddition;
        _isAvailable = isAvailable;
    }

    public IList<AITool> GetTools() => _isAvailable is null || _isAvailable() ? _getTools() : [];

    public string? GetSystemPromptAddition() =>
        _isAvailable is null || _isAvailable() ? _systemPromptAddition : null;

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
    public static BuiltInPluginHandler FromMemoryHandler(
        IMemoryToolHandler handler, SyncPlugin config)
    {
        return new BuiltInPluginHandler(
            config.Id,
            config.Name,
            handler.GetTools,
            async (toolCall, ct) =>
            {
                var (result, pending) = await handler.HandleToolCallAsync(toolCall, ct);
                if (pending is null) return (result, null);
                return (null, new PluginToolCall(
                    pending.ToolName, config.Id, config.Name, pending.Description, pending.NewValue, pending.Execute,
                    pending.DiffPreview, pending.TargetPath));
            },
            async pluginCall => await pluginCall.Execute(),
            GetSystemPromptFromConfig(config.ConfigJson));
    }

    /// <summary>
    /// Factory: creates adapter wrapping ITodoToolHandler.
    /// </summary>
    public static BuiltInPluginHandler FromTodoHandler(
        ITodoToolHandler handler, SyncPlugin config)
    {
        return new BuiltInPluginHandler(
            config.Id,
            config.Name,
            handler.GetTools,
            async (toolCall, ct) =>
            {
                var (result, pending) = await handler.HandleToolCallAsync(toolCall, ct);
                if (pending is null) return (result, null);
                return (null, new PluginToolCall(
                    pending.ToolName, config.Id, config.Name, pending.Description, pending.Details, pending.Execute));
            },
            async pluginCall => await pluginCall.Execute(),
            GetSystemPromptFromConfig(config.ConfigJson));
    }

    /// <summary>
    /// Factory: creates adapter wrapping IReminderToolHandler.
    /// </summary>
    public static BuiltInPluginHandler FromReminderHandler(
        IReminderToolHandler handler, SyncPlugin config)
    {
        return new BuiltInPluginHandler(
            config.Id,
            config.Name,
            handler.GetTools,
            async (toolCall, ct) =>
            {
                var (result, pending) = await handler.HandleToolCallAsync(toolCall, ct);
                if (pending is null) return (result, null);
                return (null, new PluginToolCall(
                    pending.ToolName, config.Id, config.Name, pending.Description, pending.Details, pending.Execute));
            },
            async pluginCall => await pluginCall.Execute(),
            GetSystemPromptFromConfig(config.ConfigJson));
    }

    /// <summary>
    /// Factory: creates adapter wrapping IScheduledJobToolHandler.
    /// </summary>
    public static BuiltInPluginHandler FromScheduledJobHandler(
        IScheduledJobToolHandler handler, SyncPlugin config)
    {
        return new BuiltInPluginHandler(
            config.Id,
            config.Name,
            handler.GetTools,
            async (toolCall, ct) =>
            {
                var (result, pending) = await handler.HandleToolCallAsync(toolCall, ct);
                if (pending is null) return (result, null);
                return (null, new PluginToolCall(
                    pending.ToolName, config.Id, config.Name, pending.Description, pending.Details, pending.Execute));
            },
            async pluginCall => await pluginCall.Execute(),
            GetSystemPromptFromConfig(config.ConfigJson));
    }

    /// <summary>
    /// Factory: creates adapter wrapping IFilesToolHandler. The files plugin is
    /// only exposed when the user has configured a sandbox folder — when that
    /// path is empty, both <c>GetTools</c> and the system-prompt addition are
    /// suppressed so the model doesn't even see the tool.
    /// </summary>
    public static BuiltInPluginHandler FromFilesHandler(
        IFilesToolHandler handler, SyncPlugin config)
    {
        return new BuiltInPluginHandler(
            config.Id,
            config.Name,
            handler.GetTools,
            async (toolCall, ct) =>
            {
                var (result, pending) = await handler.HandleToolCallAsync(toolCall, ct);
                if (pending is null) return (result, null);
                return (null, new PluginToolCall(
                    pending.ToolName, config.Id, config.Name, pending.Description, pending.Details, pending.Execute,
                    pending.DiffPreview, pending.TargetPath));
            },
            async pluginCall => await pluginCall.Execute(),
            GetSystemPromptFromConfig(config.ConfigJson),
            isAvailable: () => handler.IsAvailable);
    }

    /// <summary>
    /// Factory: creates adapter wrapping IGitToolHandler. Like the files plugin, the git plugin is only
    /// exposed when it is available (git installed + enabled + a sandbox folder configured) — when
    /// unavailable, both <c>GetTools</c> and the system-prompt addition are suppressed. Read-only git
    /// tools run inline; mutating ones return a pending action carrying a <c>Details</c> string (no diff
    /// preview), so this mirrors the todo/reminder factory shape.
    /// </summary>
    public static BuiltInPluginHandler FromGitHandler(
        IGitToolHandler handler, SyncPlugin config)
    {
        return new BuiltInPluginHandler(
            config.Id,
            config.Name,
            handler.GetTools,
            async (toolCall, ct) =>
            {
                var (result, pending) = await handler.HandleToolCallAsync(toolCall, ct);
                if (pending is null) return (result, null);
                return (null, new PluginToolCall(
                    pending.ToolName, config.Id, config.Name, pending.Description, pending.Details, pending.Execute,
                    DiffPreview: null, TargetPath: pending.TargetPath));
            },
            async pluginCall => await pluginCall.Execute(),
            GetSystemPromptFromConfig(config.ConfigJson),
            isAvailable: () => handler.IsAvailable);
    }

    /// <summary>
    /// Factory: creates adapter wrapping IIngestToolHandler. Ingest runs inline (no pending-action
    /// confirmation card): the handler returns a plain result, so handleCall adapts it to a
    /// (result, no-pending) tuple and executePending is unreachable.
    /// </summary>
    public static BuiltInPluginHandler FromIngestHandler(
        IIngestToolHandler handler, SyncPlugin config)
    {
        return new BuiltInPluginHandler(
            config.Id,
            config.Name,
            handler.GetTools,
            async (toolCall, ct) => (await handler.HandleToolCallAsync(toolCall, ct), (PluginToolCall?)null),
            _ => throw new InvalidOperationException("The ingest plugin has no pending actions."),
            GetSystemPromptFromConfig(config.ConfigJson));
    }

    /// <summary>Factory: creates adapter wrapping IChatHistoryToolHandler — inline-only like ingest, but
    /// with git's availability gate, so the off switch suppresses both the tools and the prompt.</summary>
    public static BuiltInPluginHandler FromChatHistoryHandler(
        IChatHistoryToolHandler handler, SyncPlugin config)
    {
        return new BuiltInPluginHandler(
            config.Id,
            config.Name,
            handler.GetTools,
            async (toolCall, ct) => (await handler.HandleToolCallAsync(toolCall, ct), (PluginToolCall?)null),
            _ => throw new InvalidOperationException("The chat-history plugin has no pending actions."),
            GetSystemPromptFromConfig(config.ConfigJson),
            isAvailable: () => handler.IsAvailable);
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
