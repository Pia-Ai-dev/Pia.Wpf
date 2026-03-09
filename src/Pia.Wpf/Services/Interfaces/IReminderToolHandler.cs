using Microsoft.Extensions.AI;

namespace Pia.Services.Interfaces;

public record ReminderToolCall(
    string ToolName,
    string Description,
    string? Details,
    Guid? TargetReminderId,
    Func<Task<object?>> Execute);

public interface IReminderToolHandler
{
    IList<AITool> GetTools();
    Task<(object? Result, ReminderToolCall? PendingAction)> HandleToolCallAsync(
        FunctionCallContent toolCall,
        CancellationToken cancellationToken = default);
    Task<object?> ExecutePendingActionAsync(ReminderToolCall pendingAction);
}
