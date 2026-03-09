using Microsoft.Extensions.AI;

namespace Pia.Services.Interfaces;

public record TodoToolCall(
    string ToolName,
    string Description,
    string? Details,
    Guid? TargetTodoId,
    Func<Task<object?>> Execute);

public interface ITodoToolHandler
{
    IList<AITool> GetTools();
    Task<(object? Result, TodoToolCall? PendingAction)> HandleToolCallAsync(
        FunctionCallContent toolCall,
        CancellationToken cancellationToken = default);
    Task<object?> ExecutePendingActionAsync(TodoToolCall pendingAction);
}
