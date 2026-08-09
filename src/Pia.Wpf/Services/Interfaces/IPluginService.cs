using Microsoft.Extensions.AI;
using Pia.Shared.Models;

namespace Pia.Services.Interfaces;

/// <summary>One grantable tool as a pre-approval surface sees it — before any call, so with no
/// <c>PluginToolCall</c> to read the route or the server's hint off.</summary>
public sealed record ToolCatalogEntry(
    Guid PluginId,
    string PluginName,
    string ToolName,
    string? Description,
    bool IsExternalRoute,
    bool ServerDeclaredDestructive);

public interface IPluginService
{
    event EventHandler? PluginsChanged;
    IReadOnlyList<IPluginToolHandler> ActiveHandlers { get; }
    IList<AITool> GetAllTools();

    /// <summary>Every tool of every ENABLED plugin, with the two facts a grant offer needs: route and hint.</summary>
    IReadOnlyList<ToolCatalogEntry> GetToolCatalog();

    /// <summary>
    /// True if <paramref name="toolName"/> routes to an MCP handler. MCP tools return an immediate
    /// result and so bypass the unattended write-gate; they are disabled for headless/scheduled runs
    /// this milestone (§17.4 / G-2). The gate fix for MCP writes is Phase 2.
    /// </summary>
    bool IsMcpTool(string toolName);
    string GetCombinedSystemPromptAdditions();
    Task<(object? Result, PluginToolCall? PendingAction)?> RouteToolCallAsync(
        FunctionCallContent toolCall, CancellationToken ct = default);
    Task InitializePersistedPluginsAsync();
    Task ApplyServerPluginsAsync(IReadOnlyList<SyncPlugin> upserted, IReadOnlyList<Guid> deleted);
    Task SetPluginEnabledAsync(Guid pluginId, bool enabled);
    List<SyncPluginPreference> GetPendingPreferenceChanges();
    void ClearPreferenceChangesAfterSuccessfulPush();
    IReadOnlyList<SyncPlugin> GetAllPluginConfigs();
    Task ShutdownAllAsync();
}
