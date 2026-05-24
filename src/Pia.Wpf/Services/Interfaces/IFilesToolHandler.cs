using Microsoft.Extensions.AI;

namespace Pia.Services.Interfaces;

public record FilesToolCall(
    string ToolName,
    string Description,
    string? Details,
    string? TargetPath,
    Func<Task<object?>> Execute);

public interface IFilesToolHandler
{
    /// <summary>
    /// True when a usable sandbox folder is configured. When false, the plugin host
    /// suppresses both tool registration and the system-prompt addition.
    /// </summary>
    bool IsAvailable { get; }
    IList<AITool> GetTools();
    Task<(object? Result, FilesToolCall? PendingAction)> HandleToolCallAsync(
        FunctionCallContent toolCall,
        CancellationToken cancellationToken = default);
    Task<object?> ExecutePendingActionAsync(FilesToolCall pendingAction);
}
