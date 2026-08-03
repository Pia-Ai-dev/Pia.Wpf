using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>
/// The per-step collector the <c>emit_step_result</c> interception writes into. One store is created per
/// step turn by the executor that offered the tool, and its lifetime is exactly that step's exchange — the
/// same in-process, non-persisted shape <c>RunSteeringStore</c> and <c>ExecutingRunStore</c> have.
/// <para>
/// ARMED IFF OFFERED. Both interception seams (<c>ChatSession.HandleToolCall</c> and
/// <c>BackgroundAssistantTurnRunner.HandleToolCallAsync</c>) fire only when a store is present, so an
/// ordinary chat turn — which is never offered the tool — still routes a hallucinated
/// <c>emit_step_result</c> the normal way and dead-ends at "Unknown tool.". Callers derive the store from
/// the resolved tool list (<see cref="AgentStepTools.OffersStepResultTool"/>) rather than from a separate
/// flag, so the two can never drift apart.
/// </para>
/// </summary>
public sealed class StepOutcomeStore
{
    /// <summary>Caps mirroring the shape <c>AgentRunService.NormalizeStepText</c> uses for step text: flatten,
    /// trim, then keep the HEAD. The summary reaches a replan prompt and the run's persisted failure reason;
    /// the artifact reference is a path-like token and needs far less room.</summary>
    private const int MaxSummaryChars = 600;
    private const int MaxArtifactChars = 300;

    /// <summary>
    /// The step's declared outcome, or null when it never declared one (so the caller falls back to the old
    /// non-empty-text heuristic and records the step as UNCONFIRMED).
    /// <para>
    /// LAST call wins, deliberately. A step that declares success, then hits a problem in a later tool round
    /// and corrects itself, must be able to say so — the opposite rule would freeze the first, stalest
    /// verdict. The tool description tells the model to call it once, at the end.
    /// </para>
    /// </summary>
    public StepOutcomeClaim? Claim { get; private set; }

    /// <summary>How many well-formed declarations arrived. Greater than 1 means the model corrected itself;
    /// 0 with an offered tool is the unconfirmed fallback. Id-safe: a count, never the content.</summary>
    public int AcceptedCalls { get; private set; }

    /// <summary>
    /// Records one <c>emit_step_result</c> call and returns the string handed back to the model as the tool
    /// result. A call whose <c>succeeded</c> argument is missing or unparseable records NOTHING and asks for
    /// a retry: a provider's argument-encoding quirk must never be read as "the step failed".
    /// </summary>
    public string Record(IDictionary<string, object?>? arguments)
    {
        var succeeded = TryReadBool(arguments, "succeeded");
        if (succeeded is null)
            return "emit_step_result needs a boolean 'succeeded' argument. Call it again with succeeded set.";

        Claim = new StepOutcomeClaim(
            succeeded.Value,
            Clamp(ReadString(arguments, "summary"), MaxSummaryChars) ?? string.Empty,
            Clamp(ReadString(arguments, "artifact_ref"), MaxArtifactChars));
        AcceptedCalls++;
        return succeeded.Value
            ? "Recorded: this step is DONE."
            : "Recorded: this step FAILED. Do not claim otherwise in your reply.";
    }

    /// <summary>
    /// Reads a boolean argument through every encoding a provider realistically sends: a real
    /// <see cref="bool"/>, a JSON <c>true</c>/<c>false</c>, a JSON string, or a bare string. Returns null —
    /// meaning NO claim — for anything else, including a missing key.
    /// </summary>
    internal static bool? TryReadBool(IDictionary<string, object?>? arguments, string name)
    {
        if (arguments is null || !arguments.TryGetValue(name, out var value) || value is null)
            return null;

        return value switch
        {
            bool b => b,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            JsonElement { ValueKind: JsonValueKind.String } je => ParseText(je.GetString()),
            string s => ParseText(s),
            _ => null,
        };

        static bool? ParseText(string? text) => bool.TryParse(text?.Trim(), out var parsed) ? parsed : null;
    }

    /// <summary>Reads a string argument, tolerating a raw string and a <see cref="JsonElement"/> — the same
    /// two shapes <c>ChatSession.ExtractStringArg</c> already handles for <c>suggest_agent_mode</c>.</summary>
    internal static string? ReadString(IDictionary<string, object?>? arguments, string name)
    {
        if (arguments is null || !arguments.TryGetValue(name, out var value) || value is null)
            return null;

        return value switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.Null } => null,
            JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
            JsonElement je => je.ToString(),
            _ => value.ToString(),
        };
    }

    /// <summary>Flatten CR/LF/TAB to space, trim, cap keeping the head. Blank yields null, never an
    /// empty-but-present string (which would render an empty "produced:" line downstream).</summary>
    private static string? Clamp(string? text, int cap)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var flat = text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
        return flat.Length <= cap ? flat : flat[..cap] + "…";
    }
}

/// <summary>
/// The tools an AGENT STEP is offered that an ordinary chat turn is not (hermes #9).
/// <para>
/// SCOPING, and why it is not done in <c>AssistantPromptComposer.PrepareTurn</c>: that one method builds the
/// tool list for EVERY turn shape in the app — chat, voice, MCP, @-command, background single-turn — and the
/// only narrowing axes it has (plugin enabled, persona <c>ToolScope</c>, @-command domain) are all
/// turn-shape blind. Widening it there would leak <c>emit_step_result</c> into turns that have no step to
/// report on. Instead each executor appends the tool to the tool list of a STEP turn only, at the single
/// choke point where the step's persona has already been resolved — a step carrying an
/// <c>AssignedPersonaId</c> runs on a different <c>AssistantTurnSetup</c>, and augmenting the run default
/// alone would silently drop the tool for exactly those steps.
/// </para>
/// <para>
/// Like <c>suggest_agent_mode</c> this tool has no plugin, no GUID and no <c>_toolNameRoutes</c> entry, so it
/// is intercepted PRE-ROUTE in both handlers. <c>RouteToolCallAsync</c> never sees it and no
/// <c>ToolGateDecision.UnknownTool</c> timeline row is emitted for it.
/// </para>
/// </summary>
public static class AgentStepTools
{
    /// <summary>Pinned by both interception seams and by the schema below — one spelling, one constant.</summary>
    public const string EmitStepResultToolName = "emit_step_result";

    /// <summary>
    /// The failure reason a step gets when it declared <c>succeeded=false</c> but left the summary blank.
    /// <para>
    /// Deliberately NOT a localized resource, and shared by both executors: this string is not shown to the
    /// user — it becomes <c>StepTurnResult.Error</c>, which the orchestrator hands to <c>ReplanAsync</c> and
    /// persists into the run's <c>ExtraJson</c>. A model prompt is not a UI surface, and the headless
    /// executor has no <c>ILocalizationService</c> at all, so localizing it would have made the two paths
    /// report differently for the same event.
    /// </para>
    /// </summary>
    public const string UndetailedFailure = "The step reported that it did not succeed.";

    /// <summary>
    /// The step-outcome declaration tool. Built per step rather than cached in a static: an
    /// <see cref="AITool"/> is handed to the provider transport, and one shared instance across concurrently
    /// executing runs is a needless aliasing question for an object this cheap.
    /// </summary>
    internal static AITool BuildEmitStepResultTool() =>
        AIFunctionFactory.Create(
            (
                [Description("True only if this step actually achieved what it was asked to do. False if it could not be completed, was blocked, or produced the wrong thing — including when you can explain the failure clearly.")]
                bool succeeded,
                [Description("One or two sentences: what you produced, or what blocked you.")]
                string summary,
                [Description("Optional. The concrete artifact this step produced — a file path, or a short identifier. Omit when the step produced no artifact.")]
                string? artifact_ref = null) => "ok",
            EmitStepResultToolName,
            "Declare the outcome of the step you were just asked to execute. Call this exactly once, as the LAST thing you do in the step, after any other tool calls. Explaining a failure in prose is NOT a failure report — a step whose outcome you do not declare here is recorded as unconfirmed, and a step you declare succeeded=false is recorded as failed no matter what else you wrote.");

    /// <summary>Whether a resolved tool list carries the declaration tool — i.e. whether THIS turn may
    /// produce a claim. The executors derive the sink from this so "armed" and "offered" cannot drift.</summary>
    public static bool OffersStepResultTool(IEnumerable<AITool>? tools) =>
        tools is not null && tools.Any(t => string.Equals(t.Name, EmitStepResultToolName, StringComparison.Ordinal));

    /// <summary>
    /// Returns <paramref name="setup"/> with the declaration tool appended, on a COPY of the tool list.
    /// <para>
    /// The copy is the point: an executor's <c>AssistantTurnSetup</c> is resolved once and cached for the
    /// whole run (and on the live path it is the very same instance the session's ordinary chat turns use),
    /// so mutating <c>setup.Tools</c> in place would leak a step tool into every later turn.
    /// </para>
    /// <para>
    /// A setup with <c>SupportsTools=false</c> is returned untouched: the exchange engines pass neither tools
    /// nor a tool handler in that case, so there would be nothing to offer it to and nothing to intercept —
    /// such a step lands on the unconfirmed fallback, which is exactly right.
    /// </para>
    /// </summary>
    public static AssistantTurnSetup WithStepResultTool(AssistantTurnSetup setup)
    {
        if (!setup.SupportsTools)
            return setup;

        var tools = new List<AITool>(setup.Tools ?? []) { BuildEmitStepResultTool() };
        return setup with { Tools = tools };
    }
}
