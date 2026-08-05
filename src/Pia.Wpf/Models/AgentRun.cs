namespace Pia.Models;

/// <summary>
/// The durable spine of an agent execution. A run is scoped to a goal and spans a slice of a
/// chat's transcript (delimited by stable message Ids). One chat hosts 0..N runs.
/// See docs/superpowers/specs/2026-07-18-agent-system-phase1-plan.md §2.1.
/// </summary>
public sealed class AgentRun
{
    public Guid Id { get; set; }

    public int SchemaVersion { get; set; } = 1;

    /// <summary>FK → AssistantChats(Id). The chat this run lives in (1 chat : 0..N runs; not unique).</summary>
    public Guid ChatId { get; set; }

    public RunShape RunShape { get; set; }

    public AgentRunState State { get; set; }

    public AgentRunTrigger TriggerKind { get; set; }

    /// <summary>e.g. <c>ScheduledJob.Id</c> when <see cref="AgentRunTrigger.Schedule"/>.</summary>
    public Guid? TriggerRef { get; set; }

    /// <summary>Reserved for Phase 3 sub-agents; null in Phase 1.</summary>
    public Guid? ParentRunId { get; set; }

    /// <summary>Only the owner fires/advances (mirrors <c>ScheduledJob.OwnerDeviceId</c>).</summary>
    public Guid? OwnerDeviceId { get; set; }

    /// <summary>SENSITIVE (user content) — log only via <c>SensitiveDebug</c>.</summary>
    public string? Goal { get; set; }

    /// <summary>Run's transcript slice, by STABLE message Id (never a positional ordinal — §16 R3).</summary>
    public Guid? FirstMessageId { get; set; }

    public Guid? LastMessageId { get; set; }

    /// <summary>
    /// Opaque launch-grant envelope written once at create (the launcher owns its schema; the run
    /// service never parses it). METADATA — may name granted capabilities, so log presence only.
    /// Also the seam for the Phase 2 per-run autonomy policy.
    /// </summary>
    public string? PolicyJson { get; set; }

    /// <summary>
    /// Tokens/wall-clock, per step + total:
    /// <c>{ inputTokens, outputTokens, wallClockMs, activeMs, segmentStartedAt?, perStep:[...] }</c>.
    /// <c>wallClockMs</c> is accumulated ACTIVE time (parked gaps excluded); <c>activeMs</c> +
    /// <c>segmentStartedAt</c> are its internal accumulator/open-segment marker. A row written before
    /// 2026-07-30 also carries a withdrawn per-run money field; readers ignore unknown members.
    /// </summary>
    public string? LedgerJson { get; set; }

    /// <summary>
    /// 18 D2 / owner Q3: the answers the user has given to this run's clarification questions, as a JSON array
    /// of strings, oldest-first. Accumulates across parks (18 D4 puts no cap on asking, so a second park must
    /// not lose the first answer). Null until the run has parked and been answered once.
    /// <para>
    /// SENSITIVE (user content) — log the COUNT, never the text; the text goes out only via
    /// <c>SensitiveDebug</c>. <c>Pia.Services.RunClarifications</c> owns the shape; the run service reads and
    /// writes it as an opaque string, the same discipline <see cref="PolicyJson"/> has.
    /// </para>
    /// <para>
    /// Its own column rather than a member of the pause envelope in <see cref="ExtraJson"/>, and that is FORCED:
    /// both resume claims <c>SET ExtraJson=NULL</c>, so an answer kept there would be destroyed by the very
    /// resume that carries it. <see cref="Goal"/> is deliberately left untouched — the panel shows the user what
    /// they typed.
    /// </para>
    /// </summary>
    public string? ClarificationsJson { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public string? ExtraJson { get; set; }

    /// <summary>The ordered plan (live model view). Empty for <see cref="RunShape.SingleTurn"/>.</summary>
    public IReadOnlyList<AgentStep> Plan { get; set; } = [];
}
