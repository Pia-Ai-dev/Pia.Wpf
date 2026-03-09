using Microsoft.Extensions.AI;

namespace Pia.Services.Interfaces;

public record MemoryToolCall(
    string ToolName,
    string Description,
    string? OldValue,
    string? NewValue,
    Guid? TargetObjectId,
    Func<Task<object?>> Execute);

public interface IMemoryToolHandler
{
    IList<AITool> GetTools();
    Task<(object? Result, MemoryToolCall? PendingAction)> HandleToolCallAsync(
        FunctionCallContent toolCall,
        CancellationToken cancellationToken = default);
    Task<object?> ExecutePendingActionAsync(MemoryToolCall pendingAction);
}
