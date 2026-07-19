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
/// Terminal critic (§13.x). Judges goal-achievement via a single constrained <c>emit_verdict</c>
/// turn, draining the whole stream (R6-style). On no-call it retries once (firmer); on still-no-call
/// it degrades to ACCEPT rather than wedging an otherwise-successful run. Sums provider usage from
/// the yielded <see cref="Finished"/> items so the orchestrator can accrue it run-level. Verdict
/// text (reason/missing) is SENSITIVE — logged only via <see cref="LoggingExtensions.SensitiveDebug"/>.
/// </summary>
public sealed class AgentVerifier : IAgentVerifier
{
    private readonly IAiClientService _ai;
    private readonly ILogger<AgentVerifier> _logger;
    private static readonly JsonSerializerOptions VerdictJson = new(JsonSerializerDefaults.Web);

    private static readonly AITool EmitVerdictTool = AIFunctionFactory.Create(
        EmitVerdictSchema, "emit_verdict",
        "Emit the verdict on whether the completed run achieved its goal.");

    public AgentVerifier(IAiClientService ai, ILogger<AgentVerifier> logger)
    {
        _ai = ai;
        _logger = logger;
    }

    [Description("Emit the verdict on whether the completed run achieved its goal.")]
    private static string EmitVerdictSchema(
        [Description("True only if the run genuinely achieved the goal and produced the expected artifacts.")]
        bool passed,
        [Description("A short justification for the verdict.")]
        string reason,
        [Description("The concrete goals or artifacts still missing when passed is false; empty when passed is true.")]
        string[] missing) => "";

    private sealed record EmitVerdictArgs(bool Passed, string? Reason, string[]? Missing);

    public async Task<VerdictResult> VerifyAsync(RunContext ctx, Persona persona, AiProvider provider, CancellationToken ct)
    {
        var (args, usage) = await TryCaptureAsync(BuildVerifyMessages(ctx, persona, firm: false), provider, ct).ConfigureAwait(false);
        if (args is null)
        {
            var (args2, usage2) = await TryCaptureAsync(BuildVerifyMessages(ctx, persona, firm: true), provider, ct).ConfigureAwait(false); // retry once
            args = args2;
            usage = SumUsage(usage, usage2);
        }

        if (args is null)
        {
            _logger.LogInformation("Verifier degrade → accept (no valid emit_verdict).");
            return VerdictResult.Accept with { Usage = usage }; // still accrue the tokens spent
        }

        var reason = args.Reason?.Trim() ?? string.Empty;
        var missing = (args.Missing ?? Array.Empty<string>())
            .Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()).ToArray();

        _logger.SensitiveDebug("Verifier verdict passed={Passed} reason={Reason} missing={Missing}",
            args.Passed, reason, string.Join(" | ", missing));

        return new VerdictResult(args.Passed, reason, missing, usage);
    }

    /// <summary>
    /// Runs one verify turn, capturing the final <c>emit_verdict</c> args (last-write-wins) while
    /// draining the whole stream; sums <see cref="Finished.Usage"/> across the drained items (the
    /// planner discards these — the verifier must not). Returns (null, usage) when no verdict emitted.
    /// </summary>
    private async Task<(EmitVerdictArgs? Args, UsageDetails? Usage)> TryCaptureAsync(
        List<ChatMessage> messages, AiProvider provider, CancellationToken ct)
    {
        EmitVerdictArgs? captured = null;
        Func<FunctionCallContent, Task<object?>> toolHandler = call =>
        {
            if (string.Equals(call.Name, "emit_verdict", StringComparison.Ordinal))
            {
                try
                {
                    var json = JsonSerializer.Serialize(call.Arguments ?? new Dictionary<string, object?>());
                    captured = JsonSerializer.Deserialize<EmitVerdictArgs>(json, VerdictJson); // last-write-wins
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse emit_verdict arguments");
                }
                return Task.FromResult<object?>("Verdict received.");
            }
            return Task.FromResult<object?>("Only emit_verdict is available here.");
        };

        UsageDetails? usage = null;
        await foreach (var item in _ai.GetChatCompletionWithToolsAsync(
            messages, provider, [EmitVerdictTool], toolHandler, mode: null, ct).ConfigureAwait(false))
        {
            if (item is Finished { Usage: { } u })
                usage = SumUsage(usage, u); // capture usage from the yielded stream (unlike the planner)
        }

        return (captured, usage);
    }

    private static UsageDetails? SumUsage(UsageDetails? a, UsageDetails? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return new UsageDetails
        {
            InputTokenCount = (a.InputTokenCount ?? 0) + (b.InputTokenCount ?? 0),
            OutputTokenCount = (a.OutputTokenCount ?? 0) + (b.OutputTokenCount ?? 0),
        };
    }

    private static List<ChatMessage> BuildVerifyMessages(RunContext ctx, Persona persona, bool firm)
    {
        var sb = new StringBuilder();
        sb.AppendLine(persona.SystemPrompt);
        sb.AppendLine();
        sb.AppendLine("The run below has finished executing its plan. Judge whether it actually achieved the user's goal and produced the expected artifacts.");
        sb.AppendLine("Call the emit_verdict tool exactly once: passed=true ONLY if the goal is genuinely satisfied; otherwise passed=false with a short reason and the concrete missing items.");
        if (ctx.CompletedSteps.Count > 0)
        {
            sb.AppendLine("Steps executed (with their results):");
            foreach (var c in ctx.CompletedSteps)
            {
                sb.AppendLine($"- [{(c.Succeeded ? "ok" : "failed")}] {c.Title}: {c.Intent}");
                if (!string.IsNullOrWhiteSpace(c.VisibleText))
                    sb.AppendLine($"    result: {c.VisibleText}");
            }
        }
        if (firm)
            sb.AppendLine("You did not call emit_verdict. You MUST respond by calling the emit_verdict tool now — do not write prose.");

        return new List<ChatMessage>
        {
            new(ChatRole.System, sb.ToString()),
            new(ChatRole.User, ctx.Goal),
        };
    }
}
