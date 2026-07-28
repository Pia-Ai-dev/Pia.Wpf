using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Logging;
using Pia.Models;
using Pia.Services.Interfaces;

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
/// Sensitive plan text is logged only via <see cref="LoggingExtensions.SensitiveDebug"/>.
/// </summary>
public sealed class AgentPlanner : IAgentPlanner
{
    private readonly IAiClientService _ai;
    private readonly ILogger<AgentPlanner> _logger;
    private static readonly JsonSerializerOptions PlanJson = new(JsonSerializerDefaults.Web);

    private static readonly AITool EmitPlanTool = AIFunctionFactory.Create(
        EmitPlanSchema, "emit_plan",
        "Emit the ordered plan of steps to accomplish the goal.");

    public AgentPlanner(IAiClientService ai, ILogger<AgentPlanner> logger)
    {
        _ai = ai;
        _logger = logger;
    }

    [Description("Emit the ordered plan of steps to accomplish the goal.")]
    private static string EmitPlanSchema(
        [Description("The ordered steps, each with a short title, an intent, and an optional expected artifact.")]
        PlanStepArg[] steps) => "";

    /// <summary>One step in an <c>emit_plan</c> call. Title + Intent are required (§13.3).</summary>
    public sealed record PlanStepArg(
        [property: Description("Short imperative title")] string Title,
        [property: Description("What this step should accomplish")] string Intent,
        [property: Description("The concrete artifact/result this step should produce")] string? ExpectedArtifact = null);

    private sealed record EmitPlanArgs(PlanStepArg[]? Steps);

    public async Task<PlanResult> PlanAsync(string goal, RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct)
    {
        var (steps, usage) = await TryCaptureAsync(BuildPlanMessages(goal, persona, firm: false), provider, ct).ConfigureAwait(false);
        if (steps is null)
        {
            var (retried, retryUsage) = await TryCaptureAsync(BuildPlanMessages(goal, persona, firm: true), provider, ct).ConfigureAwait(false); // R10 retry once
            steps = retried;
            usage = AgentTurnUsage.Sum(usage, retryUsage); // I1: the retry's rounds were paid for too
        }

        if (steps is null || !ValidatePlan(steps, ctx.MaxSteps))
        {
            _logger.LogInformation("Planner degrade → SingleTurn fallback (no valid emit_plan).");
            return PlanResult.Fallback with { Usage = usage }; // still accrue the tokens spent
        }
        return new PlanResult(BuildSteps(steps), false, usage);
    }

    public async Task<PlanResult> ReplanAsync(RunContext ctx, string? failure, Persona persona, AiProvider provider, CancellationToken ct)
    {
        var (steps, usage) = await TryCaptureAsync(BuildReplanMessages(ctx, failure, persona, firm: false), provider, ct).ConfigureAwait(false);
        if (steps is null)
        {
            var (retried, retryUsage) = await TryCaptureAsync(BuildReplanMessages(ctx, failure, persona, firm: true), provider, ct).ConfigureAwait(false);
            steps = retried;
            usage = AgentTurnUsage.Sum(usage, retryUsage);
        }

        if (steps is null || !ValidatePlan(steps, ctx.MaxSteps))
        {
            _logger.LogInformation("Replan degrade → fallback (no valid emit_plan).");
            return PlanResult.Fallback with { Usage = usage }; // still accrue the tokens spent
        }
        return new PlanResult(BuildSteps(steps), false, usage);
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

    private static IReadOnlyList<AgentStep> BuildSteps(IReadOnlyList<PlanStepArg> steps)
    {
        var result = new List<AgentStep>(steps.Count);
        for (var i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            result.Add(new AgentStep
            {
                Id = Guid.Empty, // ReplaceStepsAsync assigns a fresh Id
                Ordinal = i,
                Title = s.Title.Trim(),
                Intent = s.Intent.Trim(),
                ExpectedArtifact = string.IsNullOrWhiteSpace(s.ExpectedArtifact) ? null : s.ExpectedArtifact!.Trim(),
                Status = AgentStepStatus.Pending,
                AssignedPersonaId = null,
            });
        }
        return result;
    }

    private static List<ChatMessage> BuildPlanMessages(string goal, Persona persona, bool firm)
    {
        var sb = new StringBuilder();
        sb.AppendLine(persona.SystemPrompt);
        sb.AppendLine();
        sb.AppendLine("You are decomposing the user's goal into an ordered, minimal plan of concrete steps.");
        sb.AppendLine("Call the emit_plan tool exactly once with the ordered steps. Each step needs a short title and an intent (what it accomplishes); include an expectedArtifact when there is a concrete deliverable.");
        sb.AppendLine("Keep the plan tight — only the steps genuinely needed to accomplish the goal.");
        if (firm)
            sb.AppendLine("You did not call emit_plan. You MUST respond by calling the emit_plan tool now — do not write prose.");

        return new List<ChatMessage>
        {
            new(ChatRole.System, sb.ToString()),
            new(ChatRole.User, goal),
        };
    }

    private static List<ChatMessage> BuildReplanMessages(RunContext ctx, string? failure, Persona persona, bool firm)
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
        if (firm)
            sb.AppendLine("You MUST call the emit_plan tool now — do not write prose.");

        return new List<ChatMessage>
        {
            new(ChatRole.System, sb.ToString()),
            new(ChatRole.User, ctx.Goal),
        };
    }
}
