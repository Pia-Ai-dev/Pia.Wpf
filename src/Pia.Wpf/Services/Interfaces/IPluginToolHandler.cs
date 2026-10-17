using Microsoft.Extensions.AI;
using Pia.Models;
using Pia.Services.Plugins.Sandbox;
using Pia.Shared.Models;

namespace Pia.Services.Interfaces;

/// <param name="ServerDeclaredDestructive">The MCP server declared the tool destructive; it can only tighten the gate, never loosen it.</param>
/// <param name="Warning">A caution the handler composed; shown beside any warning the card derives itself, never replacing it.</param>
/// <param name="Sandbox">The policy the tool's server runs under; null for a server that is not sandboxed.</param>
public record PluginToolCall(
    string ToolName,
    Guid PluginId,
    string PluginName,
    string Description,
    string? Details,
    Func<Task<object?>> Execute,
    IReadOnlyList<DiffLine>? DiffPreview = null,
    string? TargetPath = null,
    bool ServerDeclaredDestructive = false,
    string? Warning = null,
    McpSandboxPolicy? Sandbox = null);

public interface IPluginToolHandler
{
    Guid PluginId { get; }
    string PluginName { get; }
    IList<AITool> GetTools();
    string? GetSystemPromptAddition();
    Task<(object? Result, PluginToolCall? PendingAction)> HandleToolCallAsync(
        FunctionCallContent toolCall, CancellationToken ct = default);
    Task<object?> ExecutePendingActionAsync(PluginToolCall pendingAction);
    Task InitializeAsync(CancellationToken ct = default);
    Task ShutdownAsync();
    void ApplyServerMetadata(SyncPlugin plugin);

    /// <summary>The server's own destructive declaration for one tool. False means "no hint": a built-in has none.</summary>
    bool DeclaresDestructive(string toolName) => false;
}
