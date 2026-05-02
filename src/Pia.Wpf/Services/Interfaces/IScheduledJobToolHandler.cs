using Microsoft.Extensions.AI;

namespace Pia.Services.Interfaces;

public record ScheduledJobToolCall(
    string ToolName,
    string Description,
    string? Details,
    Guid? TargetJobId,
    Func<Task<object?>> Execute);

public interface IScheduledJobToolHandler
{
    IList<AITool> GetTools();
    Task<(object? Result, ScheduledJobToolCall? PendingAction)> HandleToolCallAsync(
        FunctionCallContent toolCall,
        CancellationToken cancellationToken = default);
    Task<object?> ExecutePendingActionAsync(ScheduledJobToolCall pendingAction);
}
