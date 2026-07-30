using Pia.Models;

namespace Pia.Services;

/// <summary>
/// The ONE place a tool's <see cref="ToolClass"/> is derived (04 D4). Both gates and the action-card builder
/// call it, so the card can no longer disagree with the gate about what class a tool is.
/// <para>
/// Before this existed there were two derivations: the gates asked <c>IPluginService.IsMcpTool</c> (route
/// based) while the card switched on the plugin NAME with <c>_ =&gt; Mcp</c>. The built-in plugin
/// <c>scheduled-research</c> was missing from that switch, so its cards were titled "External tool", offered
/// an "Always allow" button the gate then silently ignored, and parsed their key/value details as JSON.
/// </para>
/// </summary>
public static class ToolClassifier
{
    /// <summary>
    /// Classify a pending tool call. The ROUTE wins: <paramref name="isExternalRoute"/> (from
    /// <c>IPluginService.IsMcpTool</c>, re-derived at the gate and fail-closed to <c>true</c>) short-circuits
    /// to <see cref="ToolClass.External"/> so a server-defined tool can never talk its way into a built-in
    /// class by naming its plugin "files".
    /// <para>
    /// Otherwise the BUILT-IN plugin names map 1:1 (the full set from <c>BuiltInPluginDefaults</c>), and
    /// anything else is <see cref="ToolClass.External"/> — which is correct because a pending action can only
    /// come from a registered plugin, and every registered non-built-in plugin is an MCP server. Comparison
    /// is ordinal: the names are literals in <c>BuiltInPluginDefaults</c>, not user input.
    /// </para>
    /// </summary>
    public static ToolClass Classify(string? pluginName, bool isExternalRoute)
    {
        if (isExternalRoute)
            return ToolClass.External;

        return pluginName switch
        {
            "memory" => ToolClass.Memory,
            "todo" => ToolClass.Todo,
            "reminder" => ToolClass.Reminder,
            "files" => ToolClass.Files,
            "git" => ToolClass.Git,
            "scheduled-research" => ToolClass.Scheduling,
            "ingest" => ToolClass.Ingest,
            _ => ToolClass.External,
        };
    }
}
