using Microsoft.Extensions.AI;

namespace Pia.Services.Interfaces;

/// <summary>
/// A prepared git mutating action awaiting user approval. Read-only git tools run inline and never
/// produce one of these; mutating tools (init/add/commit/switch/restore/stash) return one so the host
/// can surface a confirmation card, then invoke <see cref="Execute"/> on approval. <see cref="Execute"/>
/// re-runs the sandbox-containment guard before spawning git (the sandbox root is mutable at runtime).
/// </summary>
public record GitToolCall(
    string ToolName,
    string Description,
    string? Details,
    Func<Task<object?>> Execute,
    string? TargetPath = null);

public interface IGitToolHandler
{
    /// <summary>
    /// True when git is installed, the git tools are enabled, and a sandbox folder is configured. When
    /// false, the plugin host suppresses both tool registration and the system-prompt addition.
    /// </summary>
    bool IsAvailable { get; }

    IList<AITool> GetTools();

    Task<(object? Result, GitToolCall? PendingAction)> HandleToolCallAsync(
        FunctionCallContent toolCall,
        CancellationToken cancellationToken = default);

    Task<object?> ExecutePendingActionAsync(GitToolCall pendingAction);
}
