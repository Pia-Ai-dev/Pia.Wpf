using Microsoft.Extensions.AI;
using Pia.Shared.Models;

namespace Pia.Services.Interfaces;

public record PluginToolCall(
    string ToolName,
    Guid PluginId,
    string PluginName,
    string Description,
    string? Details,
    Func<Task<object?>> Execute);

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
}
