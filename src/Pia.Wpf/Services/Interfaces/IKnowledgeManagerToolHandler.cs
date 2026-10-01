using Microsoft.Extensions.AI;

namespace Pia.Services.Interfaces;

/// <param name="Warning">Shown on the card: the content is unencrypted, and the KB is shared.</param>
public record KbManagerToolCall(
    string ToolName,
    string Description,
    string? Details,
    string? Warning,
    Func<Task<object?>> Execute);

public interface IKnowledgeManagerToolHandler
{
    /// <summary>A read of the cached surface, never an HTTP probe.</summary>
    bool IsAvailable { get; }

    IList<AITool> GetTools();

    Task<(object? Result, KbManagerToolCall? PendingAction)> HandleToolCallAsync(
        FunctionCallContent toolCall,
        CancellationToken cancellationToken = default);
}
