using System.Text.Json;
using Pia.Shared.Models;

namespace Pia.Services.Plugins;

/// <summary>Whether a plugin counts as switched on here, for its settings row as much as for its process.</summary>
public static class PluginEnablement
{
    public static bool IsEnabled(SyncPlugin config)
    {
        if (!config.IsActive)
            return false;

        if (config.UserEnabled.HasValue)
            return config.UserEnabled.Value;

        try
        {
            using var doc = JsonDocument.Parse(config.ConfigJson);
            if (doc.RootElement.TryGetProperty("defaultEnabled", out var el))
                return el.GetBoolean();
        }
        catch { }

        // A distributed MCP server runs a process here, so without the admin's explicit default it waits for the user.
        return !(config is { IsPreloaded: false, Kind: "mcp_server" } && !LocalMcpConfig.IsLocal(config.ConfigJson));
    }
}
