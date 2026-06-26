namespace Pia.Models;

/// <summary>
/// A standing "always allow" grant for a specific tool of a specific plugin.
/// Keyed by <see cref="PluginId"/> + <see cref="ToolName"/> (never by tool name
/// alone) so a tool name that rebinds to a different plugin across installs does
/// not inherit the old owner's grant.
/// </summary>
public record ToolGrant(Guid PluginId, string ToolName, DateTimeOffset GrantedAt)
{
    /// <summary>True if this grant is for the given (plugin, tool) key. Ordinal tool-name match.</summary>
    public bool Matches(Guid pluginId, string toolName)
        => PluginId == pluginId && ToolName == toolName;
}
