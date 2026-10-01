namespace Pia.Models;

/// <summary>
/// A run's autonomy policy: the tool CLASSES this run may auto-approve without a card (interactive) or
/// without a named grant (unattended). Purely ADDITIVE — a persisted document can only ever widen, never
/// narrow, so a hostile or stale one cannot lift a rule that lives in <c>ToolAutonomy.Resolve</c>.
/// </summary>
/// <remarks>A null policy, an empty class set, and a policy naming only classes this build does not know are
/// all the same thing: no class is auto-approved.</remarks>
public sealed record RunAutonomyPolicy(IReadOnlyCollection<ToolClass> AutoApproveClasses)
{
    /// <summary>
    /// The settings preset. Git, External, Screen and KnowledgeBase are left out because a class-wide grant must
    /// never cover them; Ingest is left out because it never asks.
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
    /// envelope stays byte-identical to a document written before policies existed.
    /// </summary>
    public static RunAutonomyPolicy? FromSettings(AppSettings settings) =>
        settings.AgentRunAutoApproveBuiltInWrites ? new RunAutonomyPolicy(PresetClasses) : null;
}
