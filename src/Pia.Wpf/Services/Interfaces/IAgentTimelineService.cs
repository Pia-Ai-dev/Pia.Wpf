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
    /// Append one row. NEVER throws: <c>Seq</c> is allocated synchronously (which is what makes the ordering
    /// correct) and the write is queued to a serial background writer. Emitting an audit event must never be
    /// able to fail a step, so callers invoke this with no <c>await</c> and no result to check.
    /// <paramref name="e"/>'s <c>Seq</c> is ignored — the service assigns it.
    /// <para>
    /// <b>Blocking, precisely.</b> The steady-state emit does no I/O at all. The FIRST emit of each run does:
    /// the SQLite implementation seeds the run's sequence with one indexed aggregate — and, on the very first
    /// call of the process, opens its connection — synchronously on the CALLER's thread, which for the
    /// interactive gate is the UI thread. That is a bounded cost per run, not a free one, and it is stated here
    /// because an earlier version of this comment claimed "NEVER blocks on I/O", which was false.
    /// </para>
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
/// (<c>AiClientService</c>'s <c>await toolHandler(toolCall, new ToolDispatchContext(round + 1))</c>) has no
/// decision and no ids in scope, and
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

    /// <summary>
    /// The tool name to persist for a call that DID NOT ROUTE. On that one arm the name is an arbitrary
    /// MODEL-authored string — providers surface the raw function name verbatim — so a malformed call whose
    /// name concatenates its arguments (<c>read_file{"path":"C:/…/Therapy notes.md"}</c>) would otherwise put a
    /// user path in the column whose contract is "never an argument, never a result, never a path", and would
    /// do it with no length bound. Anything outside a tool-identifier shape becomes a sentinel.
    /// <para>
    /// Applied ONLY to the unrouted arm, deliberately. A routed call's name comes from the pending action, i.e.
    /// from the plugin service's own route table, and rewriting a known-good MCP name to a sentinel because it
    /// carries a character this charset does not list would be a regression on the good path. Lives here so
    /// both gates share one definition rather than one each.
    /// </para>
    /// </summary>
    public static string SanitizeUnroutedToolName(string? toolName)
    {
        if (string.IsNullOrEmpty(toolName) || toolName.Length > 64) return "(unnamed)";

        return toolName.All(IsToolIdChar) ? toolName : "(unnamed)";
    }

    private static bool IsToolIdChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or ':' or '-';

    /// <summary>
    /// The provider's correlation token to persist for a gated call, for <c>ToolCallId</c>. Returns
    /// <c>null</c> — not a sentinel — for anything outside a correlation-id shape, because the column is
    /// NULLABLE and "no usable correlation id" is exactly what null means there. (Contrast
    /// <see cref="SanitizeUnroutedToolName"/>, whose column is NOT NULL, so it must answer with a string.)
    /// <para>
    /// Applied on EVERY arm, unlike the tool-name sanitizer. The tool NAME is model-authored on the unrouted
    /// arm alone — a routed name is a key from the plugin service's own route table, so rewriting it would be
    /// a regression on the good path. <c>CallId</c> has no such good path: it is copied out of provider JSON
    /// on every arm and nothing in this process validates it, so a provider (or a proxy in front of one) that
    /// echoes model text into it would otherwise put an UNBOUNDED string in a metadata-only column.
    /// </para>
    /// <para>
    /// The charset is the same tool-identifier shape the name check uses, which real provider ids fit:
    /// <c>call_abc123</c> (OpenAI), <c>toolu_01ABCdef</c> (Anthropic), <c>chatcmpl-tool-9f2</c>, a bare GUID.
    /// The 128-char bound is generous against every one of those and still far short of a payload.
    /// </para>
    /// <para>
    /// <b>What this does and does NOT guarantee.</b> It makes the value SHAPE- and LENGTH-bounded; it does not
    /// make readable text impossible. The permitted charset is base64url-minus-padding, so
    /// <c>Therapy_notes_2026.md</c> passes through unchanged. What the bound buys is that no path separator, no
    /// space, no quote and no brace survives, and nothing longer than 128 characters does — enough that a
    /// serialized argument, a JSON blob or a full path cannot land here, which is the property the audit
    /// table's "metadata only" contract needs. Claiming more than that would hand a future reviewer a
    /// guarantee this code does not make.
    /// </para>
    /// </summary>
    public static string? SanitizeCallId(string? callId)
    {
        if (string.IsNullOrWhiteSpace(callId) || callId.Length > 128) return null;

        return callId.All(IsToolIdChar) ? callId : null;
    }

    /// <summary>
    /// The LENGTH of a tool call's arguments, for <c>ArgsChars</c>. The serialized form exists only inside
    /// this method: it is measured and discarded, never stored and never logged. Shared by both gates so the
    /// number means the same thing on either surface. Returns null rather than throwing — a bookkeeping
    /// measurement must not be able to fail a tool call.
    /// <para>
    /// Measured where the HANDLER sees the arguments, i.e. inside
    /// <c>TokenizingAiClientService.WrapToolHandler</c>'s decoration, so this is the pre-tokenization length.
    /// </para>
    /// </summary>
    public static int? MeasureArgs(IDictionary<string, object?>? arguments)
    {
        if (arguments is null) return null;
        try { return System.Text.Json.JsonSerializer.Serialize(arguments).Length; }
        catch { return null; }
    }

    /// <summary>Whole milliseconds since a <c>Stopwatch.GetTimestamp()</c> reading, for <c>DurationMs</c>.</summary>
    public static long ElapsedMs(long startTimestamp) =>
        (long)System.Diagnostics.Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;

    /// <summary>
    /// Record one gated tool call. Fills the ids, the row id, the timestamp and
    /// <see cref="AgentTimelineEventKind.ToolCall"/>; the caller supplies the decision vocabulary. Never
    /// throws — the service's <c>Emit</c> is the failure boundary and this adds one of its own.
    /// <para>
    /// <b>Why the four correlation parameters are REQUIRED</b> (T2-14) while argsChars/resultChars/durationMs
    /// stayed optional: with a default, a gate arm that simply forgot <c>requestedAt:</c> would persist NULL
    /// silently and read back as "this arm asked no gate" — a false audit statement no test would notice.
    /// Required, the omission is a compile error at the exact call site that needs review. It also makes the
    /// unrouted arms' positional 7th argument (<c>argsChars</c>) a type error rather than a mis-bind.
    /// <c>StepOrdinal</c> is deliberately NOT here: like <c>Seq</c> it is service-assigned.
    /// </para>
    /// </summary>
    /// <param name="toolCallId">Raw <c>FunctionCallContent.CallId</c> — sanitized HERE, once, so no gate can
    /// forget to. Pass it through unmodified; <see cref="SanitizeCallId"/> is the boundary.</param>
    /// <param name="round">The 1-based provider tool-loop round, from the handler's dispatch context.</param>
    /// <param name="requestedAt">When the authorization question was posed; null if none was.</param>
    /// <param name="decidedAt">When it was answered; null while genuinely still pending (the unattended park).</param>
    public void Emit(
        ToolGateSurface surface,
        string toolName,
        ToolClass toolClass,
        Guid? pluginId,
        ToolGateDecision decision,
        AgentTimelineOutcome outcome,
        string? toolCallId,
        int? round,
        DateTime? requestedAt,
        DateTime? decidedAt,
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
                CreatedAt: DateTime.UtcNow,
                ToolCallId: SanitizeCallId(toolCallId),
                Round: round,
                StepOrdinal: null, // assigned by the service, like Seq
                RequestedAt: requestedAt,
                DecidedAt: decidedAt));
        }
        catch
        {
            // Bookkeeping must never fail a step. The service already logs; a fake that throws must not
            // escape here either (that is exactly what the failure-isolation test drives).
        }
    }
}
