using Microsoft.Extensions.AI;
using Pia.Models;

namespace Pia.Services.Interfaces;

public record FilesToolCall(
    string ToolName,
    string Description,
    string? Details,
    string? TargetPath,
    Func<Task<object?>> Execute,
    IReadOnlyList<DiffLine>? DiffPreview = null);

public interface IFilesToolHandler
{
    /// <summary>
    /// True when a usable sandbox folder is configured. When false, the plugin host
    /// suppresses both tool registration and the system-prompt addition.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Enumerates files in the sandbox folder for the <c>@Files</c> autocomplete picker,
    /// applying the same containment + sensitive-path filtering as <c>list_files</c> so the
    /// picker and the tools agree on which files exist. Returns sandbox-relative paths using
    /// forward slashes (so the model can copy them into a tool argument without backslash
    /// escape corruption), optionally filtered by a case-insensitive substring, capped at
    /// <paramref name="max"/>. Returns empty when no folder is configured.
    /// </summary>
    IReadOnlyList<string> ListRelativeFiles(string? filter, int max);

    /// <summary>
    /// Working subpath of the chat currently shown in the UI, used to scope
    /// <see cref="ListRelativeFiles"/> (the <c>@Files</c> autocomplete) to the active chat's
    /// working directory. The autocomplete runs outside any turn, so it cannot read the
    /// per-turn ambient; the view model sets this on active-chat change / re-point.
    /// Null/empty = sandbox root.
    /// </summary>
    string? ActiveUiWorkingSubpath { get; set; }

    IList<AITool> GetTools();
    Task<(object? Result, FilesToolCall? PendingAction)> HandleToolCallAsync(
        FunctionCallContent toolCall,
        CancellationToken cancellationToken = default);
    Task<object?> ExecutePendingActionAsync(FilesToolCall pendingAction);
}
