using Microsoft.Extensions.AI;
using Pia.Shared.Models;

namespace Pia.Services.Interfaces;

public interface IPluginService
{
    IReadOnlyList<IPluginToolHandler> ActiveHandlers { get; }
    IList<AITool> GetAllTools();
    string GetCombinedSystemPromptAdditions();
    Task<(object? Result, PluginToolCall? PendingAction)?> RouteToolCallAsync(
        FunctionCallContent toolCall, CancellationToken ct = default);
    Task ApplyServerPluginsAsync(IReadOnlyList<SyncPlugin> upserted, IReadOnlyList<Guid> deleted);
    Task SetPluginEnabledAsync(Guid pluginId, bool enabled);
    List<SyncPluginPreference> GetPendingPreferenceChanges();
    Task ShutdownAllAsync();
}
