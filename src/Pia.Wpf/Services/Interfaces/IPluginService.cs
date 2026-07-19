using Microsoft.Extensions.AI;
using Pia.Shared.Models;

namespace Pia.Services.Interfaces;

public interface IPluginService
{
    event EventHandler? PluginsChanged;
    IReadOnlyList<IPluginToolHandler> ActiveHandlers { get; }
    IList<AITool> GetAllTools();

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
