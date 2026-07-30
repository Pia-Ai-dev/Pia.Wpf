namespace Pia.Models;

/// <summary>
/// The family a tool belongs to, for autonomy-policy purposes (Batch 04 D4/D15). PERSISTED — the
/// <c>AgentRuns.PolicyJson</c> envelope stores class member NAMES, and Batch 03's timeline stores the ordinal
/// — so this enum is APPEND-ONLY: never renumber, never reuse an ordinal, never rename a member. An ordinal
/// or a name this build does not know reads back as <see cref="Unknown"/>, which
/// <c>RunAutonomyPolicy.Covers</c> hardcodes to false.
/// </summary>
public enum ToolClass
{
    /// <summary>Not classified. Never grantable as a class (<c>Covers(Unknown)</c> is hardcoded false).</summary>
    Unknown = 0,
    Memory = 1,
    Todo = 2,
    Reminder = 3,
    Files = 4,
    Git = 5,

    /// <summary>The built-in scheduled-job tools (plugin <c>scheduled-research</c>).</summary>
    Scheduling = 6,

    /// <summary>An external, server-defined MCP tool. Derived from the ROUTE first, then from an
    /// unrecognised plugin name (which, for a pending action, can only be a non-built-in plugin).</summary>
    External = 7,

    /// <summary>
    /// The built-in ingest tool (plugin <c>ingest</c>). It runs INLINE and returns no pending action today,
    /// so it never reaches a gate or a card; the class exists so that if it ever does, it is not silently
    /// treated as an external/MCP tool the way <c>scheduled-research</c> was (04 §0.6).
    /// </summary>
    Ingest = 8,
}

/// <summary>Which gate asked. PERSISTED by Batch 03 → APPEND-ONLY.</summary>
public enum ToolGateSurface
{
    Unknown = 0,

    /// <summary>A live chat session: an unauthorized write shows an action card the user answers.</summary>
    Interactive = 1,

    /// <summary>A headless/scheduled run: nobody is watching, so an unauthorized write is refused.</summary>
    Unattended = 2,

    /// <summary>Voice mode: a user IS present but there is no card surface, so a write is refused with a remedy.</summary>
    Voice = 3,
}

/// <summary>Why a tool ran or did not. PERSISTED by Batch 03 → APPEND-ONLY. See 04's D15 table.</summary>
public enum ToolGateDecision
{
    /// <summary>Never written by this build; the render value for an ordinal an older/newer DB carries.</summary>
    Unknown = 0,
    AutoApprovedStandingGrant = 1,
    AutoApprovedPolicy = 2,
    GrantedByName = 3,
    ApprovedOnce = 4,
    ApprovedAlways = 5,
    DeclinedByUser = 6,

    /// <summary>The card was cancelled (new chat / retry / scope dispose) — NOT a user denial.</summary>
    CardCancelled = 7,
    DeniedNotGranted = 8,
    DeniedDestructiveFloor = 9,
    UnknownTool = 10,

    /// <summary>
    /// The curated additive allowlist authorized the call. Voice-mode only: the interactive surface requires
    /// a standing grant as well, and the unattended surface has no allowlist at all (04 §0.3).
    /// </summary>
    AutoApprovedAllowlist = 11,
}

/// <summary>
/// What the caller must DO. Control flow only — NOT persisted and NOT append-only-constrained; adding or
/// reordering members here is safe. Kept separate from <see cref="ToolGateDecision"/> so the persisted audit
/// vocabulary is not hostage to a control-flow refactor.
/// </summary>
public enum ToolGateOutcome
{
    /// <summary>Execute now, with no user interaction. The card (if any) is rendered pre-resolved.</summary>
    AutoRun,

    /// <summary>Ask the user. Only reachable on <see cref="ToolGateSurface.Interactive"/>.</summary>
    Prompt,

    /// <summary>Do not execute; tell the model why.</summary>
    Refuse,
}
