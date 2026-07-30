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
    /// anything else is <see cref="ToolClass.Unknown"/> — deliberately NOT <see cref="ToolClass.External"/>.
    /// A NAME must never make a tool external at a gate: today the only source of externality there is
    /// <c>IsMcpTool</c>, and a plugin whose name this build does not recognise (a built-in renamed through
    /// <c>ApplyServerMetadata</c>, say) would otherwise become grantable-as-external by name. Comparison is
    /// ordinal: the names are literals in <c>BuiltInPluginDefaults</c>, not user input.
    /// </para>
    /// </summary>
    public static ToolClass Classify(string? pluginName, bool isExternalRoute)
    {
        if (isExternalRoute)
            return ToolClass.External;

        return MapBuiltInName(pluginName);
    }

    /// <summary>
    /// Name-only classification with an unrecognised name PRESUMED <see cref="ToolClass.External"/>.
    /// <para>
    /// <b>Never call this from a gate.</b> It exists for the action-card builder, which has no plugin route to
    /// consult and has always presumed "not one of the built-in names ⇒ an external tool" — that presumption
    /// is what puts the "Always allow" offer on a genuine MCP tool's card, and dropping it would remove a
    /// capability users have today. A GATE must not guess: a built-in renamed through
    /// <c>ApplyServerMetadata</c> would then become grantable-as-external by name, so a gate calls
    /// <see cref="Classify"/> with the route it already derived. In production the gate hands the builder the
    /// authoritative class, so this guess is only reached when nobody supplied one.
    /// </para>
    /// </summary>
    public static ToolClass ClassifyPresumedExternal(string? pluginName)
    {
        var mapped = MapBuiltInName(pluginName);
        return mapped is ToolClass.Unknown ? ToolClass.External : mapped;
    }

    /// <summary>The built-in plugin names, 1:1. An unrecognised name is <see cref="ToolClass.Unknown"/>.</summary>
    private static ToolClass MapBuiltInName(string? pluginName) => pluginName switch
    {
        "memory" => ToolClass.Memory,
        "todo" => ToolClass.Todo,
        "reminder" => ToolClass.Reminder,
        "files" => ToolClass.Files,
        "git" => ToolClass.Git,
        "scheduled-research" => ToolClass.Scheduling,
        "ingest" => ToolClass.Ingest,
        _ => ToolClass.Unknown,
    };
}
