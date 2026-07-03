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

/// <summary>
/// A bounded, read-only preview of a sandbox file, used to inject file content directly into
/// the prompt when the user tags it with an <c>@Files</c> command — so a model that won't call
/// <c>read_file</c> on its own still sees the file. Carries enough metadata for the caller to
/// render a header telling the model how much of the file it is seeing and whether more remains.
/// A failed read (missing/blocked/binary/too large) is reported as <see cref="Found"/> = false
/// with a human-readable <see cref="Error"/> rather than thrown.
/// </summary>
public sealed record FilePromptPreview(
    string RequestedPath,
    bool Found,
    string? Text,
    int TotalLines,
    int ShownLines,
    bool Truncated,
    string? Error,
    string? AbsolutePath = null);

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

    /// <summary>
    /// Reads a bounded preview — the first <paramref name="maxLines"/> lines, additionally capped
    /// at an internal character budget — of the sandbox file at <paramref name="relativePath"/>,
    /// scoped to <paramref name="workingSubpath"/> (the active chat's working directory). Applies
    /// the same containment, sensitive-path, binary, and size guards as <c>read_file</c>. Used to
    /// inject file content directly into the prompt for an <c>@Files</c> command. Never throws for
    /// an unreadable file — returns a <see cref="FilePromptPreview"/> with <c>Found = false</c> and
    /// a human-readable <c>Error</c> instead. Does not record a staleness read: a preview is partial
    /// and runs during turn setup (outside the per-turn ambient), so an edit must still re-read.
    /// </summary>
    Task<FilePromptPreview> ReadPromptPreviewAsync(
        string relativePath, string? workingSubpath, int maxLines, CancellationToken cancellationToken = default);
}
