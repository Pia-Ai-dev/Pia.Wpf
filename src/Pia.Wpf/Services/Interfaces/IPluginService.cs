using Microsoft.Extensions.AI;
using Pia.Services.Plugins;
using Pia.Shared.Models;

namespace Pia.Services.Interfaces;

/// <summary>What a locally added server is doing right now. <paramref name="DiscoveredTools"/> is everything
/// it reported; <paramref name="ActiveTools"/> is what survived the allowlist, already prefixed.</summary>
public sealed record LocalMcpStatus(
    bool IsActive,
    IReadOnlyList<string> DiscoveredTools,
    IReadOnlyList<string> ActiveTools);

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

    /// <summary>Every tool of every ENABLED plugin. Of these the grant offers read only the server's
    /// destructive hint; the route is carried for other consumers.</summary>
    IReadOnlyList<ToolCatalogEntry> GetToolCatalog();

    /// <summary>True if <paramref name="toolName"/> routes to an MCP handler — what classifies a call as
    /// External for <c>ToolAutonomy.Resolve</c>.</summary>
    bool IsMcpTool(string toolName);

    /// <summary>
    /// A handler owning any tool named in <paramref name="excludedToolNames"/> is skipped whole: a turn that
    /// withholds a tool family must not carry prose telling the model to use it.
    /// </summary>
    string GetCombinedSystemPromptAdditions(IReadOnlySet<string>? excludedToolNames = null);
    Task<(object? Result, PluginToolCall? PendingAction)?> RouteToolCallAsync(
        FunctionCallContent toolCall, CancellationToken ct = default);
    Task InitializePersistedPluginsAsync();
    Task ApplyServerPluginsAsync(IReadOnlyList<SyncPlugin> upserted, IReadOnlyList<Guid> deleted);
    Task SetPluginEnabledAsync(Guid pluginId, bool enabled);
    List<SyncPluginPreference> GetPendingPreferenceChanges();
    void ClearPreferenceChangesAfterSuccessfulPush();
    IReadOnlyList<SyncPlugin> GetAllPluginConfigs();
    Task ShutdownAllAsync();

    /// <summary>MCP servers the user added on this machine, as opposed to the ones an admin pushes down
    /// sync. Their tool names are prefixed, so they can never collide with a built-in.</summary>
    IReadOnlyList<SyncPlugin> GetLocalMcpPlugins();
    LocalMcpDefinition? GetLocalMcpDefinition(Guid pluginId);
    LocalMcpStatus GetLocalMcpStatus(Guid pluginId);

    /// <summary>Starts the server, lists its tools and shuts it back down, without touching the catalogue.</summary>
    Task<McpProbeResult> ProbeLocalMcpAsync(LocalMcpDefinition definition, CancellationToken ct = default);

    /// <summary>Adds or replaces a local server, restarting its process. Returns the id, minted when
    /// <paramref name="pluginId"/> is null.</summary>
    Task<Guid> SaveLocalMcpAsync(Guid? pluginId, LocalMcpDefinition definition, CancellationToken ct = default);
    Task RemoveLocalMcpAsync(Guid pluginId);
}
