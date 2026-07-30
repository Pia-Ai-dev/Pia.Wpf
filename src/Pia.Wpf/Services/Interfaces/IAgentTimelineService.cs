using Pia.Models;

namespace Pia.Services.Interfaces;

/// <summary>
/// The per-run audit timeline: an append-only, metadata-only record of every GATED tool call a run made, the
/// approval decision it got, and what happened next (Batch 03).
/// <para>
/// <b>Scope.</b> The two RUN gates only — <c>ChatSession.HandleToolCall</c> (interactive step turns) and
/// <c>BackgroundAssistantTurnRunner.HandleToolCallAsync</c> (unattended). Reads emit nothing (they carry no
/// decision, are unbounded in count, and the only interesting thing about a read is its target, which this
/// table must not store). Voice mode emits nothing because a voice turn has no run to attach a row to. So the
/// trace is a record of <i>gated</i> calls, not of every effect in the app.
/// </para>
/// <para>
/// <b>Device-local.</b> There is no <c>SyncAgentRun</c> DTO and runs never cross the sync wire, so this table
/// does not replicate: a second device sees the synced transcript and no timeline.
/// </para>
/// </summary>
public interface IAgentTimelineService
{
    /// <summary>
    /// Append one row. NEVER throws and NEVER blocks on I/O: <c>Seq</c> is allocated synchronously (which is
    /// what makes the ordering correct) and the write is queued to a serial background writer. Emitting an
    /// audit event must never be able to fail a step, so callers invoke this with no <c>await</c> and no
    /// result to check. <paramref name="e"/>'s <c>Seq</c> is ignored — the service assigns it.
    /// </summary>
    void Emit(AgentTimelineEvent e);

    /// <summary>
    /// The run's rows in <c>(RunId, Seq)</c> order. Read on demand by the render surface — this service
    /// raises no change event, deliberately: ~500 rows per run through <c>RunChanged</c> would turn every
    /// consumer into a re-projection storm.
    /// </summary>
    Task<IReadOnlyList<AgentTimelineEvent>> GetForRunAsync(Guid runId, CancellationToken ct = default);

    /// <summary>
    /// Delete rows older than <paramref name="cutoff"/> by the ROW's own <c>CreatedAt</c> — never the run's
    /// <c>CompletedAt</c>, which a crash-swept or cancelled run leaves NULL forever, making its rows
    /// immortal. Returns the number of rows deleted. Never throws.
    /// </summary>
    Task<int> PruneOlderThanAsync(DateTime cutoff, CancellationToken ct = default);
}

/// <summary>
/// The per-step sink handed down to a tool gate: an immutable
/// <c>(service, runId, stepId)</c> triple whose only job is to let the gate emit a row without knowing
/// anything about the store.
/// <para>
/// This is how the run and step ids reach a gate, and the alternatives were rejected for concrete reasons.
/// <c>TaskAmbient.TaskContext.TaskId</c> is the CHAT id for interactive turns and the RUN id for agent steps —
/// one field, two meanings, no discriminator, and never a step id. The single tool-dispatch line
/// (<c>AiClientService</c>'s <c>await toolHandler(toolCall)</c>) has no decision and no ids in scope, and
/// emitting there would file the planner's and verifier's <c>emit_plan</c>/<c>emit_verdict</c> capture
/// closures as tool calls. A mutable "current step" slot on the orchestrator would be cross-thread mutable
/// state guarding an audit trail — the interactive gate runs on the UI thread, the orchestrator loop does not.
/// </para>
/// <para>
/// A <c>null</c> scope means <b>emit nothing</b>, which is what every non-run turn passes: the ordinary
/// interactive chat turn, the SingleTurn background path, and every test that does not opt in.
/// </para>
/// </summary>
public sealed class AgentTimelineScope
{
    private readonly IAgentTimelineService _service;

    public AgentTimelineScope(IAgentTimelineService service, Guid runId, Guid? stepId)
    {
        _service = service;
        RunId = runId;
        StepId = stepId;
    }

    public Guid RunId { get; }

    /// <summary>The step this turn belongs to, or null for a run-level turn (the planner-degrade fallback).</summary>
    public Guid? StepId { get; }

    /// <summary>Same scope, different step — used where one executor walks several steps.</summary>
    public AgentTimelineScope ForStep(Guid? stepId) => new(_service, RunId, stepId);

    /// <summary>
    /// Record one gated tool call. Fills the ids, the row id, the timestamp and
    /// <see cref="AgentTimelineEventKind.ToolCall"/>; the caller supplies the decision vocabulary. Never
    /// throws — the service's <c>Emit</c> is the failure boundary and this adds one of its own.
    /// </summary>
    public void Emit(
        ToolGateSurface surface,
        string toolName,
        ToolClass toolClass,
        Guid? pluginId,
        ToolGateDecision decision,
        AgentTimelineOutcome outcome,
        int? argsChars = null,
        int? resultChars = null,
        long? durationMs = null)
    {
        try
        {
            _service.Emit(new AgentTimelineEvent(
                Id: Guid.NewGuid(),
                RunId: RunId,
                StepId: StepId,
                Seq: 0, // assigned by the service
                Kind: AgentTimelineEventKind.ToolCall,
                Surface: surface,
                Decision: decision,
                Outcome: outcome,
                ToolName: toolName,
                ToolClass: toolClass,
                PluginId: pluginId,
                ArgsChars: argsChars,
                ResultChars: resultChars,
                DurationMs: durationMs,
                CreatedAt: DateTime.UtcNow));
        }
        catch
        {
            // Bookkeeping must never fail a step. The service already logs; a fake that throws must not
            // escape here either (that is exactly what the failure-isolation test drives).
        }
    }
}
