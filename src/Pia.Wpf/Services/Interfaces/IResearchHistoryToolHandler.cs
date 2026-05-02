using Microsoft.Extensions.AI;

namespace Pia.Services.Interfaces;

public record ResearchHistoryToolCall(
    string ToolName,
    string Description,
    string? Details,
    Func<Task<object?>> Execute);

public interface IResearchHistoryToolHandler
{
    IList<AITool> GetTools();
    Task<(object? Result, ResearchHistoryToolCall? PendingAction)> HandleToolCallAsync(
        FunctionCallContent toolCall,
        CancellationToken cancellationToken = default);
    Task<object?> ExecutePendingActionAsync(ResearchHistoryToolCall pendingAction);
}
