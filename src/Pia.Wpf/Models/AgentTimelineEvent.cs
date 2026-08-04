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
/// <param name="ToolCallId">The provider's own correlation token for this call
/// (<c>FunctionCallContent.CallId</c>), so a row lines up with the provider round-trip in a log. Metadata
/// only BECAUSE it is shape- and length-bounded by <c>AgentTimelineScope.SanitizeCallId</c> — the raw string
/// is provider-JSON-authored on EVERY arm (unlike the tool NAME, which is model-authored on the unrouted arm
/// alone), so it is sanitized everywhere. Null when the provider gave none, or when the arm asked no gate.</param>
/// <param name="Round">The provider tool-loop round this call was dispatched in, 1-BASED to match every log
/// line in <c>AiClientService.GetChatCompletionWithToolsAsync</c>'s loop. Null on the synthetic truncation
/// marker, which belongs to no round, and on any surface that does not carry a dispatch context.</param>
/// <param name="StepOrdinal">Monotonic per STEP — <see cref="Seq"/>'s per-run sibling. Service-assigned in
/// the same critical section as <see cref="Seq"/>, so a caller-built row carries null and the service fills
/// it. Stays null when <see cref="StepId"/> is null (the planner-degrade run-level turn, the truncation
/// marker): a shared null-bucket counter would invent a step that does not exist, and <see cref="Seq"/>
/// already orders those rows.</param>
/// <param name="RequestedAt">When the authorization question was POSED — the instant before the policy
/// resolver was consulted, or the instant an action card became visible to a human. Null on the unrouted arm,
/// which consulted no gate.</param>
/// <param name="DecidedAt">When that question was ANSWERED. Null while a decision is genuinely still pending:
/// the unattended park writes its row immediately (so the run's audit trail records that it stopped to ask)
/// and the human's answer arrives later as a resume that writes a FRESH row. It is never back-filled —
/// back-filling would break the write-once model this record's remarks describe.</param>
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
    DateTime CreatedAt,
    string? ToolCallId,
    int? Round,
    long? StepOrdinal,
    DateTime? RequestedAt,
    DateTime? DecidedAt)
{
    /// <summary>
    /// Row shape version, so a future shape change is detectable rather than misread. <b>2</b> since T2-14
    /// widened the row with the five correlation columns; the DDL still says <c>DEFAULT 1</c> deliberately,
    /// because a row an older build wrote genuinely IS a v1 row. That is what makes a NULL
    /// <see cref="ToolCallId"/> readable: on a v1 row it means "never recorded", on a v2 row "the provider
    /// gave none". Nothing branches on it — it exists so the distinction is recoverable, not so code forks.
    /// </summary>
    public int SchemaVersion { get; init; } = 2;
}
