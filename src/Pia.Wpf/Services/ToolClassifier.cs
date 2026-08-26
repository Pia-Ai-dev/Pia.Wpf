using Pia.Models;

namespace Pia.Services;

/// <summary>The ONE place a tool's <see cref="ToolClass"/> is derived, so a card and a gate cannot disagree
/// about what class a tool is.</summary>
public static class ToolClassifier
{
    /// <summary>
    /// Classify a pending tool call, ROUTE first: <paramref name="isExternalRoute"/> short-circuits to
    /// <see cref="ToolClass.External"/>, and an unrecognised NAME is <see cref="ToolClass.Unknown"/> rather
    /// than External — class decides what the autonomy preset covers and what the unattended park will ask
    /// about, so a name-only guess at a gate would change both for a built-in the server renamed.
    /// </summary>
    public static ToolClass Classify(string? pluginName, bool isExternalRoute)
    {
        if (isExternalRoute)
            return ToolClass.External;

        return MapBuiltInName(pluginName);
    }

    /// <summary>
    /// Name-only classification with an unrecognised name PRESUMED <see cref="ToolClass.External"/>, for the
    /// action-card builder, which has no route to consult; it picks the card's category, title and warning,
    /// not what may be granted. <b>Never call this from a gate</b> — see <see cref="Classify"/>. In production
    /// the gate hands the builder the authoritative class, so this guess is reached only when nobody did.
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
        "assignments" => ToolClass.Assignment,
        _ => ToolClass.Unknown,
    };
}
