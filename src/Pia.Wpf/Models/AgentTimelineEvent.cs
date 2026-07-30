namespace Pia.Models;

/// <summary>
/// What a timeline row IS. PERSISTED (<c>AgentTimelineEvents.Kind</c>) → <b>APPEND-ONLY</b>: never renumber,
/// never reuse an ordinal. An ordinal this build does not know reads back as <see cref="Unknown"/> and renders
/// as "unknown"; it never throws.
/// </summary>
public enum AgentTimelineEventKind
{
    /// <summary>Never written by this build; the render value for an ordinal an older/newer DB carries.</summary>
    Unknown = 0,

    /// <summary>One gated tool call: its decision, its outcome, and the step it belonged to.</summary>
    ToolCall = 1,

    /// <summary>The per-run cap was reached; later events for this run were dropped (03 D6).</summary>
    TraceTruncated = 2,
}

/// <summary>
/// What happened AFTER the decision. PERSISTED (<c>AgentTimelineEvents.Outcome</c>) → <b>APPEND-ONLY</b>.
/// </summary>
public enum AgentTimelineOutcome
{
    /// <summary>Never written by this build for a tool call; also the value the synthetic cap row carries.</summary>
    Unknown = 0,

    /// <summary>Authorized and <c>Execute()</c> returned.</summary>
    Ok = 1,

    /// <summary>Authorized and <c>Execute()</c> threw. The exception TYPE is logged, never stored.</summary>
    Error = 2,

    /// <summary>Not authorized — nothing ran. The <c>Decision</c> says why.</summary>
    NotExecuted = 3,
}

/// <summary>
/// One row of a run's audit timeline: a single GATED tool call, its approval decision, its outcome, and the
/// step it belonged to. <b>METADATA ONLY</b> — see <c>03-audit-timeline.impl.md</c> §3. A "reference" here is
/// an id, a count/duration, or the tool NAME (already logged at Information by both gates). Never an
/// argument, never a result, never a path, never a goal or a step title, and never a HASH of any of those:
/// the argument space of a file tool is low-entropy and enumerable, so a hash would be a brute-forceable
/// confirmation oracle rather than an anonymization.
/// <para>
/// Ordered by <c>(RunId, Seq)</c>. <see cref="Seq"/> is allocated in memory by
/// <c>IAgentTimelineService.Emit</c> — never derived from a timestamp, because <c>DateTime.UtcNow</c>'s ~1 ms
/// resolution on Windows is coarser than one tool call. A row built by a caller carries <c>Seq = 0</c>; the
/// service assigns the real value.
/// </para>
/// <para>
/// Written ONCE, after the outcome is known, so <see cref="Decision"/>, <see cref="Outcome"/> and
/// <see cref="DurationMs"/> land together. The accepted cost: a tool call in flight when the process dies
/// leaves no row at all. That is the better failure — the run dies with it (the startup crash sweep settles
/// it <c>Cancelled</c>), and a half-written row claiming "approved" for a call whose effect is unknown would
/// be worse than a missing one.
/// </para>
/// </summary>
/// <param name="ArgsChars">Length of the tool arguments as the HANDLER saw them, i.e. BEFORE
/// <c>TokenizingAiClientService.WrapToolHandler</c> rewrites anything. Do not reconcile it against a log line
/// captured on the other side of that wrapper.</param>
/// <param name="ResultChars">Length of the tool result as the handler returned it — again pre-tokenization.
/// Null when the tool did not run, or returned a non-string.</param>
public sealed record AgentTimelineEvent(
    Guid Id,
    Guid RunId,
    Guid? StepId,
    long Seq,
    AgentTimelineEventKind Kind,
    ToolGateSurface Surface,
    ToolGateDecision Decision,
    AgentTimelineOutcome Outcome,
    string ToolName,
    ToolClass ToolClass,
    Guid? PluginId,
    int? ArgsChars,
    int? ResultChars,
    long? DurationMs,
    DateTime CreatedAt)
{
    /// <summary>Row shape version, so a future shape change is detectable rather than misread.</summary>
    public int SchemaVersion { get; init; } = 1;
}
