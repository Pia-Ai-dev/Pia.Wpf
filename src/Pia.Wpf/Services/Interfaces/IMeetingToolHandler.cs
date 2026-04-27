using Microsoft.Extensions.AI;
using Pia.Models;

namespace Pia.Services.Interfaces;

public record MeetingToolCall(
    string ToolName,
    string Description,
    string? Details,
    IReadOnlyList<ActionCardChoice>? Choices,
    Func<string?, Task<object?>> Execute);

public interface IMeetingToolHandler
{
    IList<AITool> GetTools();
    Task<(object? Result, MeetingToolCall? PendingAction)> HandleToolCallAsync(
        FunctionCallContent toolCall,
        CancellationToken cancellationToken = default);
}
