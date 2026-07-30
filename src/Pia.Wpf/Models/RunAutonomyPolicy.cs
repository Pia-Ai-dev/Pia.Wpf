namespace Pia.Models;

/// <summary>
/// A run's autonomy policy: the tool CLASSES this run may auto-approve without a card (interactive) or
/// without a named grant (unattended). Purely ADDITIVE — there is deliberately no "never" list, because a
/// floor a document can express is a floor a document can shrink; the floor lives in
/// <c>ToolAutonomy.Resolve</c> and is evaluated before any policy branch (04 D2/D5).
/// <para>
/// A null policy, an empty class set, and a policy naming only classes this build does not know are all
/// exactly TODAY'S behaviour: no class is auto-approved.
/// </para>
/// </summary>
public sealed record RunAutonomyPolicy(IReadOnlyCollection<ToolClass> AutoApproveClasses)
{
    /// <summary>
    /// The classes the settings preset grants (04 D9). <c>Git</c> is excluded because
    /// <c>git_switch</c>/<c>git_restore</c>/<c>git_stash</c> shed uncommitted work and are NOT delete-like by
    /// name, so neither the floor nor the never-covers-a-delete rule would stop them. <c>External</c> is
    /// excluded because a class grant would make an MCP server's NEXT tool auto-approved retroactively.
    /// <c>Ingest</c> is excluded because it is never gated (it returns no pending action).
    /// </summary>
    public static readonly IReadOnlyList<ToolClass> PresetClasses =
    [
        ToolClass.Memory,
        ToolClass.Todo,
        ToolClass.Reminder,
        ToolClass.Scheduling,
        ToolClass.Files,
    ];

    /// <summary>
    /// Does this policy cover <paramref name="toolClass"/>? <see cref="ToolClass.Unknown"/> is hardcoded
    /// false: an unrecognised class NAME in a persisted document must never become authority.
    /// </summary>
    public bool Covers(ToolClass toolClass)
        => toolClass != ToolClass.Unknown && AutoApproveClasses.Contains(toolClass);

    /// <summary>
    /// The policy a launch resolves from user settings. Null when the setting is off — so the persisted
    /// envelope stays byte-identical to a pre-Batch-04 document (04 D9/D12).
    /// </summary>
    public static RunAutonomyPolicy? FromSettings(AppSettings settings) =>
        settings.AgentRunAutoApproveBuiltInWrites ? new RunAutonomyPolicy(PresetClasses) : null;
}
