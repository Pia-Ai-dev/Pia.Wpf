using Microsoft.Extensions.AI;

namespace Pia.Services.Interfaces;

/// <summary>One assignment tool call the user has still to affirm. <c>Execute</c> is the whole side effect.</summary>
public record AssignmentToolCall(
    string ToolName,
    string Description,
    string? Details,
    Func<Task<object?>> Execute);

public interface IAssignmentToolHandler
{
    /// <summary>Non-blocking: a read of the cached surface, never an HTTP probe.</summary>
    bool IsAvailable { get; }

    IList<AITool> GetTools();

    Task<(object? Result, AssignmentToolCall? PendingAction)> HandleToolCallAsync(
        FunctionCallContent toolCall,
        CancellationToken cancellationToken = default);

    Task<object?> ExecutePendingActionAsync(AssignmentToolCall pendingAction);
}
