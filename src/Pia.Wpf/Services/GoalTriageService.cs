using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pia.Logging;
using Pia.Models;
using Pia.Services.Interfaces;

namespace Pia.Services;

/// <summary>
/// One tool-less classification turn in front of the plan turn: a goal that needs no plan is answered as an
/// ordinary chat turn instead of paying plan → step → verify for it.
/// <para>
/// Asymmetric by construction. Answering a real multi-step goal in one chat turn is a worse failure than
/// planning a question, so every unclear reply, every timeout and every fault resolves to
/// <see cref="GoalTriageVerdict.NeedsPlan"/> — only the exact word <c>ANSWER</c> downgrades.
/// </para>
/// </summary>
public sealed class GoalTriageService : IGoalTriageService
{
    private readonly IAiClientService _ai;
    private readonly ILogger<GoalTriageService> _logger;

    /// <summary>Above this the classification costs more than the plan turn it is meant to avoid.</summary>
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(6);

    private const string AnswerToken = "ANSWER";

    private const string Instructions = """
        You decide how an assistant should handle the user's message. Answer with exactly one word.

        Answer PLAN when the message asks for work: several things in sequence, producing or changing
        files, gathering information from multiple places, or anything where the assistant should decide
        on an approach before acting.

        Answer ANSWER when a single reply settles it: a question, a lookup, a request to explain,
        summarize, translate or reword something, a request about material the user already has, or
        small talk. A message that names something the user already has — a memory, a note, a file, a
        chat — with a marker like @Memory:Something or @File:something is a reference to existing
        material, not a task, and is usually ANSWER.

        When you are not sure, answer PLAN.

        Reply with the single word PLAN or ANSWER. Write nothing else.
        """;

    public GoalTriageService(IAiClientService ai, ILogger<GoalTriageService> logger)
    {
        _ai = ai;
        _logger = logger;
    }

    public async Task<GoalTriageVerdict> ClassifyAsync(
        string goal, AiProvider provider, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(goal))
            return GoalTriageVerdict.NeedsPlan;

        var sw = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Ceiling);

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, Instructions),
                // The goal rides the USER role: TokenizeMessages rewrites that role only, so a goal placed
                // in the system prompt would reach the provider untokenized.
                new(ChatRole.User, goal),
            };

            var response = await _ai.GetChatResponseAsync(
                messages, provider, tools: null, mode: AgentTurnRouting.Mode,
                personaModelType: AgentTurnRouting.ModelType, cancellationToken: timeout.Token)
                .ConfigureAwait(false);

            // The tokens are not accrued anywhere: on a downgrade no run row exists to bill, and on
            // NeedsPlan the orchestrator only ever sees the planner's own usage.
            var verdict = Parse(response.Text);
            _logger.LogInformation("Goal triage → {Verdict} in {ElapsedMs}ms", verdict, sw.ElapsedMilliseconds);
            _logger.SensitiveDebug("Goal triage goal: {Goal}", goal);
            return verdict;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the user's own cancel, not the ceiling
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Goal triage timed out after {ElapsedMs}ms; planning as usual", sw.ElapsedMilliseconds);
            return GoalTriageVerdict.NeedsPlan;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Goal triage failed after {ElapsedMs}ms; planning as usual", sw.ElapsedMilliseconds);
            return GoalTriageVerdict.NeedsPlan;
        }
    }

    // Whole-response equality, not Contains: "ANSWER — but first…" is a model that did not follow the one-word
    // instruction, and reading a downgrade out of it is the expensive direction to be wrong in.
    private static GoalTriageVerdict Parse(string? text) =>
        string.Equals(text?.Trim(), AnswerToken, StringComparison.OrdinalIgnoreCase)
            ? GoalTriageVerdict.AnswerDirectly
            : GoalTriageVerdict.NeedsPlan;
}
