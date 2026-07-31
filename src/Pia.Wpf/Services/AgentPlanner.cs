using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Logging;
using Pia.Models;
using Pia.Services.Interfaces;
using Pia.Services.Providers;

namespace Pia.Services;

/// <summary>
/// Decomposes a goal into an ordered plan via <c>emit_plan</c> (§13.3). Uses
/// <see cref="IAiClientService.GetChatCompletionWithToolsAsync"/> with an inline capture handler
/// that drains the whole stream — because that loop has no handler-driven early exit, a plan turn
/// costs ≥1 extra provider round after the ack (§16 R6). On no-call it retries once with a firmer
/// instruction; on still-no-call or a semantically invalid plan it signals a SingleTurn fallback
/// rather than a degenerate 1-step Planned run (§16 R10). Provider usage is summed off the drained
/// <see cref="Finished"/> items and surfaced on <see cref="PlanResult.Usage"/> — on the degrade paths
/// too, where the rounds were still paid for — so the orchestrator accrues it run-level (I1).
/// <para>
/// Opt-in (<see cref="AppSettings.AgentPlanReasoningTurnEnabled"/>): on a provider whose handler drops the
/// configured reasoning effort as soon as tools are attached, <see cref="PlanAsync"/> first spends a
/// tool-FREE free-form reasoning turn — the only way such a plan turn reasons at anything but the model
/// default — and folds its analysis into the constrained turn. That turn can never hard-fail planning: any
/// failure, timeout or empty answer degrades to today's single constrained turn, and its tokens are summed
/// into <see cref="PlanResult.Usage"/> on every path, discarded analysis included (I1).
/// </para>
/// Sensitive plan text is logged only via <see cref="LoggingExtensions.SensitiveDebug"/>.
/// </summary>
public sealed class AgentPlanner : IAgentPlanner
{
    private readonly IAiClientService _ai;
    private readonly AiProviderHandlerResolver _handlers;
    private readonly ISettingsService _settings;
    private readonly ILogger<AgentPlanner> _logger;

    /// <summary>
    /// The assignable-persona roster source (Batch 07 D1/D2); null ⇒ no roster is ever listed and no step is
    /// ever assigned, i.e. the pre-Phase-3 plan prompt byte for byte.
    /// <para>
    /// A FACTORY, not an instance, and the reason is lifetime: <see cref="StepPersonaResolver"/> memoizes the
    /// roster for the life of the instance, but this planner is resolved ONCE into a window-lifetime
    /// <c>ChatSessionManager</c> scope on the interactive path. Holding one resolver would freeze the roster at
    /// the first plan of the session, so a user who configures the roster in Settings would see no specialists
    /// until the app restarted. A fresh resolver per plan is the correct grain: within one plan the roster is
    /// resolved once, which is what "the model can only be held to the list it was shown" needs.
    /// </para>
    /// </summary>
    private readonly Func<StepPersonaResolver>? _personas;

    private static readonly JsonSerializerOptions PlanJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Cap on the analysis text folded into the constrained turn. That turn sends exactly two messages and
    /// passes NO contextBudget (there is nothing to compact), so an unbounded analysis block could overflow
    /// a small local model's window and turn a WORKING plan turn into a failing one — the reliability
    /// regression this optimization is forbidden to cause.
    /// </summary>
    private const int MaxAnalysisChars = 4000;

    private static readonly AITool EmitPlanTool = AIFunctionFactory.Create(
        EmitPlanSchema, "emit_plan",
        "Emit the ordered plan of steps to accomplish the goal.");

    /// <param name="personas">The Batch 07 roster source, as a factory invoked once per plan (see
    /// <see cref="_personas"/> for why a factory); null ⇒ the roster is empty, the plan prompt carries no
    /// specialist block and every step is emitted unassigned. Trailing and defaulted on purpose: this type is
    /// constructed POSITIONALLY in its own tests, so a required parameter would force an edit into every one
    /// of them.</param>
    public AgentPlanner(
        IAiClientService ai,
        AiProviderHandlerResolver handlers,
        ISettingsService settings,
        ILogger<AgentPlanner> logger,
        Func<StepPersonaResolver>? personas = null)
    {
        _ai = ai;
        _handlers = handlers;
        _settings = settings;
        _logger = logger;
        _personas = personas;
    }

    [Description("Emit the ordered plan of steps to accomplish the goal.")]
    private static string EmitPlanSchema(
        [Description("The ordered steps, each with a short title, an intent, and an optional expected artifact.")]
        PlanStepArg[] steps) => "";

    /// <summary>
    /// One step in an <c>emit_plan</c> call. Title + Intent are required (§13.3); the two trailing members are
    /// Batch 07's and are optional in the schema as well as in C# — <c>AIFunctionFactory</c> generates the tool
    /// schema from this record, so a required member would make every plan turn carry them.
    /// </summary>
    public sealed record PlanStepArg(
        [property: Description("Short imperative title")] string Title,
        [property: Description("What this step should accomplish")] string Intent,
        [property: Description("The concrete artifact/result this step should produce")] string? ExpectedArtifact = null,
        // Matched by NAME against the roster the system message listed (07 D2). A name, not a Guid: models do
        // not reproduce GUIDs reliably and one mistyped nibble is an unresolvable id for a step the model DID
        // mean to assign. Not an index either: an off-by-one silently assigns the WRONG persona, whereas a
        // name mismatch fails closed to null, which is the run persona, which is today.
        [property: Description("Optional: the exact name of one of the listed specialists to run this step")] string? PersonaKey = null,
        // Steps sharing the same non-null value are declared independent of each other. Persisted into
        // AgentStep.ExtraJson and READ BACK by AgentRunOrchestrator.ParallelGroupOf since G10: a group of two or
        // more still-pending steps is delegated to sibling child runs and awaited (07 D11), so this number is
        // load-bearing, not a record of intent. A group of ONE is not a fan-out and runs in-process.
        [property: Description("Optional: steps that can run at the same time, independently, share one number")] int? ParallelGroup = null);

    private sealed record EmitPlanArgs(PlanStepArg[]? Steps);

    public async Task<PlanResult> PlanAsync(string goal, RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct)
    {
        // Optional free-form reasoning turn BEFORE the constrained one. It sends tools: null, so
        // AiClientService computes hasTools:false (SupportsToolCalling && tools is {Count:>0}) and the
        // handler sends the configured reasoning effort — on the handlers that drop effort under tools this
        // is the ONLY way a plan turn reasons at anything but the model default. Its tokens are part of the
        // plan's cost, so they are summed in on every path below (I1).
        var (analysis, usage) = await TryReasonAsync(goal, persona, provider, ct).ConfigureAwait(false);

        // Resolved ONCE per plan, before the turns, and reused for the prompt AND the name→id mapping — the
        // model can only be held to the list it was actually shown.
        var roster = await TryGetRosterAsync(ct).ConfigureAwait(false);

        var (steps, planUsage) = await TryCaptureAsync(BuildPlanMessages(goal, persona, firm: false, analysis, roster), provider, ct).ConfigureAwait(false);
        usage = AgentTurnUsage.Sum(usage, planUsage);

        if (steps is null)
        {
            // The firm retry REUSES the one analysis: the retry exists because the model wrote prose
            // instead of calling emit_plan, which a second reasoning turn would not fix and would pay for.
            var (retried, retryUsage) = await TryCaptureAsync(BuildPlanMessages(goal, persona, firm: true, analysis, roster), provider, ct).ConfigureAwait(false); // R10 retry once
            steps = retried;
            usage = AgentTurnUsage.Sum(usage, retryUsage); // I1: the retry's rounds were paid for too
        }

        if (steps is null || !ValidatePlan(steps, ctx.MaxSteps))
        {
            _logger.LogInformation("Planner degrade → SingleTurn fallback (no valid emit_plan).");
            return PlanResult.Fallback with { Usage = usage }; // still accrue the tokens spent
        }
        return new PlanResult(BuildSteps(steps, roster), false, usage);
    }

    public async Task<PlanResult> ReplanAsync(RunContext ctx, string? failure, Persona persona, AiProvider provider, CancellationToken ct)
    {
        // PLAN-ONLY: a replan keeps its SINGLE constrained turn even when the reason-then-emit toggle is on.
        // A replan already carries the completed-step summaries and the failure detail, so it has the context
        // a fresh reasoning turn would have to reconstruct; and it can run up to MaxReplans times per run, so
        // doubling ITS cost multiplies over the run instead of being paid once. Deliberate asymmetry, not an
        // oversight — revisit only with evidence that replans specifically plan worse.
        // The roster is threaded through the REPLAN too, and that is not symmetry for its own sake: a replan
        // REPLACES the remaining plan, so listing the specialists only on the first plan would make the first
        // failure silently strip every persona assignment for the rest of the run.
        var roster = await TryGetRosterAsync(ct).ConfigureAwait(false);

        var (steps, usage) = await TryCaptureAsync(BuildReplanMessages(ctx, failure, persona, firm: false, roster), provider, ct).ConfigureAwait(false);
        if (steps is null)
        {
            var (retried, retryUsage) = await TryCaptureAsync(BuildReplanMessages(ctx, failure, persona, firm: true, roster), provider, ct).ConfigureAwait(false);
            steps = retried;
            usage = AgentTurnUsage.Sum(usage, retryUsage);
        }

        if (steps is null || !ValidatePlan(steps, ctx.MaxSteps))
        {
            _logger.LogInformation("Replan degrade → fallback (no valid emit_plan).");
            return PlanResult.Fallback with { Usage = usage }; // still accrue the tokens spent
        }
        return new PlanResult(BuildSteps(steps, roster), false, usage);
    }

    /// <summary>
    /// The assignable roster, or EMPTY on any problem. Wrapped like <see cref="ShouldReasonFirstAsync"/>: this
    /// is the gate of an optional feature and must never be able to fail planning, so a settings-read fault or
    /// a persona-store fault degrades to today's plan (no specialist block, no assignments) rather than to no
    /// plan at all.
    /// <para>
    /// Stated honestly: <c>GetRosterAsync</c> already swallows its own faults and answers <c>[]</c>, so the
    /// catch below is currently UNREACHABLE by any fault this build can produce. It is kept as defence in
    /// depth around a public method of another type — it is what makes "the roster cannot fail a plan" true at
    /// THIS layer whatever the resolver later grows into — and the test that drives a settings fault therefore
    /// proves the outcome, not this arm. The warning carries the exception TYPE only: a persona store's message
    /// can embed a persona name, which is user-named content.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<Persona>> TryGetRosterAsync(CancellationToken ct)
    {
        if (_personas is null)
            return [];
        try
        {
            // A fresh resolver per plan (see the field's comment): its memo is per-instance by design, and this
            // planner can outlive many plans.
            return await _personas().GetRosterAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Plan persona roster could not be resolved ({Error}); planning without specialists.",
                ex.GetType().Name);
            return [];
        }
    }

    /// <summary>
    /// The optional first turn: a tool-FREE, free-form "think about how to decompose this goal" round whose
    /// text seeds the constrained turn. Returns (null, usage) — never throws for a provider problem — so
    /// planning degrades to today's single constrained turn. The usage is returned even when the text is
    /// discarded: the round was paid for (I1). Caller cancellation is NOT a degrade.
    /// </summary>
    private async Task<(string? Analysis, UsageDetails? Usage)> TryReasonAsync(
        string goal, Persona persona, AiProvider provider, CancellationToken ct)
    {
        if (!await ShouldReasonFirstAsync(provider, ct).ConfigureAwait(false))
            return (null, null);

        // Cost-aware: metadata only — provider TYPE, never the name, never the plan text.
        _logger.LogInformation(
            "Plan reason-then-emit is ON for {ProviderType}: this plan spends TWO provider turns "
            + "(free-form reasoning + constrained emit_plan), so the plan-turn cost is doubled.",
            provider.ProviderType);

        try
        {
            var response = await _ai.GetChatResponseAsync(
                BuildReasoningMessages(goal, persona), provider, tools: null, mode: null, ct).ConfigureAwait(false);

            var usage = response.Usage;          // paid for regardless of what came back
            var text = response.Text?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                _logger.LogInformation(
                    "Plan reasoning turn produced no text for {ProviderType}; using the single constrained turn.",
                    provider.ProviderType);
                return (null, usage);
            }

            if (text.Length > MaxAnalysisChars)
            {
                _logger.LogDebug("Plan reasoning analysis truncated: {Chars} → {Cap} chars.", text.Length, MaxAnalysisChars);
                text = text[..MaxAnalysisChars] + "\n… (analysis truncated)";
            }

            _logger.SensitiveDebug("Plan reasoning analysis ({Chars} chars): {Analysis}", text.Length, text);
            return (text, usage);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // cancellation is not a degrade — rethrow the original, don't rebuild it below
        }
        catch (Exception ex)
        {
            // The OPTIONAL turn must never be able to hard-fail planning. The warning carries the exception
            // TYPE only: LlmTimeoutException's message embeds the provider NAME, which is user-named and
            // therefore sensitive — the detail goes to SensitiveDebug.
            _logger.LogWarning(
                "Plan reasoning turn failed ({Error}) for {ProviderType}; using the single constrained turn.",
                ex.GetType().Name, provider.ProviderType);
            _logger.SensitiveDebug("Plan reasoning turn failure detail: {Detail}", ex.ToString());
            ct.ThrowIfCancellationRequested(); // a cancel that surfaced as a provider-shaped error is still a cancel
            return (null, null);               // the throw lost the usage; there is nothing to accrue
        }
    }

    /// <summary>
    /// The reason-then-emit gate, cheapest test first. <see cref="AiProvider.SupportsToolCalling"/> is in
    /// here because when it is false the constrained turn already gets <c>hasTools:false</c> (so the effort
    /// IS being sent) and <c>emit_plan</c> is never attached, so planning is heading for the SingleTurn
    /// degrade regardless — a reasoning turn buys nothing there.
    /// </summary>
    private async Task<bool> ShouldReasonFirstAsync(AiProvider provider, CancellationToken ct)
    {
        try
        {
            var settings = await _settings.GetSettingsAsync().ConfigureAwait(false);
            if (!settings.AgentPlanReasoningTurnEnabled) return false;
            if (!provider.SupportsToolCalling) return false;
            // Fully qualified: `using Microsoft.Extensions.AI;` also brings a ReasoningEffort into scope.
            if (provider.ReasoningEffort is null or Pia.Models.ReasoningEffort.None) return false;
            return _handlers.Get(provider.ProviderType).DropsReasoningEffortWithTools;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Evaluating the GATE of an optional optimization must never be able to fail planning:
            // GetSettingsAsync does I/O and AiProviderHandlerResolver.Get throws NotSupportedException for
            // an unregistered provider type. Either way the answer is "don't spend the extra turn".
            _logger.LogWarning("Plan reasoning-turn gate could not be evaluated ({Error}); planning single-turn.", ex.GetType().Name);
            return false;
        }
    }

    /// <summary>
    /// Runs one planning turn, capturing the final <c>emit_plan</c> args (last-write-wins) while
    /// draining the whole stream (R6); sums <see cref="Finished.Usage"/> across the drained items so
    /// the plan turn's ≥2 rounds reach the run ledger (I1 — they used to be discarded here).
    /// Returns (null, usage) when the model emitted no <c>emit_plan</c> call.
    /// </summary>
    private async Task<(PlanStepArg[]? Steps, UsageDetails? Usage)> TryCaptureAsync(
        List<ChatMessage> messages, AiProvider provider, CancellationToken ct)
    {
        PlanStepArg[]? captured = null;
        Func<FunctionCallContent, Task<object?>> toolHandler = call =>
        {
            if (string.Equals(call.Name, "emit_plan", StringComparison.Ordinal))
            {
                try
                {
                    var json = JsonSerializer.Serialize(call.Arguments ?? new Dictionary<string, object?>());
                    captured = JsonSerializer.Deserialize<EmitPlanArgs>(json, PlanJson)?.Steps; // last-write-wins
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse emit_plan arguments");
                }
                // Short ack — the tool loop appends this as a FunctionResult and does one more round (R6).
                return Task.FromResult<object?>("Plan received.");
            }
            return Task.FromResult<object?>("Only emit_plan is available here.");
        };

        UsageDetails? usage = null;
        await foreach (var item in _ai.GetChatCompletionWithToolsAsync(
            messages, provider, [EmitPlanTool], toolHandler, mode: null, ct).ConfigureAwait(false))
        {
            // Drain the whole stream; the plan itself is captured in the handler, but the USAGE only
            // ever surfaces on the yielded Finished items — mirror the verifier and keep it (I1).
            if (item is Finished { Usage: { } u })
                usage = AgentTurnUsage.Sum(usage, u);
        }

        if (captured is not null)
            _logger.SensitiveDebug("Planner captured {Count} step(s): {Titles}",
                captured.Length, string.Join(" | ", captured.Select(s => s.Title)));
        return (captured, usage);
    }

    /// <summary>Semantic validation (§13.3): non-empty; ≤ MaxSteps; every step has a title+intent; no duplicate titles.</summary>
    private static bool ValidatePlan(IReadOnlyList<PlanStepArg> steps, int maxSteps)
    {
        if (steps.Count == 0 || steps.Count > maxSteps) return false;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in steps)
        {
            if (s is null || string.IsNullOrWhiteSpace(s.Title) || string.IsNullOrWhiteSpace(s.Intent))
                return false;
            if (!seen.Add(s.Title.Trim()))
                return false;
        }
        return true;
    }

    private IReadOnlyList<AgentStep> BuildSteps(IReadOnlyList<PlanStepArg> steps, IReadOnlyList<Persona> roster)
    {
        var result = new List<AgentStep>(steps.Count);
        var dropped = 0;
        for (var i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            var assigned = MatchRoster(s.PersonaKey, roster, ref dropped);
            result.Add(new AgentStep
            {
                Id = Guid.Empty, // ReplaceStepsAsync assigns a fresh Id
                Ordinal = i,
                Title = s.Title.Trim(),
                Intent = s.Intent.Trim(),
                ExpectedArtifact = string.IsNullOrWhiteSpace(s.ExpectedArtifact) ? null : s.ExpectedArtifact!.Trim(),
                Status = AgentStepStatus.Pending,
                AssignedPersonaId = assigned,
                // Gated on the roster, like the assignment above, and for the D1 reason rather than tidiness:
                // AppendRoster is what tells the model about parallelGroup at all, so with no roster
                // configured a value here was never asked for. Recording it anyway would make a step row
                // differ from the pre-batch one on a build the user never opted into.
                ExtraJson = roster.Count == 0 ? null : SerializeExtras(s.ParallelGroup),
            });
        }
        if (dropped > 0)
        {
            // COUNT only. The key ECHOES a persona name, which is user-named content, so the key itself must
            // never reach the log (07 D2) — same shape as the launcher's dropped-policy-class count.
            _logger.LogInformation(
                "Plan assigned {DroppedCount} step(s) to an unknown persona; those steps use the run persona", dropped);
        }
        return result;
    }

    /// <summary>
    /// Maps a model-emitted persona key to a roster id, <c>OrdinalIgnoreCase</c> on the trimmed
    /// <see cref="Persona.Name"/>. A blank key is simply "unassigned" and is not counted as a miss; a non-blank
    /// key that matches nothing is counted, so the log can say HOW OFTEN without saying WHAT.
    /// <para>
    /// An unmatched key is deliberately NOT a plan defect — <see cref="ValidatePlan"/> does not see these
    /// members at all. Validating them would turn a cosmetic model slip into a SingleTurn degrade, i.e. throw
    /// away a perfectly good plan because one label was wrong.
    /// </para>
    /// </summary>
    private static Guid? MatchRoster(string? personaKey, IReadOnlyList<Persona> roster, ref int dropped)
    {
        if (string.IsNullOrWhiteSpace(personaKey) || roster.Count == 0)
            return null;

        var key = personaKey.Trim();
        foreach (var p in roster)
        {
            if (string.Equals(p.Name.Trim(), key, StringComparison.OrdinalIgnoreCase))
                return p.Id;
        }
        dropped++;
        return null;
    }

    /// <summary>
    /// The step's <c>ExtraJson</c> payload, or null when there is nothing to record. Serialized with
    /// <see cref="JsonSerializer"/> rather than interpolated: the value is model-authored, and building JSON by
    /// string concatenation is how a hostile value breaks the document.
    /// </summary>
    private static string? SerializeExtras(int? parallelGroup) =>
        parallelGroup is { } g ? JsonSerializer.Serialize(new StepExtras(g), PlanJson) : null;

    /// <summary>The <c>AgentStep.ExtraJson</c> document shape. One member so far.</summary>
    private sealed record StepExtras(int ParallelGroup);

    /// <summary>
    /// The reasoning turn's prompt. Deliberately says NOTHING about <c>emit_plan</c> or any output format:
    /// this turn exists to think, and no tool schema is even attached to it. The plan contract is imposed by
    /// the SECOND turn, which is the one that stays constrained and validated.
    /// </summary>
    private static List<ChatMessage> BuildReasoningMessages(string goal, Persona persona)
    {
        var sb = new StringBuilder();
        sb.AppendLine(persona.SystemPrompt);
        sb.AppendLine();
        sb.AppendLine("Before this goal is turned into an execution plan, think it through.");
        sb.AppendLine("Work out what accomplishing it actually requires: the sub-problems, the order they must happen in, what depends on what, what is still unknown, and the concrete deliverables that would show it is done.");
        sb.AppendLine("Answer with your analysis only — no tool calls, no JSON, no numbered final plan. Keep it short: a few paragraphs or bullets.");

        return new List<ChatMessage>
        {
            new(ChatRole.System, sb.ToString()),
            new(ChatRole.User, goal),
        };
    }

    /// <summary>
    /// Appends the assignable-specialist block, and appends NOTHING when the roster is empty — which is the
    /// whole opt-in (07 D1): with no roster configured, this method leaves the plan prompt byte-identical to
    /// the pre-Phase-3 one, so a user who has not opted in cannot get a different plan.
    /// <para>
    /// It goes on the SYSTEM message, never the user one. <c>TokenizingAiClientService</c> rewrites only
    /// <see cref="ChatRole.User"/> text to PII placeholders, and a roster is app-owned configuration rather
    /// than user turn text — the same reasoning that puts the reasoning analysis on the user message puts this
    /// one on the system message.
    /// </para>
    /// </summary>
    private static void AppendRoster(StringBuilder sb, IReadOnlyList<Persona> roster)
    {
        if (roster.Count == 0)
            return;

        sb.AppendLine("You may assign each step to one of these specialists by setting personaKey to its exact name.");
        sb.AppendLine("Leave personaKey out to use the default assistant.");
        sb.AppendLine("Available:");
        foreach (var p in roster)
            sb.AppendLine($"  {p.Name} — {Describe(p)}");
        sb.AppendLine("Steps that can run at the same time, independently of each other, may share the same parallelGroup number. Leave parallelGroup out unless the steps are genuinely independent.");
    }

    /// <summary>One roster line's descriptor: the tagline, else the first three expertise tags, else nothing.</summary>
    private static string Describe(Persona p) =>
        !string.IsNullOrWhiteSpace(p.Tagline) ? p.Tagline!.Trim()
        : p.Expertise.Count > 0 ? string.Join(", ", p.Expertise.Take(3))
        : "general assistant";

    private static List<ChatMessage> BuildPlanMessages(
        string goal, Persona persona, bool firm, string? analysis, IReadOnlyList<Persona> roster)
    {
        var sb = new StringBuilder();
        sb.AppendLine(persona.SystemPrompt);
        sb.AppendLine();
        sb.AppendLine("You are decomposing the user's goal into an ordered, minimal plan of concrete steps.");
        sb.AppendLine("Call the emit_plan tool exactly once with the ordered steps. Each step needs a short title and an intent (what it accomplishes); include an expectedArtifact when there is a concrete deliverable.");
        sb.AppendLine("Keep the plan tight — only the steps genuinely needed to accomplish the goal.");
        AppendRoster(sb, roster);
        if (firm)
            sb.AppendLine("You did not call emit_plan. You MUST respond by calling the emit_plan tool now — do not write prose.");

        // The analysis rides on the USER message, never on the System prompt:
        // TokenizingAiClientService.TokenizeMessages rewrites ONLY ChatRole.User text to PII placeholders,
        // and this analysis came back DETOKENIZED from the reasoning turn — in the System prompt it would
        // ship restored PII straight past the tokenizer. Folding it into the single user message (rather
        // than appending a second one) also keeps the request shape exactly [System, User], so no provider
        // meets a shape this path has not sent before.
        var user = analysis is null
            ? goal
            : $"{goal}\n\n--- Your analysis of this goal (use it; do not restate it) ---\n{analysis}\n--- end of analysis ---";

        return new List<ChatMessage>
        {
            new(ChatRole.System, sb.ToString()),
            new(ChatRole.User, user),
        };
    }

    private static List<ChatMessage> BuildReplanMessages(
        RunContext ctx, string? failure, Persona persona, bool firm, IReadOnlyList<Persona> roster)
    {
        var sb = new StringBuilder();
        sb.AppendLine(persona.SystemPrompt);
        sb.AppendLine();
        sb.AppendLine("A step in the current plan failed. Revise the REMAINING plan to recover and still accomplish the goal.");
        if (ctx.CompletedSteps.Count > 0)
        {
            sb.AppendLine("Completed so far (do NOT repeat these steps):");
            foreach (var c in ctx.CompletedSteps)
            {
                sb.AppendLine($"- [{(c.Succeeded ? "ok" : "failed")}] {c.Title}: {c.Intent}");
                if (c.FromEarlierSegment) // E2: seeded pre-pause step — it ran, its text is just not here
                    sb.AppendLine($"    {CompletedStepSummary.EarlierSegmentNote}");
            }
        }
        if (!string.IsNullOrWhiteSpace(failure))
            sb.AppendLine($"Failure detail: {failure}");
        sb.AppendLine("Call emit_plan with the revised ordered steps (only the steps still needed).");
        AppendRoster(sb, roster);
        if (firm)
            sb.AppendLine("You MUST call the emit_plan tool now — do not write prose.");

        return new List<ChatMessage>
        {
            new(ChatRole.System, sb.ToString()),
            new(ChatRole.User, ctx.Goal),
        };
    }
}
