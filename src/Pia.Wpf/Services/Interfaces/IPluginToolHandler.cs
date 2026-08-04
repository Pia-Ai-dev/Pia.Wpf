using Microsoft.Extensions.AI;
using Pia.Models;
using Pia.Shared.Models;

namespace Pia.Services.Interfaces;

/// <param name="ServerDeclaredDestructive">
/// T2-7b — the MCP server said so itself (<c>ToolAnnotations.DestructiveHint</c>), via
/// <c>McpPluginToolHandler.IsServerDeclaredDestructive</c>, which is this member's only producer. It can only
/// ever TIGHTEN: it widens <c>ToolPermissionService.IsDeleteLike</c> for this one call, and no value of it can
/// loosen anything (see that method for why a hint from an untrusted server is safe to believe in exactly one
/// direction).
/// <para>
/// Trailing and defaulted to <c>false</c>, unlike the gate input it feeds: every OTHER handler builds a
/// built-in tool call, where "the server declared it destructive" is not a fact that exists, and the name
/// heuristic remains the whole rule. A default here is therefore "no hint available", not an unanswered
/// question — the question is forced where it matters, at <c>ToolGateInput</c>, whose member is required.
/// </para>
/// </param>
public record PluginToolCall(
    string ToolName,
    Guid PluginId,
    string PluginName,
    string Description,
    string? Details,
    Func<Task<object?>> Execute,
    IReadOnlyList<DiffLine>? DiffPreview = null,
    string? TargetPath = null,
    bool ServerDeclaredDestructive = false);

public interface IPluginToolHandler
{
    Guid PluginId { get; }
    string PluginName { get; }
    IList<AITool> GetTools();
    string? GetSystemPromptAddition();
    Task<(object? Result, PluginToolCall? PendingAction)> HandleToolCallAsync(
        FunctionCallContent toolCall, CancellationToken ct = default);
    Task<object?> ExecutePendingActionAsync(PluginToolCall pendingAction);
    Task InitializeAsync(CancellationToken ct = default);
    Task ShutdownAsync();
    void ApplyServerMetadata(SyncPlugin plugin);
}
