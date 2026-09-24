using Microsoft.Extensions.AI;

namespace Pia.Services.Interfaces;

/// <summary>One screen capture the user has still to approve. <c>Execute</c> is the capture itself.</summary>
public record ScreenCaptureToolCall(
    string ToolName,
    string Description,
    string? Details,
    Func<Task<object?>> Execute);

public interface IScreenCaptureToolHandler
{
    bool IsAvailable { get; }

    IList<AITool> GetTools();

    Task<(object? Result, ScreenCaptureToolCall? PendingAction)> HandleToolCallAsync(
        FunctionCallContent toolCall,
        CancellationToken cancellationToken = default);

    Task<object?> ExecutePendingActionAsync(ScreenCaptureToolCall pendingAction);
}
